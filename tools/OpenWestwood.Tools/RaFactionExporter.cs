using System.Globalization;
using System.Text;
using OpenWestwood.Core.Rules;
using OpenWestwood.TiberianDawn;
using OpenWestwood.TiberianDawn.Logic;
using OpenWestwood.TiberianDawn.Logic.Types;

namespace OpenWestwood.Tools;

/// <summary>
/// export-ra-factions: turns Red Alert's Allies and Soviets into Zero Hour engine factions (mods/zh/rules/ra/),
/// as export-td-factions does for Tiberian Dawn. The objects' art and layout come from RA's type tables
/// (mods/redalert/rules, imported from EA's *DATA.CPP); their numbers from RULES.INI (with AFTRMATH.INI on
/// top), as RA reads them (TechnoTypeClass::Read_INI, WeaponTypeClass, WarheadTypeClass, BulletTypeClass).
///
/// Conversions (one RA cell = 36 world units; RA runs 15 ticks a second):
///   speed      INI 0..100 is v*256/100 leptons a tick (CCINIClass::Get_MPHType), x 15 x 36/256
///   range      cells x 36;  sight: cells x 36
///   fire rate  ticks x 1000/15 ms
///   warheads   to ZH damage types; RA's five armour classes become ZH armour sets from each warhead's Verses.
/// </summary>
public static class RaFactionExporter
{
    const float CellUnits = 36f;
    const float SizeUnits = CellUnits;           // objects at their original size, a whole cell (as TD's: TdScale.Size)
    const float LeptonUnits = CellUnits / 256f;
    const float TicksPerSecond = 15f;
    const float InfantrySpeedFactor = 2f;        // soldiers at Zero Hour's pace, as TD's (before TD's own +15%)
    // Multiplayer's top setting, 10, is tech level 13 (SCENARIO.CPP _build_tech): everything but the campaign-only.
    const int BuildLevel = 13;

    sealed record Side(string Faction, string Prefix, string SideName, string BaseSide, string[] Owners, string DisplayName, string Color, string Score, string Logo);

    static readonly Side[] Sides =
    [
        new("FactionRAAllies", "RAALL_", "RAAllies", "USA", ["allies", "greece", "england", "france", "germany", "spain", "turkey", "goodguy"],
            "Allies (Red Alert)", "R:90 G:150 B:255", "America_ScoreScreen", "SAFactionLogoPage_US"),
        new("FactionRASoviet", "RASOV_", "RASoviet", "China", ["soviet", "ussr", "ukraine", "russia", "badguy"],
            "Soviets (Red Alert)", "R:220 G:30 B:30", "China_ScoreScreen", "SNFactionLogoPage_China"),
    ];

