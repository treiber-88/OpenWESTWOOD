using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenWestwood.Core.Rules;
using OpenWestwood.Formats.Emperor;

namespace OpenWestwood.Tools;

/// <summary>
/// "export-emperor-factions": Emperor: Battle for Dune's Atreides, Harkonnen and Ordos as Zero Hour engine factions,
/// from the game's own Rules.txt (MODEL0002's patched one) and ArtIni.txt in content/emperor. Written to
/// mods/zh/rules/emperor. They play like the Dune 2000 factions: an MCV unfolds into a Construction Yard, which builds
/// structures that are then placed next to the base. The models are Emperor's own (EM_x / EMB_x, see XbfToW3d).
/// Conversions (an Emperor tile is 32 model units; XbfToW3d.Scale shrinks those to the engine's; the game runs
/// 20 ticks a second: Rules.txt's "6600 = 5.5 minutes"):
/// <list type="bullet">
/// <item>distances: tiles x TileUnits (12.8); health and damage: the rules' values / 5 (an infantryman's 600 is a
/// Ranger's 120);</item>
/// <item>times (BuildTime, ReloadCount): ticks / 20 seconds;</item>
/// <item>speeds ("game coord per update"): x 4 units a second (an Emperor infantryman, Speed 6, as fast as a
/// Zero Hour one) - a choice: how often Emperor moves its units isn't in the data;</item>
/// <item>damage against armour: each warhead is its own damage type (EM_x) with its table of percentages.</item>
/// </list>
/// Left for later: the sub-houses (Fremen, Sardaukar, Ix, Tleilaxu, Guild), the palaces' powers, the Starport's
/// market, walls, refinery docks, sounds.
/// </summary>
static class EmperorFactionExporter
{
    const float TileUnits = 32 * XbfToW3d.Scale, TicksPerSecond = 20, HealthScale = 5, SpeedUnits = 4;

    sealed record House(string Name, string Code, string Faction, string Side, string BaseSide, string Color, string ScoreImage, string LoadImage);

    // Colours: ArtIni's Recolor for sides 0-2.
    static readonly House[] Houses =
    [
        new("Atreides", "AT", "FactionEMAtreides", "EMATR", "USA", "R:0 G:133 B:226", "America_ScoreScreen", "SAFactionLogoPage_US"),
        new("Harkonnen", "HK", "FactionEMHarkonnen", "EMHAR", "China", "R:175 G:36 B:22", "China_ScoreScreen", "SNFactionLogoPage_China"),
        new("Ordos", "OR", "FactionEMOrdos", "EMORD", "GLA", "R:146 G:253 B:202", "GLA_ScoreScreen", "SUFactionLogoPage_GLA"),
    ];