    // RA warheads -> the ZH damage types they act as (each its own, so the armour tables stay RA's).
    static readonly Dictionary<string, string> DamageTypeOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SA"] = "SMALL_ARMS", ["HE"] = "EXPLOSION", ["AP"] = "ARMOR_PIERCING", ["Fire"] = "FLAME", ["HollowPoint"] = "SNIPER",
        ["Super"] = "PARTICLE_BEAM", ["Organic"] = "MELEE", ["Nuke"] = "RADIATION", ["Mechanical"] = "HEALING",
    };

    // RA armour (RULES.INI "Armor=", the Verses columns in order) -> (ZH armour set, the ZH set it starts from).
    static readonly (string RaName, string Name, string Base)[] Armors =
    [
        ("none", "RA_ArmorNone", "HumanArmor"),
        ("wood", "RA_ArmorWood", "StructureArmor"),
        ("light", "RA_ArmorLight", "TruckArmor"),
        ("heavy", "RA_ArmorHeavy", "TankArmor"),
        ("concrete", "RA_ArmorConcrete", "StructureArmor"),
    ];

    // Structures that stand in for others in prerequisites (HouseClass::Can_Build: advanced power counts as power;
    // in multiplayer either tech centre counts as both).
    static readonly Dictionary<string, string[]> Equivalents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powr"] = ["powr", "apwr"], ["stek"] = ["stek", "atek"], ["atek"] = ["atek", "stek"],
    };

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>INI files as sections of keys (case-insensitive; ";" comments).</summary>
    sealed class Ini
    {
        readonly Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);

        public void Read(string text)
        {
            Dictionary<string, string>? current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Split(';')[0].Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string name = line[1..^1].Trim();
                    if (!sections.TryGetValue(name, out current))
                        sections[name] = current = new(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (current != null && eq > 0)
                    current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        public bool Has(string section) => sections.ContainsKey(section);
        public string Get(string section, string key, string fallback = "") =>
            sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;
        public float Num(string section, string key, float fallback = 0)
        {
            string v = Get(section, key).TrimEnd('%');
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }
        public bool Bool(string section, string key, bool fallback = false) =>
            Get(section, key) is { Length: > 0 } v ? v[0] is 'y' or 'Y' or 't' or 'T' or '1' : fallback;
        public string[] List(string section, string key) =>
            Get(section, key).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static int Run(string root)
    {
        using var ra = TdContent.Load(root, "redalert");
        var types = new TdTypes(ra.Rules);
        var ini = new Ini();
        ini.Read(Encoding.Latin1.GetString(ra.Files.ReadAllBytes("RULES.INI")));
        // Then AFTRMATH.INI, and MPLAYER.INI for multiplayer games (SCENARIO.CPP), as skirmish is.
        foreach (string overlay in new[] { "AFTRMATH.INI", "MPLAYER.INI" })
            if (ra.Files.Exists(overlay))
                ini.Read(Encoding.Latin1.GetString(ra.Files.ReadAllBytes(overlay)));
        var text = ra.Files.Exists("CONQUER.ENG") ? ReadStrings(ra.Files.ReadAllBytes("CONQUER.ENG")) : [];
        string output = Path.Combine(root, "mods", "zh", "rules", "ra");
        Directory.CreateDirectory(output);
        var zhArmor = RuleYaml.Parse(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "armor.yaml")), "armor.yaml")["Armor"]!;
        // RA's animations by their enum names (the explosion lists name them so).
        var animByEnum = ra.Rules.Section("Animations").Entries.ToDictionary(e => e.Value.GetString("Type"), e => e.Value.GetString("IniName"), StringComparer.OrdinalIgnoreCase);
        string AnimName(string enumName) => animByEnum.GetValueOrDefault(enumName, "");

        var objects = new StringBuilder("Objects:\n");
        var weapons = new StringBuilder("Weapons:\n");
        var projectiles = new StringBuilder("Objects:\n");
        var locomotors = new StringBuilder("Locomotors:\n");
        var commandSets = new StringBuilder("CommandSets:\n");
        var buttons = new StringBuilder("CommandButtons:\n");
        var armor = new StringBuilder("Armor:\n");
        var factions = new StringBuilder("PlayerTemplates:\n");
        var sounds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var powers = new StringBuilder("SpecialPowers:\n");
        var ocls = new StringBuilder("ObjectCreationLists:\n");
        buttons.Append("  Command_RaRepair:\n    Command: TD_REPAIR\n    TextLabel: Repair\n    ButtonImage: SSRepair\n    ButtonBorderType: ACTION\n    DescriptLabel: Repair this building (costs money as it mends)\n");
        commandSets.Append("  RATransportCommandSet:\n    1: Command_TransportExit\n    2: Command_TransportExit\n    3: Command_TransportExit\n    4: Command_TransportExit\n    5: Command_TransportExit\n    9: Command_Evacuate\n    11: Command_AttackMove\n    13: Command_Guard\n    14: Command_Stop\n");

        // ---- Armour: each RA armour class takes every warhead's Verses column ----
        var warheadNames = DamageTypeOf.Keys.Where(ini.Has).ToList();
        for (int column = 0; column < Armors.Length; column++)
        {
            var (_, name, baseName) = Armors[column];
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (zhArmor.TryGet(baseName, out var b) && b.TryGet("Armor", out var list))
                foreach (var e in list.AsList())
                    if (RuleValue.Words(e.Value) is [var dt, var pct, ..])
                        entries[dt] = pct;
            foreach (string warhead in warheadNames)
            {
                var verses = ini.List(warhead, "Verses");
                if (verses.Length > column && DamageTypeOf[warhead] != "HEALING")
                    entries[DamageTypeOf[warhead]] = verses[column].EndsWith('%') ? verses[column] : $"{verses[column]}%";
            }
            armor.Append($"  {name}:\n    Armor:\n");
            foreach (var (dt, pct) in entries)
                armor.Append($"    - {dt} {pct}\n");
        }
        string ArmorName(string raArmor) => Armors.FirstOrDefault(a => a.RaName.Equals(raArmor, StringComparison.OrdinalIgnoreCase)).Name ?? "RA_ArmorNone";

        // ---- Weapons, as RULES.INI has them (shared by both sides) ----
        var weaponNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Weapon(string raName)
        {
            if (raName.Length == 0 || !ini.Has(raName))
                return null;
            if (weaponNames.TryGetValue(raName, out var done))
                return done;
            string name = "RA_Weapon_" + raName.Replace('-', '_');
            weaponNames[raName] = name;
            string warhead = ini.Get(raName, "Warhead");
            string projectile = ini.Get(raName, "Projectile");
            float damage = ini.Num(raName, "Damage");
            string dt = damage < 0 ? "HEALING" : DamageTypeOf.GetValueOrDefault(warhead, "EXPLOSION");
            float spread = ini.Num(warhead, "Spread", 1);
            float radius = Math.Clamp(spread * LeptonUnits * 8, 3, 27); // Spread is a falloff in sixteenths of a cell
            float range = ini.Num(raName, "Range", 1) * CellUnits;
            int burst = (int)Math.Max(1, ini.Num(raName, "Burst", 1));
            float rof = Math.Max(ini.Num(raName, "ROF", 15), 1) * 1000 / TicksPerSecond;
            weapons.Append($"  {name}:\n    PrimaryDamage: {F(Math.Abs(damage))}\n    PrimaryDamageRadius: {F(radius)}\n");
            if (spread >= 6)
                weapons.Append($"    SecondaryDamage: {F(Math.Abs(damage) / 2)}\n    SecondaryDamageRadius: {F(Math.Min(radius * 2, CellUnits * 1.5f))}\n");
            weapons.Append($"    AttackRange: {F(Math.Max(range, 15))}\n    DamageType: {dt}\n");
            weapons.Append($"    DeathType: {(dt == "FLAME" ? "BURNED" : dt is "EXPLOSION" or "ARMOR_PIERCING" ? "EXPLODED" : dt == "PARTICLE_BEAM" ? "LASERED" : "NORMAL")}\n");
            if (burst > 1)
                weapons.Append($"    ClipSize: {burst}\n    ClipReloadTime: {(int)rof}\n    DelayBetweenShots: 200\n");
            else
                weapons.Append($"    DelayBetweenShots: {(int)rof}\n    ClipSize: 0\n    ClipReloadTime: 0\n");
            weapons.Append(dt == "HEALING" ? "    RadiusDamageAffects: ALLIES SELF\n" : "    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n");
            bool antiAir = ini.Bool(projectile, "AA"), antiGround = ini.Bool(projectile, "AG", true);
            weapons.Append($"    AntiGround: {(antiGround ? "Yes" : "No")}\n    AntiAirborneVehicle: {(antiAir ? "Yes" : "No")}\n    AntiAirborneInfantry: {(antiAir ? "Yes" : "No")}\n");
            // Its sound (Report=), if the installed files have it (a few Aftermath sounds are on its own disc).
            if (ini.Get(raName, "Report") is { Length: > 0 } report && ra.Files.Exists(report + ".AUD"))
            {
                weapons.Append($"    FireSound: RaSnd_{report.ToUpperInvariant()}\n");
                sounds.Add(report.ToUpperInvariant());
            }
            // The muzzle flash (Anim=) and the explosion (Combat_Anim of the warhead's Explosion set).
            string muzzle = ini.Get(raName, "Anim");
            // Electric weapons (Charges=yes: the Tesla Coil, Tesla Tank, Volkov's arm) draw a bolt (TECHNO.CPP Electric_Zap).
            if (ini.Bool(raName, "Charges"))
                muzzle = "ZAP";
            string blast = Explosion((int)ini.Num(warhead, "Explosion"), Math.Abs(damage));
            string image = ini.Get(projectile, "Image");
            bool visible = image.Length > 0 && !image.Equals("none", StringComparison.OrdinalIgnoreCase);
            if (!visible)
            {
                if (muzzle.Length > 0 || blast.Length > 0)
                    weapons.Append($"    FireFX: RAFX:{muzzle}|{blast}\n");
            }
            else
            {
                if (muzzle.Length > 0)
                    weapons.Append($"    FireFX: RAFX:{muzzle}\n");
                string proj = $"RA_Projectile_{projectile}_{image}".Replace('-', '_');
                float speed = Math.Max(ini.Num(raName, "Speed", 40) * 256 / 100f * TicksPerSecond * LeptonUnits, 60);
                weapons.Append($"    ProjectileObject: {proj}\n    WeaponSpeed: {F(speed)}\n");
                if (blast.Length > 0)
                    weapons.Append($"    ProjectileDetonationFX: RAFX:{blast}\n");
                if (projectileNames.Add(proj))
                {
                    bool homing = ini.Num(projectile, "ROT") > 0;
                    projectiles.Append($"  {proj}:\n    DisplayName: RA projectile\n    EditorSorting: SYSTEM\n    VisionRange: 0.0\n");
                    projectiles.Append(homing ? "    KindOf: PROJECTILE SMALL_MISSILE\n" : "    KindOf: PROJECTILE\n");
                    if (homing)
                        projectiles.Append($"    Locomotor: SET_NORMAL {proj}Locomotor\n");
                    projectiles.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    ArmorSet:\n      Armor: ProjectileArmor\n    Modules:\n");
                    projectiles.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RASpriteDraw\n        Shape: {image}\n        TdKind: Bullet\n        TdType: {projectile}\n");
                    projectiles.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
                    projectiles.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                    if (homing)
                    {
                        projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: MissileAIUpdate\n        TryToFollowTarget: Yes\n        FuelLifetime: 10000\n        InitialVelocity: {F(speed)}\n        IgnitionDelay: 0\n        DistanceToTravelBeforeTurning: 5\n        DistanceToTargetForLock: 60\n");
                        locomotors.Append($"  {proj}Locomotor:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    MinSpeed: 30\n    Acceleration: 400\n    Braking: 0\n    TurnRate: 180\n    MaxThrustAngle: 30\n    AllowAirborneMotiveForce: Yes\n    Appearance: THRUST\n    CloseEnoughDist: 1\n");
                    }
                    else
                    {
                        float arc = ini.Bool(projectile, "High") ? 60 : 5;
                        projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n        FirstHeight: {F(arc)}\n        SecondHeight: {F(arc)}\n        FirstPercentIndent: 30%\n        SecondPercentIndent: 70%\n        FlightPathAdjustDistPerSecond: 100\n");
                    }
                    projectiles.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
                }
            }
            return name;
        }

        // COMBAT.CPP Combat_Anim on land: the explosion set, then by damage within its list.
        string Explosion(int set, float damage)
        {
            string Pick(string[] list, float cap) => AnimName(list[(int)((list.Length - 1) * Math.Min(damage, cap) / cap)]);
            return set switch
            {
                1 => AnimName("ANIM_PIFF"),
                2 => AnimName(damage > 15 ? "ANIM_PIFFPIFF" : "ANIM_PIFF"),
                3 => Pick(["ANIM_NAPALM1", "ANIM_NAPALM2", "ANIM_NAPALM3"], 150),
                4 => Pick(["ANIM_VEH_HIT3", "ANIM_VEH_HIT2", "ANIM_FRAG1", "ANIM_FBALL1"], 90),
                5 => Pick(["ANIM_VEH_HIT1", "ANIM_VEH_HIT2", "ANIM_ART_EXP1", "ANIM_FBALL1"], 130),
                6 => AnimName("ANIM_ATOM_BLAST"),
                _ => "",
            };
        }

        // ---- Special weapons (HouseClass's SpecialWeapons, by the building that gives them) ----
        var exportedPowers = new HashSet<string>();
        float Minutes(string key, float fallback) => ini.Num("Recharge", key, fallback) * 60_000;
        // (name, button image, targeted, module text, OCL text or "")
        List<(string Power, string Button, string Image, bool Targeted, string Module, string Ocl, float ReloadMs, string Label)> PowersOf(string building, string prefix)
        {
            var list = new List<(string, string, string, bool, string, string, float, string)>();
            void Add(string key, string label, string image, bool targeted, string module, string ocl, float reload)
            {
                string power = $"RA_Superweapon_{key}";
                list.Add((power, $"Command_{power}", image, targeted, module, ocl, reload, label));
            }
            switch (building.ToUpperInvariant())
            {
                case "MSLO":
                    // The atom bomb: AtomDamage to everything round where it falls (Nuke warhead), ATOMSFX.
                    weapons.Append($"  RA_Weapon_AtomBomb:\n    PrimaryDamage: {F(ini.Num("General", "AtomDamage", 1000))}\n    PrimaryDamageRadius: {F(CellUnits * 1.5f)}\n    SecondaryDamage: {F(ini.Num("General", "AtomDamage", 1000) / 2)}\n    SecondaryDamageRadius: {F(CellUnits * 3.5f)}\n");
                    weapons.Append("    AttackRange: 99999\n    DamageType: RADIATION\n    DeathType: BURNED\n    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n    FireFX: RAFX:ATOMSFX\n");
                    Add("Nuke", "Nuclear Missile", "RACAMEO:ATOM", true, "OCLSpecialPower", "    - Type: FireWeapon\n      Weapon: RA_Weapon_AtomBomb\n", Minutes("Nuke", 13));
                    break;
                case "PDOX":
                    Add("Chronosphere", "Chronosphere", "RACAMEO:WARP", true, $"RaSuperweapon\n        Mode: Chronosphere\n        Duration: {F(ini.Num("General", "ChronoDuration", 3) * 60)}", "", Minutes("Chrono", 7));
                    break;
                case "IRON":
                    Add("IronCurtain", "Iron Curtain", "RACAMEO:INFX", true, $"RaSuperweapon\n        Mode: IronCurtain\n        Duration: {F(ini.Num("General", "IronCurtain", 0.75f) * 60)}", "", Minutes("IronCurtain", 11));
                    break;
                case "ATEK":
                    Add("GPS", "GPS Satellite", "RACAMEO:GPSS", false, "RaSuperweapon\n        Mode: GPS", "", Minutes("GPS", 8));
                    break;
                case "AFLD":
                    // The airfield's free missions: paratroopers (five riflemen), parachute bombs, the spy plane.
                    Add("Paratroopers", "Paratroopers", "RACAMEO:PINF", true, "OCLSpecialPower", $"    - Type: CreateObject\n      ObjectNames: {prefix}E1\n      Count: 5\n      Offset: X:0 Y:0 Z:0\n", Minutes("Paratrooper", 7));
                    if (Weapon("ParaBomb") is { } bomb)
                        Add("ParaBomb", "Parachute Bombs", "RACAMEO:PBMB", true, "OCLSpecialPower", $"    - Type: FireWeapon\n      Weapon: {bomb}\n", Minutes("ParaBomb", 14));
                    Add("SpyPlane", "Spy Plane", "RACAMEO:CAM", true, "OCLSpecialPower", "", Minutes("SpyPlane", 3));
                    break;
            }
            return list;
        }

        // ---- The two sides ----
        var buildingRules = ra.Rules.Section("Buildings");
        string RawType(string section, string iniName) =>
            ra.Rules.Section(section).Entries.FirstOrDefault(e => e.Value.GetString("IniName").Equals(iniName, StringComparison.OrdinalIgnoreCase)).Value?.GetString("Type") ?? "";

        foreach (var side in Sides)
        {
            bool Own(TechnoTypeClass t)
            {
                if (!ini.Has(t.IniName))
                    return false;
                int level = (int)ini.Num(t.IniName, "TechLevel", -1);
                if (level < 0 || level > BuildLevel)
                    return false;
                return ini.List(t.IniName, "Owner").Any(o => side.Owners.Contains(o, StringComparer.OrdinalIgnoreCase));
            }
            var structs = types.AllBuildings.Where(Own).ToList();
            var units = types.AllUnits.Where(Own).ToList();
            var infantry = types.AllInfantry.Where(Own).ToList();
            var aircraft = types.AllAircraft.Where(Own).ToList();
            var vessels = types.AllVessels.Where(Own).ToList();
            // RA's shipyard and sub pen build ships (their ToBuild is RTTI_VESSELTYPE, which TD's classes lack).
            static bool BuildsShips(BuildingTypeClass b) => b.IniName is "SYRD" or "SPEN";
            static bool IsFactory(BuildingTypeClass b) => b.ToBuild != RTTIType.RTTI_NONE || BuildsShips(b);
            // Every side has a Construction Yard and its MCV (they start with one).
            if (types.FromName<BuildingTypeClass>("FACT") is { } yard && !structs.Contains(yard))
                structs.Insert(0, yard);
            var notBuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (types.FromName<UnitTypeClass>("MCV") is { } mcv && !units.Contains(mcv))
            {
                units.Add(mcv);
                notBuilt.Add("MCV");
            }
            var names = structs.Select(s => s.IniName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string N(ObjectTypeClass t) => side.Prefix + t.IniName;
            string Name(TechnoTypeClass t)
            {
                if (ini.Get(t.IniName, "Name") is { Length: > 0 } n)
                    return n;
                // FullName: the text number in CONQUER.ENG.
                int id = (int)(ra.Rules.Section(t switch { BuildingTypeClass => "Buildings", UnitTypeClass => "Vehicles", InfantryTypeClass => "Infantry", _ => "Aircraft" })
                    .Entries.FirstOrDefault(e => e.Value.GetString("IniName").Equals(t.IniName, StringComparison.OrdinalIgnoreCase)).Value is { } node
                    ? RuleValue.Int(node.GetString("FullName"), -1) : -1);
                return id >= 0 && id < text.Length && text[id].Length > 0 ? text[id] : t.IniName;
            }

            string Prerequisites(TechnoTypeClass t)
            {
                var lines = new List<string>();
                foreach (string need in ini.List(t.IniName, "Prerequisite"))
                {
                    var alternatives = Equivalents.GetValueOrDefault(need, [need]).Where(names.Contains).Select(a => side.Prefix + a.ToUpperInvariant()).ToList();
                    if (alternatives.Count > 0)
                        lines.Add(string.Join(' ', alternatives));
                }
                if (lines.Count == 0)
                    return "    Prerequisites: {}\n";
                var sb = new StringBuilder("    Prerequisites:\n      Object:\n");
                foreach (var l in lines)
                    sb.Append($"      - {l}\n");
                return sb.ToString();
            }

            string Button(TechnoTypeClass t)
            {
                string name = $"Command_RA_{side.Prefix}{t.IniName}";
                buttons.Append($"  {name}:\n    Command: UNIT_BUILD\n    Object: {N(t)}\n    TextLabel: {Name(t)}\n    ButtonImage: RACAMEO:{t.IniName}\n    ButtonBorderType: BUILD\n    DescriptLabel: {Name(t)}\n");
                return name;
            }

            void CommandSet(string name, IEnumerable<string> entries)
            {
                commandSets.Append($"  {name}:\n");
                int slot = 1;
                foreach (var e in entries)
                    commandSets.Append($"    {slot++}: {e}\n");
            }

            string FactoryCommandSet(BuildingTypeClass b)
            {
                string setName = $"{N(b)}CommandSet";
                var built = BuildsShips(b) ? vessels.Cast<TechnoTypeClass>() : b.ToBuild switch
                {
                    RTTIType.RTTI_BUILDINGTYPE => structs.Where(s => s.IniName != "FACT").Cast<TechnoTypeClass>(),
                    RTTIType.RTTI_UNITTYPE => units.Where(u => !notBuilt.Contains(u.IniName)).Cast<TechnoTypeClass>(),
                    RTTIType.RTTI_INFANTRYTYPE => infantry.Cast<TechnoTypeClass>(),
                    RTTIType.RTTI_AIRCRAFTTYPE => aircraft.Where(a => AirFactory(a).Equals(b.IniName, StringComparison.OrdinalIgnoreCase)).Cast<TechnoTypeClass>(),
                    _ => [],
                };
                var list = built.Select(Button).ToList();
                list.AddRange(PowersOf(b.IniName, side.Prefix).Select(x => x.Button));
                if (b.ToBuild != RTTIType.RTTI_BUILDINGTYPE || BuildsShips(b))
                    list.Add("Command_SetRallyPoint");
                list.Add("Command_RaRepair");
                list.Add("Command_Sell");
                CommandSet(setName, list);
                return setName;
            }
            // RA: planes come from the airfield (AFLD), helicopters from the helipad (HPAD).
            static string AirFactory(AircraftTypeClass a) => a.IsFixedWing ? "AFLD" : "HPAD";

            // ---- Structures ----
            foreach (var b in structs)
            {
                string key = b.IniName;
                var occupy = CellBox(b, b.Occupy_List(types, types.Special));
                var footprint = CellBox(b, b.Occupy_List(types, types.Special, placement: true));
                (float X, float Y) Art(float col, float row) => ((col - b.Width() / 2f) * SizeUnits - occupy.X, -(row - b.Height() / 2f) * SizeUnits - occupy.Y);
                int cost = (int)ini.Num(key, "Cost", b.Cost);
                float hp = ini.Num(key, "Strength", b.MaxStrength);
                string role = RawType("Buildings", key) is "STRUCT_TENT" ? "STRUCT_BARRACKS" : RawType("Buildings", key);
                objects.Append($"  {N(b)}:\n    DisplayName: {Name(b)}\n    Side: {side.SideName}\n    EditorSorting: STRUCTURE\n    ButtonImage: RACAMEO:{key}\n    SelectPortrait: RACAMEO:{key}\n");
                objects.Append($"    BuildCost: {cost}\n    BuildTime: {F(BuildSeconds(cost))}\n    TdRole: {role}\n");
                int power = (int)ini.Num(key, "Power");
                if (power != 0)
                    objects.Append($"    EnergyProduction: {power}\n");
                float sight = Math.Max(ini.Num(key, "Sight", 2), 2) * CellUnits;
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n    RadarPriority: STRUCTURE\n");
                var kinds = new List<string> { "PRELOAD", "STRUCTURE", "SELECTABLE", "IMMOBILE", "SCORE", "TD_OBJECT" };
                if (!b.IsWall)
                    kinds.Add("MP_COUNT_FOR_VICTORY");
                if (ini.Bool(key, "Capturable", b.IsCaptureable))
                    kinds.Add("CAPTURABLE");
                if (key == "FACT")
                    kinds.AddRange(["COMMANDCENTER", "FS_FACTORY", "AUTO_RALLYPOINT"]);
                if (power > 0)
                    kinds.Add("FS_POWER");
                if (b.IsWall)
                    kinds.Add("WALK_ON_TOP_OF_WALL");
                switch (b.ToBuild)
                {
                    case RTTIType.RTTI_INFANTRYTYPE: kinds.AddRange(["FS_FACTORY", "FS_BARRACKS", "AUTO_RALLYPOINT"]); break;
                    case RTTIType.RTTI_UNITTYPE: kinds.AddRange(["FS_FACTORY", "FS_WARFACTORY", "AUTO_RALLYPOINT"]); break;
                    case RTTIType.RTTI_AIRCRAFTTYPE: kinds.AddRange(["FS_FACTORY", "FS_AIRFIELD"]); break;
                }
                if (key == "PROC")
                    kinds.AddRange(["FS_SUPPLY_CENTER", "CASH_GENERATOR"]);
                // WaterBound: it stands on water (a shipyard).
                if (ini.Bool(key, "WaterBound"))
                    kinds.AddRange(["SHIPYARD", "FS_FACTORY", "AUTO_RALLYPOINT"]);
                if (ini.Num(key, "Adjacent", 1) > 1)
                    objects.Append($"    TdAdjacent: {(int)ini.Num(key, "Adjacent", 1)}\n");
                string? primary = Weapon(ini.Get(key, "Primary"));
                if (primary != null)
                    kinds.AddRange(["CAN_ATTACK", "FS_BASE_DEFENSE"]);
                if (key == "DOME")
                    kinds.Add("FS_TECHNOLOGY");
                if (key == "FIX")
                    kinds.Add("REPAIR_PAD");
                int storage = (int)ini.Num(key, "Storage");
                if (storage > 0)
                    objects.Append($"    TdStorage: {storage}\n");
                objects.Append($"    KindOf: {string.Join(' ', kinds)}\n");
                objects.Append($"    Geometry: BOX\n    GeometryMajorRadius: {F(occupy.HalfX)}\n    GeometryMinorRadius: {F(occupy.HalfY)}\n    GeometryHeight: {F(Math.Min(b.Width(), b.Height()) * SizeUnits / 2)}\n    GeometryIsSmall: No\n");
                objects.Append($"    TdArtOffset: 'X: {F(-occupy.X)} Y: {F(-occupy.Y)} Z:0.0'\n");
                objects.Append($"    TdFootprint: {F(footprint.X - occupy.X)} {F(footprint.Y - occupy.Y)} {F(footprint.HalfX)} {F(footprint.HalfY)}\n");
                objects.Append("    BuildCompletion: PLACED_BY_PLAYER\n    PlacementViewAngle: 0\n    TdBuiltByYard: Yes\n");
                objects.Append(Prerequisites(b));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(ini.Get(key, "Armor", "wood"))}\n      DamageFX: StructureDamageFXNoShake\n");
                if (primary != null)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: PRIMARY {primary}\n");
                objects.Append(IsFactory(b)
                    ? $"    CommandSet: {FactoryCommandSet(b)}\n"
                    : $"    CommandSet: {Set($"{N(b)}CommandSet", [.. PowersOf(key, side.Prefix).Select(x => x.Button), "Command_RaRepair", "Command_Sell"])}\n");
                objects.Append("    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RASpriteDraw\n        Shape: {(ini.Get(key, "Image") is { Length: > 0 } buildingImage ? buildingImage : key)}\n        TdKind: Building\n        TdType: {key}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: StructureBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                objects.Append($"      ModuleTag_12:\n        Module: Behavior\n        Class: TdStructureRepair\n        RepairStep: {F(Math.Max(hp / 20, 5))}\n");
                int powerTag = 20;
                foreach (var (sw, button, image, targeted, module, ocl, reload, label) in PowersOf(key, side.Prefix))
                {
                    string oclName = $"OCL_{sw}_{side.Prefix.TrimEnd('_')}";
                    objects.Append($"      ModuleTag_{powerTag++}:\n        Module: Behavior\n        Class: {module}\n        SpecialPowerTemplate: {sw}\n");
                    if (module == "OCLSpecialPower")
                        objects.Append($"        OCL: {oclName}\n        CreateLocation: CREATE_AT_LOCATION\n");
                    if (!exportedPowers.Add(sw + side.Prefix))
                        continue;
                    if (module == "OCLSpecialPower")
                        ocls.Append($"  {oclName}:\n" + (ocl.Length > 0 ? $"    Nuggets:\n{ocl}" : "    Nuggets: []\n"));
                    if (exportedPowers.Add(sw))
                    {
                        powers.Append($"  {sw}:\n    Enum: SPECIAL_{sw.ToUpperInvariant()}\n    ReloadTime: {(int)reload}\n    PublicTimer: {(sw.EndsWith("Nuke") || sw.EndsWith("Chronosphere") || sw.EndsWith("IronCurtain") ? "Yes" : "No")}\n");
                        powers.Append($"    SharedSyncedTimer: No\n    ViewObjectDuration: {(sw.EndsWith("SpyPlane") ? 30000 : 10000)}\n    ViewObjectRange: {(sw.EndsWith("SpyPlane") ? F(CellUnits * 10) : "150")}\n");
                        powers.Append($"    RadiusCursorRadius: {(targeted ? (sw.EndsWith("Nuke") ? F(CellUnits * 3.5f) : "40") : "0")}\n    ShortcutPower: Yes\n");
                        buttons.Append($"  {button}:\n    Command: SPECIAL_POWER\n    SpecialPower: {sw}\n    Options: {(targeted ? "NEED_TARGET_POS CONTEXTMODE_COMMAND" : "OK_FOR_MULTI_SELECT")}\n");
                        buttons.Append($"    TextLabel: {label}\n    ButtonImage: {image}\n    ButtonBorderType: ACTION\n    DescriptLabel: {label}\n");
                    }
                }
                if (primary != null)
                {
                    objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (b.IsTurretEquipped)
                        objects.Append("        Turret:\n          TurretTurnRate: 180\n          ControlledWeaponSlots: PRIMARY\n");
                }
                if (IsFactory(b))
                {
                    objects.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                    if (b.ToBuild == RTTIType.RTTI_BUILDINGTYPE)
                        objects.Append("        PlacesStructures: Yes\n");
                    else
                    {
                        // Ships leave the yard past its bottom edge, on the water.
                        var create = Art(b.Width() / 2f, BuildsShips(b) ? b.Height() + 0.5f : b.Height() - 0.25f);
                        var rally = Art(b.Width() / 2f, b.Height() + 1);
                        objects.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: DefaultProductionExitUpdate\n        UnitCreatePoint: 'X: {F(create.X)} Y: {F(create.Y)} Z:0.0'\n        NaturalRallyPoint: 'X: {F(rally.X)} Y: {F(rally.Y)} Z:0.0'\n");
                    }
                }
                if (key == "PROC")
                {
                    var dock = Art(b.Width() / 2f, b.Height() - 0.5f);
                    var start = Art(b.Width() / 2f, b.Height() + 0.5f);
                    objects.Append("      ModuleTag_08:\n        Module: Behavior\n        Class: SupplyCenterCreate\n");
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: SupplyCenterDockUpdate\n        AllowsPassthrough: No\n        NumberApproachPositions: -1\n");
                    objects.Append($"        DockStart: 'X: {F(start.X)} Y: {F(start.Y)} Z:0.0'\n        DockAction: 'X: {F(dock.X)} Y: {F(dock.Y)} Z:0.0'\n        DockEnd: 'X: {F(dock.X)} Y: {F(dock.Y)} Z:0.0'\n");
                    // The free harvester that comes with a refinery (BuildingClass::Grand_Opening: "Tiberium Refineries get a free harvester").
                    objects.Append($"      ModuleTag_10:\n        Module: Behavior\n        Class: TdFreeUnitOnBuilt\n        Unit: {side.Prefix}HARV\n");
                }
                if (key == "FIX")
                    objects.Append("      ModuleTag_11:\n        Module: Behavior\n        Class: RepairDockUpdate\n        TimeForFullHeal: 5000\n        NumberApproachPositions: 3\n");
            }

            // ---- Units, infantry, aircraft ----
            void Mobile(TechnoTypeClass t, string kind, string locomotor, float radius, float height)
            {
                string key = t.IniName;
                int cost = (int)ini.Num(key, "Cost", t.Cost);
                float hp = ini.Num(key, "Strength", t.MaxStrength);
                float sight = Math.Max(ini.Num(key, "Sight", 2), 1) * CellUnits;
                string role = kind == "Infantry" ? RawType("Infantry", key) : kind == "Unit" ? RawType("Vehicles", key) : RawType("Aircraft", key);
                objects.Append($"  {N(t)}:\n    DisplayName: {Name(t)}\n    Side: {side.SideName}\n    EditorSorting: {(kind == "Infantry" ? "INFANTRY" : "VEHICLE")}\n");
                if (kind == "Vessel")
                    role = RawType("VesselTypeClass", key);
                objects.Append($"    ButtonImage: RACAMEO:{key}\n    SelectPortrait: RACAMEO:{key}\n    BuildCost: {cost}\n    BuildTime: {F(BuildSeconds(cost))}\n    TdRole: {role}\n");
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n    IsTrainable: Yes\n    RadarPriority: UNIT\n");
                var kinds = new List<string> { "PRELOAD", "SELECTABLE", "SCORE", "TD_OBJECT" };
                string? primary = Weapon(ini.Get(key, "Primary")), secondary = Weapon(ini.Get(key, "Secondary"));
                if (primary != null)
                    kinds.Add("CAN_ATTACK");
                kinds.Add(kind switch { "Infantry" => "INFANTRY", "Aircraft" => "AIRCRAFT", _ => "VEHICLE" });
                if (key == "HARV")
                    kinds.Add("HARVESTER");
                if (key == "MCV")
                    kinds.Add("TD_MCV");
                if (kind == "Aircraft")
                    kinds.Add("VTOL");
                objects.Append($"    KindOf: {string.Join(' ', kinds)}\n    Locomotor: SET_NORMAL {locomotor}\n");
                objects.Append($"    Geometry: {(kind == "Infantry" ? "CYLINDER" : "BOX")}\n    GeometryMajorRadius: {F(radius)}\n    GeometryMinorRadius: {F(radius)}\n    GeometryHeight: {F(height)}\n    GeometryIsSmall: Yes\n");
                if (kind == "Infantry")
                    objects.Append("    CrushableLevel: 0\n    TransportSlotCount: 1\n");
                else if (kind is "Unit" or "Vessel")
                    objects.Append($"    CrusherLevel: {(ini.Bool(key, "Crusher") || ini.Bool(key, "Tracked") ? 2 : 1)}\n    CrushableLevel: 2\n");
                objects.Append(Prerequisites(t));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(ini.Get(key, "Armor", "none"))}\n");
                var slots = new List<string>();
                if (primary != null)
                    slots.Add($"PRIMARY {primary}");
                if (secondary != null)
                    slots.Add($"SECONDARY {secondary}");
                if (slots.Count > 0)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: [{string.Join(", ", slots)}]\n");
                int passengers = (int)ini.Num(key, "Passengers");
                objects.Append(passengers > 0 ? "    CommandSet: RATransportCommandSet\n" : "    CommandSet: GenericCommandSet\n");
                objects.Append("    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RASpriteDraw\n        Shape: {(ini.Get(key, "Image") is { Length: > 0 } img ? img : key)}\n        TdKind: {kind}\n        TdType: {key}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                if (key == "HARV")
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: 9\n        SupplyCenterActionDelay: 1500\n        SupplyWarehouseActionDelay: 600\n        SupplyWarehouseScanDistance: 900\n");
                else
                {
                    objects.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: {(passengers > 0 ? "TransportAIUpdate" : "AIUpdateInterface")}\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (t.IsTurretEquipped)
                        objects.Append("        Turret:\n          TurretTurnRate: 180\n          ControlledWeaponSlots: PRIMARY\n");
                }
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                objects.Append(kind == "Infantry"
                    ? "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 1000\n"
                    : "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 100\n        FX: FINAL FX_GenericTankDeathExplosion\n");
                if (key == "MCV")
                    objects.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {side.Prefix}FACT\n");
                if (passengers > 0)
                    objects.Append($"      ModuleTag_08:\n        Module: Behavior\n        Class: TransportContain\n        Slots: {passengers}\n        AllowInsideKindOf: INFANTRY\n        ExitDelay: 250\n        NumberOfExitPaths: 1\n        GoAggressiveOnExit: Yes\n        DamagePercentToUnits: 100%\n");
                if (ini.Bool(key, "Cloakable"))
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: StealthUpdate\n        StealthDelay: 2000\n        StealthForbiddenConditions: ATTACKING FIRING_PRIMARY\n        InnateStealth: Yes\n        OrderIdleEnemiesToAttackMeUponReveal: No\n");
                // RA's engineer captures (or damages) buildings; Tanya blows them up.
                if (key == "E6")
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Capture\n");
                if (ini.Bool(key, "C4"))
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Demolish\n        Consumed: No\n");
                if (kind == "Aircraft" && primary != null && ini.Num(key, "Ammo") > 0)
                    objects.Append($"      ModuleTag_11:\n        Module: Behavior\n        Class: TdAircraftAmmo\n        MaxAmmo: {(int)ini.Num(key, "Ammo")}\n");
            }

            float Speed(TechnoTypeClass t) => ini.Num(t.IniName, "Speed") * 256 / 100f * TicksPerSecond * LeptonUnits;
            float Rot(TechnoTypeClass t) => Math.Max(ini.Num(t.IniName, "ROT", 10), 1) * TicksPerSecond * 360 / 256f;
            foreach (var u in units)
            {
                string loco = $"{N(u)}Locomotor";
                bool tracked = ini.Bool(u.IniName, "Tracked");
                locomotors.Append($"  {loco}:\n    Surfaces: GROUND\n    Speed: {F(Speed(u))}\n    SpeedDamaged: {F(Speed(u) * 0.75f)}\n");
                locomotors.Append($"    TurnRate: {F(Rot(u))}\n    TurnRateDamaged: {F(Rot(u))}\n    Acceleration: 1000\n    AccelerationDamaged: 1000\n    Braking: 1000\n    MinTurnSpeed: 0\n");
                locomotors.Append($"    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: {(tracked ? "TREADS" : "FOUR_WHEELS")}\n");
                Mobile(u, "Unit", loco, u.IniName is "4TNK" or "MCV" or "HARV" ? 17 : 13, 12);
            }
            foreach (var i in infantry)
            {
                string loco = $"{N(i)}Locomotor";
                float speed = Speed(i) * InfantrySpeedFactor;
                locomotors.Append($"  {loco}:\n    Surfaces: GROUND RUBBLE\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n");
                locomotors.Append("    TurnRate: 500\n    TurnRateDamaged: 500\n    Acceleration: 100\n    AccelerationDamaged: 100\n    Braking: 100\n    MinTurnSpeed: 0\n");
                locomotors.Append("    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: TWO_LEGS\n    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
                Mobile(i, "Infantry", loco, 5, 12);
            }
            foreach (var v in vessels)
            {
                // Ships: on water only (RA's SPEED_FLOAT).
                string loco = $"{N(v)}Locomotor";
                locomotors.Append($"  {loco}:\n    Surfaces: WATER\n    Speed: {F(Speed(v))}\n    SpeedDamaged: {F(Speed(v) * 0.75f)}\n");
                locomotors.Append($"    TurnRate: {F(Rot(v))}\n    TurnRateDamaged: {F(Rot(v))}\n    Acceleration: 200\n    AccelerationDamaged: 200\n    Braking: 200\n    MinTurnSpeed: 0\n");
                locomotors.Append("    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: HOVER\n");
                Mobile(v, "Vessel", loco, v.IniName is "CA" or "CARR" ? 22 : 16, 10);
            }
            foreach (var a in aircraft)
            {
                string loco = $"{N(a)}Locomotor";
                float speed = Math.Max(Speed(a), 40);
                locomotors.Append($"  {loco}:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n");
                locomotors.Append($"    TurnRate: {F(Math.Max(Rot(a), 120))}\n    TurnRateDamaged: {F(Math.Max(Rot(a), 120))}\n    Acceleration: 60\n    AccelerationDamaged: 60\n    Lift: 120\n    LiftDamaged: 80\n    Braking: 240\n    MinTurnSpeed: 0\n");
                locomotors.Append($"    PreferredHeight: {F(24 * 1.5f)}\n    AllowAirborneMotiveForce: Yes\n    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n    Appearance: HOVER\n    Apply2DFrictionWhenAirborne: Yes\n    AirborneTargetingHeight: 30\n    LocomotorWorksWhenDead: Yes\n");
                Mobile(a, "Aircraft", loco, 14, 10);
            }

            string Set(string name, IEnumerable<string> entries)
            {
                CommandSet(name, entries);
                return name;
            }

            // ---- The player templates: one a country (HouseTypeClass), each with its RULES.INI biases ----
            var countries = side.Prefix == "RAALL_"
                ? new[] { ("England", "England"), ("France", "France"), ("Germany", "Germany") }
                : new[] { ("USSR", "Russia"), ("Ukraine", "Ukraine") };
            foreach (var (house, country) in countries)
            {
                factions.Append($"  FactionRA{country}:\n    Side: {side.SideName}\n    BaseSide: {side.BaseSide}\n    PlayableSide: Yes\n    StartMoney: 0\n    PreferredColor: {side.Color}\n");
                factions.Append($"    DisplayName: {country} (Red Alert)\n    StartingUnit0: {side.Prefix}MCV\n    TdFaction: Yes\n");
                foreach (var (key, field) in new[] { ("Firepower", "TdFirepower"), ("Armor", "TdArmor"), ("ROF", "TdROF"), ("Cost", "TdCost"), ("Groundspeed", "TdGroundspeed") })
                    if (ini.Num(house, key, 1) is var bias && bias != 1)
                        factions.Append($"    {field}: {F(bias)}\n");
                factions.Append($"    ScoreScreenImage: {side.Score}\n    LoadScreenImage: {side.Logo}\n    BeaconName: MultiplayerBeacon\n    OldFaction: Yes\n");
            }
        }

        // ---- Scenery for Red Alert's maps: trees and rocks (TERRAIN), ore and gems (one supply pile a cell) ----
        var scenery = new StringBuilder("Objects:\n");
        foreach (var t in types.AllTerrains)
        {
            float w = SizeUnits * 0.6f;
            scenery.Append($"  RA_Terrain_{t.IniName}:\n    DisplayName: {t.IniName}\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE TD_OBJECT\n");
            scenery.Append($"    Geometry: CYLINDER\n    GeometryMajorRadius: {F(w / 2)}\n    GeometryMinorRadius: {F(w / 2)}\n    GeometryHeight: 30\n    GeometryIsSmall: No\n");
            scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: RA_ArmorWood\n");
            scenery.Append($"    Modules:\n      ModuleTag_01:\n        Module: Draw\n        Class: RASpriteDraw\n        Shape: {t.IniName}\n        TdKind: Terrain\n        TdType: {t.IniName}\n");
            scenery.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {Math.Max(t.MaxStrength, 1)}.0\n        InitialHealth: {Math.Max(t.MaxStrength, 1)}.0\n");
            scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
        }
        // RULES.INI: GoldValue (25) and GemValue (50) a bail, up to 12 bails a cell (as TD's Tiberium, tripled for ZH's pace).
        foreach (var (kind, shape, value) in new[] { ("Ore", "GOLD01", ini.Num("General", "GoldValue", 25)), ("Gem", "GEM01", ini.Num("General", "GemValue", 50)) })
            for (int stage = 0; stage < 12; stage++)
            {
                int boxes = Math.Max(1, (int)Math.Ceiling(value * (stage + 1) * 3 / 75f));
                scenery.Append($"  RA_{kind}{stage + 1:00}:\n    DisplayName: {(kind == "Ore" ? "Ore" : "Gems")}\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE SUPPLY_SOURCE STRUCTURE TD_OBJECT\n");
                scenery.Append("    Geometry: CYLINDER\n    GeometryMajorRadius: 10\n    GeometryMinorRadius: 10\n    GeometryHeight: 3\n    GeometryIsSmall: Yes\n");
                scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: InvulnerableAllArmor\n");
                scenery.Append($"    Modules:\n      ModuleTag_01:\n        Module: Draw\n        Class: RASpriteDraw\n        Shape: {shape}\n        TdKind: Tiberium\n        TdType: {stage}\n");
                scenery.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 1.0\n        InitialHealth: 1.0\n");
                scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyWarehouseCreate\n");
                scenery.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: SupplyWarehouseDockUpdate\n        StartingBoxes: {boxes}\n        DeleteWhenEmpty: Yes\n        AllowsPassthrough: Yes\n        NumberApproachPositions: -1\n");
                // Ore grows and spreads (OreGrows, OreSpreads); gems don't.
                if (kind == "Ore")
                    scenery.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: TdTiberium\n        Mode: Tiberium\n        Seed: RA_Ore01\n        Poison: No\n");
            }

        const string header = "# GENERATED by 'OpenWestwood.Tools export-ra-factions' from Red Alert's RULES.INI and type tables (mods/redalert).\n" +
                              "# Red Alert's Allies and Soviets as Zero Hour engine factions. Edit freely, or re-export.\n";
        File.WriteAllText(Path.Combine(output, "sound-effects.yaml"), header + "AudioEvents:\n" + string.Concat(sounds.Select(v =>
            $"  RaSnd_{v}:\n    Sounds: /RASFX/{v}.AUD\n    Control: interrupt\n    Priority: normal\n    Limit: 3\n    Volume: 80\n    Type: world shrouded everyone\n")));
        File.WriteAllText(Path.Combine(output, "special-powers.yaml"), header + powers);
        File.WriteAllText(Path.Combine(output, "object-creation-lists.yaml"), header + ocls);
        File.WriteAllText(Path.Combine(output, "scenery.yaml"), header + scenery);
        File.WriteAllText(Path.Combine(output, "factions.yaml"), header + factions);
        File.WriteAllText(Path.Combine(output, "objects.yaml"), WestwoodHusks.Add(header + objects.ToString()));
        File.WriteAllText(Path.Combine(output, "projectiles.yaml"), header + projectiles);
        File.WriteAllText(Path.Combine(output, "weapons.yaml"), header + weapons);
        File.WriteAllText(Path.Combine(output, "locomotors.yaml"), header + locomotors);
        File.WriteAllText(Path.Combine(output, "armor.yaml"), header + armor);
        File.WriteAllText(Path.Combine(output, "command-sets.yaml"), header + commandSets);
        File.WriteAllText(Path.Combine(output, "command-buttons.yaml"), header + buttons);
        Console.WriteLine($"Wrote RA factions to {output} ({weaponNames.Count} weapons)");
        return 0;
    }

    /// <summary>RA's build time: as TD's, about cost/100 x 1.6 seconds at full power.</summary>
    static float BuildSeconds(int cost) => Math.Max(2, cost / 100f * 1.6f);

    /// <summary>The box round a building's cells (as TdFactionExporter.CellBox).</summary>
    static (float X, float Y, float HalfX, float HalfY) CellBox(BuildingTypeClass b, short[] cells)
    {
        int minCol = int.MaxValue, maxCol = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        foreach (short offset in cells)
        {
            if (offset == TdMath.RefreshEol)
                break;
            int row = offset / TdMath.MapCellW, col = offset % TdMath.MapCellW;
            minCol = Math.Min(minCol, col); maxCol = Math.Max(maxCol, col);
            minRow = Math.Min(minRow, row); maxRow = Math.Max(maxRow, row);
        }
        if (minCol == int.MaxValue)
            (minCol, maxCol, minRow, maxRow) = (0, b.Width() - 1, 0, b.Height() - 1);
        float cx = (minCol + maxCol + 1) / 2f, cy = (minRow + maxRow + 1) / 2f;
        return ((cx - b.Width() / 2f) * SizeUnits, -(cy - b.Height() / 2f) * SizeUnits,
            (maxCol - minCol + 1) * SizeUnits / 2, (maxRow - minRow + 1) * SizeUnits / 2);
    }

    /// <summary>CONQUER.ENG: uint16 offsets (the first gives the count), then zero-terminated strings.</summary>
    static string[] ReadStrings(byte[] data)
    {
        int count = BitConverter.ToUInt16(data, 0) / 2;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            int start = BitConverter.ToUInt16(data, i * 2);
            int end = start;
            while (end < data.Length && data[end] != 0)
                end++;
            strings[i] = start < data.Length ? Encoding.Latin1.GetString(data, start, end - start).Trim() : "";
        }
        return strings;
    }
}