    // Emperor's armour types and the Zero Hour armour each starts from (for Zero Hour weapons hitting them).
    static readonly Dictionary<string, string> ArmorBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = "HumanArmor", ["BPV"] = "HumanArmor", ["Light"] = "TruckArmor", ["Medium"] = "TankArmor", ["Heavy"] = "TankArmor",
        ["Concrete"] = "StructureArmor", ["Walls"] = "StructureArmor", ["Building"] = "StructureArmor", ["CY"] = "StructureArmor",
        ["Harvester"] = "TruckArmor", ["Invulnerable"] = "InvulnerableAllArmor", ["Aircraft"] = "AirplaneArmor",
    };

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Rules.txt / ArtIni.txt: [Section] then "Key = Value" lines (// comments); keys repeat (Occupy, VeterancyLevel).</summary>
    sealed class EmpIni
    {
        public readonly Dictionary<string, List<(string Key, string? Value)>> Sections = new(StringComparer.OrdinalIgnoreCase);

        public EmpIni(string text)
        {
            List<(string, string?)>? current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                    line = line[..comment];
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith('[') && line.IndexOf(']') is > 0 and var close)
                {
                    string name = line[1..close].Trim();
                    if (!Sections.TryGetValue(name, out current))
                        Sections[name] = current = new();
                    continue;
                }
                if (current == null)
                    continue;
                int eq = line.IndexOf('=');
                if (eq > 0)
                    current.Add((line[..eq].Trim(), line[(eq + 1)..].Trim().Trim('"')));
                else
                    current.Add((line, null));
            }
        }

        public string Get(string section, string key, string fallback = "") =>
            Sections.TryGetValue(section, out var s) && s.FirstOrDefault(e => e.Value != null && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Value: { } v } ? v : fallback;

        public IEnumerable<string> All(string section, string key) =>
            Sections.TryGetValue(section, out var s) ? s.Where(e => e.Value != null && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value!) : [];

        public bool Has(string section) => Sections.ContainsKey(section);

        /// <summary>The names listed in a list section ([UnitTypes]...).</summary>
        public IEnumerable<string> List(string section) =>
            Sections.TryGetValue(section, out var s) ? s.Where(e => e.Value == null).Select(e => e.Key) : [];

        public bool True(string section, string key) => Get(section, key).Trim().ToUpperInvariant() is "TRUE" or "1" or "YES";

        public float Num(string section, string key, float fallback = 0) =>
            float.TryParse(Get(section, key).Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    static string[] ListOf(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static int Run(string root)
    {
        string data = Path.Combine(root, "content", "emperor", "DATA");
        if (!Directory.Exists(data))
        {
            Console.Error.WriteLine($"No Emperor data in {data}");
            return 1;
        }
        using var art = new EmperorArtPackage(data);
        string Text(string path) => Encoding.Latin1.GetString(art.ReadAllBytes(EmperorArtPackage.Folder + path)).Replace("\r", "");
        var rules = new EmpIni(Text("Rules.txt"));
        var artIni = new EmpIni(Text("ArtIni.txt"));
        string output = Path.Combine(root, "mods", "zh", "rules", "emperor");
        Directory.CreateDirectory(output);

        var objects = new StringBuilder("Objects:\n");
        var effects = new StringBuilder("Objects:\n");
        var weapons = new StringBuilder("Weapons:\n");
        var locomotors = new StringBuilder("Locomotors:\n");
        var commandSets = new StringBuilder("CommandSets:\n");
        var buttons = new StringBuilder("CommandButtons:\n");
        buttons.Append("  Command_EmRepair:\n    Command: TD_REPAIR\n    TextLabel: Repair\n    ButtonImage: SSRepair\n    ButtonBorderType: ACTION\n    DescriptLabel: Repair this building (costs money as it mends)\n");
        var factions = new StringBuilder("PlayerTemplates:\n");
        var ocls = new StringBuilder("ObjectCreationLists:\n");
        var upgrades = new StringBuilder("Upgrades:\n");

        var warheads = rules.List("WarheadTypes").ToList();
        string DamageType(string warhead) => "EM_" + warhead.Replace(' ', '_');

        // ---- Models: an ArtIni entry's Xaf ("AT_minotaurus"), as EM_AT_minotaurus_H0 (EMB_ for buildings) ----
        string? Model(string key, bool building, string suffix = "_H0")
        {
            string xaf = artIni.Get(key, "Xaf");
            if (xaf.Length == 0)
                return null;
            xaf = Path.GetFileNameWithoutExtension(xaf.Replace('\\', '/'));
            // (Some name a whole file: "Mgbullet.xaf", "construction_AT_construction_yard_high.XBF".)
            foreach (string name in new[] { xaf + suffix, xaf })
                if (art.XbfPath(name) is { } path)
                    return (building ? "EMB_" : EmperorArtPackage.ModelPrefix) + Path.GetFileNameWithoutExtension(path);
            return null;
        }
        // The named animations of a model (from its XBF).
        var animationCache = new Dictionary<string, HashSet<string>>();
        HashSet<string> Animations(string model)
        {
            if (animationCache.TryGetValue(model, out var cached))
                return cached;
            string file = model[(model.IndexOf('_') + 1)..];
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (art.XbfPath(file) is { } path)
            {
                var xbf = XbfFile.Read(art.ReadAllBytes(path));
                foreach (var name in xbf.Animations.Keys)
                    set.Add(name.Replace(" ", ""));
                if (xbf.AllNodes.Any(n => (n.KeyAnimation?.FrameCount ?? 0) > 0 || (n.VertexAnimation?.FrameCount ?? 0) > 1))
                    set.Add("All");
            }
            return animationCache[model] = set;
        }
        string Icon(string key)
        {
            string icon = artIni.Get(key, "Icon");
            return icon.Length > 0 ? "EMCAMEO:" + Path.GetFileName(icon.Replace('\\', '/')) : "SSRepair";
        }
        string Display(string key)
        {
            string name = key;
            foreach (var h in Houses)
                if (name.StartsWith(h.Code, StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2]))
                    name = name[2..];
            return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        }

        // ---- Explosions: the effect model played once where a shot lands ----
        var explosionDone = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Explosion(string type)
        {
            if (type.Length == 0)
                return null;
            if (explosionDone.TryGetValue(type, out var done))
                return done;
            string? model = Model(type, building: false, suffix: "");
            if (model == null)
                return explosionDone[type] = null;
            string obj = "EM_FX_" + type;
            string ocl = "OCL_" + obj;
            var anims = Animations(model);
            effects.Append($"  {obj}:\n    DisplayName: Explosion\n    EditorSorting: SYSTEM\n    KindOf: NO_COLLIDE IMMOBILE UNATTACKABLE INERT\n    Modules:\n");
            effects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        DefaultConditionState:\n          Model: {model}\n");
            if (anims.Contains("All"))
                effects.Append($"          Animation: {model}.All\n          AnimationMode: ONCE\n");
            effects.Append("      ModuleTag_02:\n        Module: Behavior\n        Class: LifetimeUpdate\n        MinLifetime: 1500\n        MaxLifetime: 1500\n");
            ocls.Append($"  {ocl}:\n    Nuggets:\n    - Type: CreateObject\n      ObjectNames: {obj}\n      Disposition: LIKE_EXISTING\n");
            return explosionDone[type] = ocl;
        }

        // ---- Weapons: a unit's turret chain (TurretAttach, TurretNextJoint) gives the bullet and the reload ----
        var weaponDone = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        (string? Weapon, bool Yaw, bool Pitch, float TurnRate) Turret(string attach)
        {
            string? bullet = null;
            float reload = 30, turn = 0;
            bool yaw = false, pitch = false;
            int guard = 0;
            for (string joint = attach; joint.Length > 0 && rules.Has(joint) && guard++ < 6; joint = rules.Get(joint, "TurretNextJoint"))
            {
                bullet ??= rules.Get(joint, "Bullet") is { Length: > 0 } b ? b : null;
                if (rules.Num(joint, "ReloadCount") > 0 && bullet != null && reload == 30)
                    reload = rules.Num(joint, "ReloadCount", 30);
                if (rules.Get(joint, "TurretYRotationAngle").Length > 0)
                {
                    yaw = true;
                    turn = Math.Max(turn, rules.Num(joint, "TurretYRotationAngle") * TicksPerSecond);
                }
                pitch |= rules.Get(joint, "TurretXRotationAngle").Length > 0;
            }
            return (bullet != null ? Weapon(bullet, reload) : null, yaw, pitch, turn);
        }

        string? Weapon(string bullet, float reloadTicks)
        {
            string key = $"{bullet}@{reloadTicks}";
            if (weaponDone.TryGetValue(key, out var done))
                return done;
            if (!rules.Has(bullet))
                return weaponDone[key] = null;
            string name = $"EM_Weapon_{bullet}_{(int)reloadTicks}";
            weaponDone[key] = name;
            float damage = rules.Num(bullet, "Damage") / HealthScale;
            float range = Math.Max(rules.Num(bullet, "MaxRange", 4), 1) * TileUnits;
            float blast = Math.Max(rules.Num(bullet, "BlastRadius") * XbfToW3d.Scale, 3);
            string warhead = rules.Get(bullet, "Warhead");
            bool friendly = rules.True(bullet, "DamageFriendly") || rules.Num(bullet, "FriendlyDamageAmount") > 0;
            bool burns = rules.True(bullet, "Burnt"), gasses = rules.True(bullet, "Gassed");
            weapons.Append($"  {name}:\n    PrimaryDamage: {F(damage)}\n    PrimaryDamageRadius: {F(blast)}\n    AttackRange: {F(range)}\n");
            if (rules.Num(bullet, "MinRange") > 0)
                weapons.Append($"    MinimumAttackRange: {F(rules.Num(bullet, "MinRange") * TileUnits)}\n");
            weapons.Append($"    DamageType: {(warhead.Length > 0 ? DamageType(warhead) : "EXPLOSION")}\n");
            weapons.Append($"    DeathType: {(burns ? "BURNED" : gasses ? "POISONED" : rules.True(bullet, "BlowUp") ? "EXPLODED" : "NORMAL")}\n");
            weapons.Append($"    DelayBetweenShots: {(int)Math.Max(reloadTicks * 1000 / TicksPerSecond, 50)}\n    ClipSize: 0\n    ClipReloadTime: 0\n");
            weapons.Append($"    RadiusDamageAffects: {(friendly ? "ALLIES ENEMIES NEUTRALS" : "ENEMIES NEUTRALS")}\n");
            bool air = rules.True(bullet, "AntiAircraft");
            bool ground = rules.Get(bullet, "AntiGround").Length == 0 || rules.True(bullet, "AntiGround");
            weapons.Append($"    AntiGround: {(ground ? "Yes" : "No")}\n    AntiAirborneVehicle: {(air ? "Yes" : "No")}\n    AntiAirborneInfantry: {(air ? "Yes" : "No")}\n");
            // Every shot is a projectile (an instant one flies at once), so its explosion plays where it lands.
            float speed = rules.Num(bullet, "Speed", -1);
            float unitsPerSecond = speed > 0 ? speed * XbfToW3d.Scale * TicksPerSecond : 3000;
            string projectile = "EM_Projectile_" + bullet;
            weapons.Append($"    ProjectileObject: {projectile}\n    WeaponSpeed: {F(unitsPerSecond)}\n");
            if (Explosion(rules.Get(bullet, "ExplosionType")) is { } ocl)
                weapons.Append($"    ProjectileDetonationOCL: {ocl}\n");
            if (!effects.ToString().Contains($"  {projectile}:\n"))
            {
                bool homing = rules.True(bullet, "Homing");
                string? model = speed > 0 ? Model(bullet, building: false, suffix: "") : null;
                effects.Append($"  {projectile}:\n    DisplayName: Emperor projectile\n    EditorSorting: SYSTEM\n    VisionRange: 0.0\n");
                effects.Append(homing ? "    KindOf: PROJECTILE SMALL_MISSILE\n" : "    KindOf: PROJECTILE\n");
                if (homing)
                    effects.Append($"    Locomotor: SET_NORMAL {projectile}Locomotor\n");
                effects.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    ArmorSet:\n      Armor: ProjectileArmor\n    Modules:\n");
                if (model != null)
                {
                    effects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        DefaultConditionState:\n          Model: {model}\n");
                    if (Animations(model).Contains("All"))
                        effects.Append($"          Animation: {model}.All\n          AnimationMode: LOOP\n");
                }
                effects.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
                effects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                if (homing)
                {
                    effects.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: MissileAIUpdate\n        TryToFollowTarget: Yes\n        FuelLifetime: 10000\n        InitialVelocity: {F(unitsPerSecond)}\n        IgnitionDelay: 0\n        DistanceToTravelBeforeTurning: 5\n        DistanceToTargetForLock: 30\n");
                    float turn = Math.Max(rules.Num(bullet, "TurnRate", 0.3f) * 180 / MathF.PI * TicksPerSecond, 180);
                    locomotors.Append($"  {projectile}Locomotor:\n    Surfaces: AIR\n    Speed: {F(unitsPerSecond)}\n    MinSpeed: 30\n    Acceleration: 400\n    Braking: 0\n    TurnRate: {F(turn)}\n    MaxThrustAngle: 30\n    AllowAirborneMotiveForce: Yes\n    Appearance: THRUST\n    CloseEnoughDist: 1\n");
                }
                else
                {
                    float arc = rules.True(bullet, "Trajectory") ? 60 : 2;
                    effects.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n        FirstHeight: {F(arc)}\n        SecondHeight: {F(arc)}\n        FirstPercentIndent: 30%\n        SecondPercentIndent: 70%\n        FlightPathAdjustDistPerSecond: 100\n");
                }
                effects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
            }
            return name;
        }

        // ---- Draw: the model with its Stationary, Move, Fire and death animations ----
        string Draw(string model, bool building, string damaged, string reallyDamaged, (bool Yaw, bool Pitch) turret, bool infantry)
        {
            var anims = Animations(model);
            var sb = new StringBuilder($"      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        OkToChangeModelColor: Yes\n        DefaultConditionState:\n          Model: {model}\n");
            string idle = anims.Contains("Stationary") ? "Stationary" : anims.Contains("Idle0") ? "Idle0" : "";
            if (idle.Length > 0)
                sb.Append($"          Animation: {model}.{idle}\n          AnimationMode: LOOP\n");
            if (turret.Yaw)
                sb.Append("          Turret: TURRET\n");
            if (turret.Pitch)
                sb.Append("          TurretPitch: TURRETPITCH\n");
            sb.Append("          WeaponLaunchBone: PRIMARY MUZZLE\n          WeaponFireFXBone: PRIMARY MUZZLE\n");
            var states = new List<(string Conditions, string Model, string Anim, string Mode)>();
            if (!building)
            {
                if (anims.Contains("Move"))
                    states.Add(("MOVING", model, "Move", "LOOP"));
                if (anims.Contains("Fire0"))
                {
                    states.Add(("FIRING_A", model, "Fire0", "ONCE"));
                    states.Add(("BETWEEN_FIRING_SHOTS_A", model, infantry && anims.Contains("Fire0") ? "Fire0" : idle, infantry ? "ONCE" : "LOOP"));
                }
                string death = infantry ? (anims.Contains("Shot1") ? "Shot1" : anims.Contains("BlowUp1") ? "BlowUp1" : "") : anims.Contains("Explode") ? "Explode" : "";
                if (death.Length > 0)
                    states.Add(("DYING", model, death, "ONCE"));
                if (infantry && anims.Contains("Burnt1"))
                    states.Add(("DYING BURNED", model, "Burnt1", "ONCE"));
            }
            else
            {
                if (damaged.Length > 0)
                    states.Add(("DAMAGED", damaged, Animations(damaged).Contains("Stationary") ? "Stationary" : "", "LOOP"));
                if (reallyDamaged.Length > 0)
                    states.Add(("REALLYDAMAGED", reallyDamaged, Animations(reallyDamaged).Contains("Stationary") ? "Stationary" : "", "LOOP"));
            }
            if (states.Count > 0)
            {
                sb.Append("        ConditionState:\n");
                foreach (var (conditions, m, anim, mode) in states)
                {
                    sb.Append($"          {conditions}:\n            Model: {m}\n");
                    if (anim.Length > 0)
                        sb.Append($"            Animation: {m}.{anim}\n            AnimationMode: {mode}\n");
                }
            }
            return sb.ToString();
        }

        foreach (var house in Houses)
        {
            // ---- What this house builds: its own buildings from its yard, and units from those buildings ----
            string yard = house.Code + "ConYard";
            var buildings = rules.List("BuildingTypes").Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(b => rules.Get(b, "House").Equals(house.Name, StringComparison.OrdinalIgnoreCase)
                    && (ListOf(rules.Get(b, "PrimaryBuilding")).Contains(yard, StringComparer.OrdinalIgnoreCase) || b.Equals(yard, StringComparison.OrdinalIgnoreCase))
                    && !rules.True(b, "Wall") && !b.Contains("Dock") && !b.Contains("Frigate") && Model(b, true) != null)
                .ToList();
            var buildingSet = buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Units: built at one of this house's buildings (shared ones - the harvester, MCV, carryall - too).
            var units = rules.List("UnitTypes").Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(u => (rules.Get(u, "House").Equals(house.Name, StringComparison.OrdinalIgnoreCase) || rules.Get(u, "House").Length == 0)
                    && ListOf(rules.Get(u, "PrimaryBuilding")).Any(buildingSet.Contains)
                    && !rules.True(u, "HawkWeapon") && !rules.True(u, "DeathHand") && !rules.True(u, "BeamWeapon") && Model(u, false) != null)
                .ToList();
            bool Shared(string key) => rules.Get(key, "House").Length == 0;
            string N(string key) => Shared(key) ? $"EM_{house.Code}_{key}" : "EM_" + key;
            string FactoryOf(string unit) => ListOf(rules.Get(unit, "PrimaryBuilding")).First(buildingSet.Contains);
            string Upgrade(string building) => "Upgrade_EM_" + building;

            // SecondaryBuilding: any one of these (this house's own).
            string Prerequisites(string key, bool unit)
            {
                var objectsNeeded = new List<string>();
                var secondary = ListOf(rules.Get(key, "SecondaryBuilding")).Where(buildingSet.Contains).Select(N).ToList();
                if (secondary.Count > 0)
                    objectsNeeded.Add(string.Join(' ', secondary));
                var ups = new List<string>();
                if (unit && rules.True(key, "UpgradedPrimaryRequired"))
                    ups.Add(Upgrade(FactoryOf(key)));
                if (!unit && rules.True(key, "UpgradedPrimaryRequired"))
                    ups.Add(Upgrade(yard));
                if (objectsNeeded.Count == 0 && ups.Count == 0)
                    return "    Prerequisites: {}\n";
                var sb = new StringBuilder("    Prerequisites:\n");
                if (objectsNeeded.Count > 0)
                    sb.Append("      Object:\n" + string.Concat(objectsNeeded.Select(o => $"      - {o}\n")));
                if (ups.Count > 0)
                    sb.Append("      Upgrade:\n" + string.Concat(ups.Select(u => $"      - {u}\n")));
                return sb.ToString();
            }

            string Role(string key)
            {
                if (rules.True(key, "ConYard")) return "STRUCT_CONST";
                if (rules.Get(key, "Group").Equals("SmWindtrap", StringComparison.OrdinalIgnoreCase) || rules.Num(key, "PowerGenerated") > 0) return "STRUCT_POWER";
                if (rules.True(key, "Refinery")) return "STRUCT_REFINERY";
                if (rules.True(key, "Barracks")) return "STRUCT_BARRACKS";
                if (rules.True(key, "Factory")) return "STRUCT_WEAP";
                if (rules.True(key, "Outpost")) return "STRUCT_RADAR";
                if (rules.True(key, "Hanger")) return "STRUCT_HELIPAD";
                if (rules.True(key, "Starport")) return "STRUCT_AIRSTRIP";
                if (key.EndsWith("Palace", StringComparison.OrdinalIgnoreCase)) return "STRUCT_TEMPLE";
                if (rules.Get(key, "TurretAttach").Length > 0) return rules.Num(key, "Cost") >= 1000 ? "STRUCT_OBELISK" : "STRUCT_TURRET";
                return "STRUCT_EM";
            }
            bool IsFactory(string key) => Role(key) is "STRUCT_CONST" or "STRUCT_BARRACKS" or "STRUCT_WEAP" or "STRUCT_HELIPAD";

            // ---- Upgrades (UpgradeCost): researched at the building, a player upgrade ----
            foreach (string b in buildings.Where(b => rules.Num(b, "UpgradeCost") > 0))
                upgrades.Append($"  {Upgrade(b)}:\n    DisplayName: {Display(b)} upgrade\n    Type: PLAYER\n    BuildTime: {F(Math.Max(rules.Num(b, "UpgradeBuildTime", rules.Num(b, "BuildTime", 600)), 20) / TicksPerSecond)}\n" +
                    $"    BuildCost: {(int)rules.Num(b, "UpgradeCost")}\n    ResearchSound: NoSound\n");

            // ---- Command sets: what each factory builds ----
            foreach (string factory in buildings.Where(IsFactory))
            {
                var items = factory.Equals(yard, StringComparison.OrdinalIgnoreCase)
                    ? buildings.Where(b => !b.Equals(yard, StringComparison.OrdinalIgnoreCase)).OrderBy(b => rules.Num(b, "TechLevel")).ThenBy(b => rules.Num(b, "Cost")).ToList()
                    : units.Where(u => FactoryOf(u).Equals(factory, StringComparison.OrdinalIgnoreCase)).OrderBy(u => rules.Num(u, "TechLevel")).ThenBy(u => rules.Num(u, "Cost")).ToList();
                commandSets.Append($"  {N(factory)}CommandSet:\n");
                int slot = 1;
                foreach (string item in items)
                {
                    string button = $"Command_EM_{N(item)}";
                    buttons.Append($"  {button}:\n    Command: UNIT_BUILD\n    Object: {N(item)}\n    TextLabel: {Display(item)}\n    ButtonImage: {Icon(item)}\n    ButtonBorderType: BUILD\n    DescriptLabel: {Display(item)}\n");
                    commandSets.Append($"    {slot++}: {button}\n");
                }
                if (rules.Num(factory, "UpgradeCost") > 0)
                {
                    string button = $"Command_EM_{N(factory)}_Upgrade";
                    buttons.Append($"  {button}:\n    Command: PLAYER_UPGRADE\n    Upgrade: {Upgrade(factory)}\n    TextLabel: Upgrade\n    ButtonImage: {Icon(factory)}\n    ButtonBorderType: UPGRADE\n    DescriptLabel: Upgrade the {Display(factory)}\n");
                    commandSets.Append($"    {slot++}: {button}\n");
                }
                if (!factory.Equals(yard, StringComparison.OrdinalIgnoreCase))
                    commandSets.Append($"    {slot++}: Command_SetRallyPoint\n");
                commandSets.Append($"    {slot++}: Command_EmRepair\n    {slot}: Command_Sell\n");
            }

            // ---- Buildings ----
            foreach (string key in buildings)
            {
                string model = Model(key, true)!;
                string damaged = Model(key, true, "_H1") ?? "", reallyDamaged = Model(key, true, "_H2") ?? "";
                float hp = Math.Max(rules.Num(key, "Health", 1000) / HealthScale, 1);
                float sight = Math.Max(rules.Num(key, "ViewRange", 4), 2) * TileUnits;
                var (weapon, yaw, pitch, turnRate) = rules.Get(key, "TurretAttach") is { Length: > 0 } t ? Turret(t) : (null, false, false, 0f);
                string role = Role(key);
                var rows = rules.All(key, "Occupy").ToList();
                var (half, center) = Footprint(rows);
                var kinds = new List<string> { "PRELOAD", "STRUCTURE", "SELECTABLE", "IMMOBILE", "SCORE", "TD_OBJECT", "MP_COUNT_FOR_VICTORY" };
                if (role == "STRUCT_CONST") kinds.AddRange(["COMMANDCENTER", "FS_FACTORY", "AUTO_RALLYPOINT"]);
                if (role == "STRUCT_POWER") kinds.Add("FS_POWER");
                if (role == "STRUCT_REFINERY") kinds.AddRange(["FS_SUPPLY_CENTER", "CASH_GENERATOR"]);
                if (IsFactory(key) && role != "STRUCT_CONST") kinds.AddRange(["FS_FACTORY", "AUTO_RALLYPOINT"]);
                if (weapon != null) kinds.AddRange(["CAN_ATTACK", "FS_BASE_DEFENSE"]);
                if (rules.True(key, "CanBeEngineered")) kinds.Add("CAPTURABLE");

                objects.Append($"  {N(key)}:\n    DisplayName: {Display(key)}\n    Side: {house.Side}\n    EditorSorting: STRUCTURE\n");
                objects.Append($"    ButtonImage: {Icon(key)}\n    SelectPortrait: {Icon(key)}\n    TdRole: {role}\n");
                objects.Append($"    BuildCost: {(int)rules.Num(key, "Cost")}\n    BuildTime: {F(Math.Max(rules.Num(key, "BuildTime", 400), 10) / TicksPerSecond)}\n");
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n");
                int energy = (int)(rules.Num(key, "PowerGenerated") - rules.Num(key, "PowerUsed"));
                if (energy != 0)
                    objects.Append($"    EnergyProduction: {energy}\n");
                objects.Append(Prerequisites(key, unit: false));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: EM_Armor_{ListOf(rules.Get(key, "Armour", "Building")).FirstOrDefault() ?? "Building"}\n");
                if (weapon != null)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: PRIMARY {weapon}\n");
                objects.Append($"    RadarPriority: STRUCTURE\n    KindOf: {string.Join(' ', kinds)}\n");
                objects.Append($"    Geometry: BOX\n    GeometryMajorRadius: {F(half.X)}\n    GeometryMinorRadius: {F(half.Y)}\n    GeometryHeight: {F(Math.Max(rules.Num(key, "RoofHeight", 60) * XbfToW3d.Scale, 8))}\n    GeometryIsSmall: No\n");
                objects.Append("    BuildCompletion: PLACED_BY_PLAYER\n    PlacementViewAngle: 0\n    TdBuiltByYard: Yes\n");
                // Rules.txt's MaxBuildingPlacementTileDist: up to that many tiles from the base's buildings.
                objects.Append($"    TdAdjacentDistance: {F(rules.Num("General", "MaxBuildingPlacementTileDist", 6) * TileUnits)}\n");
                objects.Append($"    TdFootprint: {F(center.X)} {F(center.Y)} {F(half.X)} {F(half.Y)}\n");
                if (!ListOf(rules.Get(key, "Terrain")).Contains("Sand", StringComparer.OrdinalIgnoreCase))
                    objects.Append("    TdRockOnly: Yes\n");
                objects.Append($"    CommandSet: {(IsFactory(key) ? $"{N(key)}CommandSet" : "EMStructureCommandSet")}\n    Modules:\n");
                objects.Append(Draw(model, true, damaged, reallyDamaged, (yaw, pitch), infantry: false));
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: StructureBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                objects.Append($"      ModuleTag_12:\n        Module: Behavior\n        Class: TdStructureRepair\n        RepairStep: {F(hp / 10)}\n        RepairPercent: 25\n");
                if (weapon != null)
                {
                    objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (yaw)
                        objects.Append($"        Turret:\n          TurretTurnRate: {F(Math.Max(turnRate, 60))}\n          ControlledWeaponSlots: PRIMARY\n");
                }
                if (IsFactory(key))
                {
                    objects.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                    if (role == "STRUCT_CONST")
                        objects.Append("        PlacesStructures: Yes\n");
                    else
                        objects.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: DefaultProductionExitUpdate\n        UnitCreatePoint: 'X: 0 Y: {F(-half.Y + center.Y + 4)} Z:0.0'\n        NaturalRallyPoint: 'X: 0 Y: {F(-half.Y + center.Y - 30)} Z:0.0'\n");
                }
                if (role == "STRUCT_REFINERY")
                {
                    objects.Append("      ModuleTag_08:\n        Module: Behavior\n        Class: SupplyCenterCreate\n");
                    objects.Append($"      ModuleTag_09:\n        Module: Behavior\n        Class: SupplyCenterDockUpdate\n        AllowsPassthrough: No\n        NumberApproachPositions: -1\n        DockActionOffset: 'X: 0 Y: {F(-half.Y + center.Y)} Z:0.0'\n        DockStartOffset: 'X: 0 Y: {F(-half.Y + center.Y - TileUnits)} Z:0.0'\n        DockEndOffset: 'X: 0 Y: {F(-half.Y + center.Y - TileUnits)} Z:0.0'\n");
                    if (rules.Get(key, "GetUnitWhenBuilt") is { Length: > 0 } free && units.Contains(free, StringComparer.OrdinalIgnoreCase))
                        objects.Append($"      ModuleTag_10:\n        Module: Behavior\n        Class: TdFreeUnitOnBuilt\n        Unit: {N(free)}\n");
                }
            }

            // ---- Units ----
            foreach (string key in units)
            {
                string model = Model(key, false)!;
                bool infantry = rules.True(key, "Infantry");
                bool aircraft = rules.True(key, "CanFly");
                float hp = Math.Max(rules.Num(key, "Health", 500) / HealthScale, 1);
                float sight = Math.Max(rules.Num(key, "ViewRange", 4), 2) * TileUnits;
                var (weapon, yaw, pitch, turnRate) = rules.Get(key, "TurretAttach") is { Length: > 0 } t ? Turret(ListOf(t)[0]) : (null, false, false, 0f);
                float speed = Math.Max(rules.Num(key, "Speed", 4), 0.5f) * SpeedUnits;
                float turnDegrees = Math.Max(rules.Num(key, "TurnRate", 0.15f) * 180 / MathF.PI * TicksPerSecond, 90);
                string loco = $"{N(key)}Locomotor";
                if (aircraft)
                    locomotors.Append($"  {loco}:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n    TurnRate: {F(turnDegrees)}\n    TurnRateDamaged: {F(turnDegrees)}\n    Acceleration: 60\n    AccelerationDamaged: 60\n    Lift: 120\n    LiftDamaged: 80\n    Braking: 240\n    MinTurnSpeed: 0\n    PreferredHeight: 50\n    AllowAirborneMotiveForce: Yes\n    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n    Appearance: HOVER\n    Apply2DFrictionWhenAirborne: Yes\n    AirborneTargetingHeight: 30\n    LocomotorWorksWhenDead: Yes\n");
                else if (infantry)
                    locomotors.Append($"  {loco}:\n    Surfaces: GROUND RUBBLE\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n    TurnRate: 500\n    TurnRateDamaged: 500\n    Acceleration: 100\n    AccelerationDamaged: 100\n    Braking: 100\n    MinTurnSpeed: 0\n    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: TWO_LEGS\n    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
                else
                    locomotors.Append($"  {loco}:\n    Surfaces: GROUND\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed * 0.8f)}\n    TurnRate: {F(turnDegrees)}\n    TurnRateDamaged: {F(turnDegrees)}\n    Acceleration: 1000\n    AccelerationDamaged: 1000\n    Braking: 1000\n    MinTurnSpeed: 0\n    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: {(rules.True(key, "Mech") ? "TWO_LEGS" : "TREADS")}\n");

                string role = rules.True(key, "Harvester") ? "UNIT_HARVESTER" : rules.True(key, "MCV") ? "UNIT_MCV" : infantry ? "INFANTRY_EM" : aircraft ? "AIRCRAFT_EM" : "UNIT_EM";
                var kinds = new List<string> { "PRELOAD", "SELECTABLE", "SCORE", "TD_OBJECT", infantry ? "INFANTRY" : aircraft ? "AIRCRAFT" : "VEHICLE" };
                if (weapon != null) kinds.Add("CAN_ATTACK");
                if (role == "UNIT_HARVESTER") kinds.Add("HARVESTER");
                if (aircraft) kinds.Add("VTOL");
                float radius = infantry ? 4 : Math.Max(rules.Num(key, "Size", 2), 1) * TileUnits * 0.45f;
                objects.Append($"  {N(key)}:\n    DisplayName: {Display(key)}\n    Side: {house.Side}\n    EditorSorting: {(infantry ? "INFANTRY" : "VEHICLE")}\n");
                objects.Append($"    ButtonImage: {Icon(key)}\n    SelectPortrait: {Icon(key)}\n    TdRole: {role}\n");
                objects.Append($"    BuildCost: {(int)rules.Num(key, "Cost")}\n    BuildTime: {F(Math.Max(rules.Num(key, "BuildTime", 300), 10) / TicksPerSecond)}\n");
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n");
                objects.Append(Prerequisites(key, unit: true));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: EM_Armor_{ListOf(rules.Get(key, "Armour", "None")).FirstOrDefault() ?? "None"}\n");
                if (weapon != null)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: PRIMARY {weapon}\n");
                objects.Append($"    IsTrainable: Yes\n    RadarPriority: UNIT\n    KindOf: {string.Join(' ', kinds)}\n    Locomotor: SET_NORMAL {loco}\n");
                objects.Append($"    Geometry: {(infantry ? "CYLINDER" : "BOX")}\n    GeometryMajorRadius: {F(radius)}\n    GeometryMinorRadius: {F(radius)}\n    GeometryHeight: {F(infantry ? 12 : 12)}\n    GeometryIsSmall: Yes\n");
                objects.Append(infantry ? "    CrushableLevel: 0\n    TransportSlotCount: 1\n" : rules.True(key, "Crushes") ? "    CrusherLevel: 1\n    CrushableLevel: 2\n" : "    CrushableLevel: 2\n");
                objects.Append("    CommandSet: GenericCommandSet\n    Modules:\n");
                objects.Append(Draw(model, false, "", "", (yaw, pitch), infantry));
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                if (role == "UNIT_HARVESTER")
                    objects.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: {Math.Max((int)MathF.Round(rules.Num(key, "SpiceCapacity", 700) / 75), 1)}\n        SupplyCenterActionDelay: 3000\n        SupplyWarehouseActionDelay: 600\n        SupplyWarehouseScanDistance: 900\n");
                else
                {
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (yaw)
                        objects.Append($"        Turret:\n          TurretTurnRate: {F(Math.Max(turnRate, 90))}\n          ControlledWeaponSlots: PRIMARY\n");
                }
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                objects.Append(infantry
                    ? "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 2500\n"
                    : "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 600\n        FX: FINAL FX_GenericTankDeathExplosion\n");
                if (role == "UNIT_MCV")
                    objects.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {N(yard)}\n");
                if (rules.True(key, "Engineer"))
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Capture\n");
                if (rules.True(key, "Saboteur"))
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Demolish\n        Consumed: Yes\n");
                if (rules.True(key, "StealthedWhenStill"))
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: StealthUpdate\n        StealthDelay: 1500\n        StealthForbiddenConditions: MOVING ATTACKING FIRING_PRIMARY\n        InnateStealth: Yes\n        OrderIdleEnemiesToAttackMeUponReveal: No\n");
                if (rules.True(key, "Carryall") || key.Equals("Carryall", StringComparison.OrdinalIgnoreCase))
                    objects.Append("      ModuleTag_13:\n        Module: Behavior\n        Class: D2kCarryall\n        BeforeLoadDelay: 0.5\n        BeforeUnloadDelay: 0.7\n");
                if (role == "UNIT_HARVESTER")
                    objects.Append("      ModuleTag_14:\n        Module: Behavior\n        Class: D2kCarryable\n        MinDistance: 10\n");
            }

            factions.Append($"  {house.Faction}:\n    Side: {house.Side}\n    BaseSide: {house.BaseSide}\n    PlayableSide: Yes\n    StartMoney: 0\n");
            factions.Append($"    PreferredColor: {house.Color}\n    DisplayName: {house.Name} (Emperor)\n    StartingUnit0: {N("MCV")}\n    TdFaction: Yes\n");
            factions.Append($"    ScoreScreenImage: {house.ScoreImage}\n    LoadScreenImage: {house.LoadImage}\n    BeaconName: MultiplayerBeacon\n");
            Console.WriteLine($"{house.Name}: {buildings.Count} buildings, {units.Count} units");
        }
        commandSets.Append("  EMStructureCommandSet:\n    1: Command_EmRepair\n    2: Command_Sell\n");

        // ---- Armour: each Emperor type, from its Zero Hour base, with every warhead's percentage ----
        var zhArmor = RuleYaml.Parse(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "armor.yaml")), "armor.yaml")["Armor"]!;
        var armor = new StringBuilder("Armor:\n");
        foreach (string type in rules.List("ArmourTypes").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (zhArmor.TryGet(ArmorBase.GetValueOrDefault(type, "TankArmor"), out var b) && b.TryGet("Armor", out var list))
                foreach (var e in list.AsList())
                    if (RuleValue.Words(e.Value) is [var dt, var pct, ..])
                        entries[dt] = pct;
            foreach (string warhead in warheads)
                entries[DamageType(warhead)] = $"{(int)rules.Num(warhead, type, 100)}%";
            armor.Append($"  EM_Armor_{type}:\n    Armor:\n");
            foreach (var (dt, pct) in entries)
                armor.Append($"    - {dt} {pct}\n");
        }

        const string header = "# GENERATED by 'OpenWestwood.Tools export-emperor-factions' from Emperor: Battle for Dune's data in content/emperor.\n# Emperor's houses as Zero Hour engine factions. Edit freely, or re-export.\n";
        File.WriteAllText(Path.Combine(output, "factions.yaml"), header + factions);
        File.WriteAllText(Path.Combine(output, "objects.yaml"), header + objects);
        File.WriteAllText(Path.Combine(output, "effects.yaml"), header + effects);
        File.WriteAllText(Path.Combine(output, "weapons.yaml"), header + weapons);
        File.WriteAllText(Path.Combine(output, "locomotors.yaml"), header + locomotors);
        File.WriteAllText(Path.Combine(output, "armor.yaml"), header + armor);
        File.WriteAllText(Path.Combine(output, "command-sets.yaml"), header + commandSets);
        File.WriteAllText(Path.Combine(output, "command-buttons.yaml"), header + buttons);
        File.WriteAllText(Path.Combine(output, "upgrades.yaml"), header + upgrades);
        File.WriteAllText(Path.Combine(output, "object-creation-lists.yaml"), header + ocls);
        Console.WriteLine($"Wrote Emperor factions to {output} ({weaponDone.Count} weapons)");
        return 0;
    }

    /// <summary>
    /// A building's Occupy rows ('b' solid, 's' and 'n' free, 'd' / 'p' pads), as the half size of its solid cells
    /// and their middle from the footprint's middle, in world units (+Y north: the first row is the north one).
    /// </summary>
    static ((float X, float Y) Half, (float X, float Y) Center) Footprint(List<string> rows)
    {
        if (rows.Count == 0)
            rows = ["bb", "bb"];
        int width = rows.Max(r => r.Length), height = rows.Count;
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (char.ToLowerInvariant(rows[y][x]) is 'b' or 'd' or 'p')
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        if (maxX < 0)
            (minX, maxX, minY, maxY) = (0, width - 1, 0, height - 1);
        float cx = (minX + maxX + 1) / 2f - width / 2f, cy = (minY + maxY + 1) / 2f - height / 2f;
        return (((maxX - minX + 1) * TileUnits / 2, (maxY - minY + 1) * TileUnits / 2), (cx * TileUnits, -cy * TileUnits));
    }
}
