using System.Globalization;
using System.Text;
using OpenWestwood.Core.Rules;
using OpenWestwood.Formats.Westwood;
using OpenWestwood.TiberianDawn;

namespace OpenWestwood.Tools;

/// <summary>
/// export-ra2-factions: Red Alert 2 / Yuri's Revenge's Allies, Soviets and Yuri as Zero Hour engine factions
/// (mods/zh/rules/ra2/), from the game's own RULESMD.INI (with the 1.001 patch's) and ARTMD.INI (over RULES.INI and
/// ART.INI). Everything a side's countries own at multiplayer's top tech level (10) is exported.
///
/// Conversions (one RA2 cell = 36 world units; its INI times are in 15ths of a second):
///   speed      INI 0..100 is v*256/100 leptons a frame, x 15 x 36/256 (as Red Alert's)
///   range      cells x 36;  CellSpread cells x 36
///   fire rate  ROF frames x 1000/15 ms
///   armour     RA2's eleven armour classes become ZH armour sets; each warhead acts as the ZH damage type of its
///              kind, and a set's percentage for a type is the average of that kind's warheads' Verses.
/// </summary>
public static class Ra2FactionExporter
{
    const float CellUnits = 36f;
    const float SizeUnits = CellUnits * 0.9f;
    const float LeptonUnits = CellUnits / 256f;
    const float FramesPerSecond = 15f;
    const float InfantrySpeedFactor = 2f;
    const int BuildLevel = 10;

    static readonly string[] ArmorNames = ["none", "flak", "plate", "light", "medium", "heavy", "wood", "steel", "concrete", "special_1", "special_2"];
    static readonly string[] ArmorBase = ["HumanArmor", "HumanArmor", "HumanArmor", "TruckArmor", "TankArmor", "TankArmor", "StructureArmor", "StructureArmor", "StructureArmor", "TankArmor", "TankArmor"];

    sealed record Side(string Faction, string Prefix, string SideName, string BaseSide, string[] Countries, string DisplayName, string Color, string Score, string Logo);

    /// <summary>
    /// A Westwood game of this family (Red Alert 2 / Yuri's Revenge, Tiberian Sun / Firestorm): its content, INI files (each
    /// later one over the earlier), sides, and where its output goes. Tiberian Sun's output is the same as Red Alert 2's
    /// with its own names (TS_ for RA2_, TSCAMEO: for RA2CAMEO: ...).
    /// </summary>
    sealed record Game(string Tag, string Content, string Title, string Output, string[] Rules, string[] Art, string[] Sounds, string[] Eva,
        string Csf, string SoundFolder, string SoundExtension, bool PoisonResources, Func<Ra2Ini, Side[]> Sides);

    static readonly Game Ra2Game = new("RA2", "redalert2", "Red Alert 2", "ra2", ["RULES.INI", "RULESMD.INI"], ["ART.INI", "ARTMD.INI"],
        ["SOUND.INI", "SOUNDMD.INI"], ["EVA.INI", "EVAMD.INI"], "RA2MD.CSF", "RA2SFX", "wav", false, rules =>
        [
            new Side("FactionRA2Allies", "RA2ALL_", "RA2Allies", "USA", rules.List("Sides", "GDI"), "Allies (Red Alert 2)", "R:60 G:120 B:255", "America_ScoreScreen", "SAFactionLogoPage_US"),
            new Side("FactionRA2Soviet", "RA2SOV_", "RA2Soviet", "China", rules.List("Sides", "Nod"), "Soviets (Red Alert 2)", "R:220 G:30 B:30", "China_ScoreScreen", "SNFactionLogoPage_China"),
            new Side("FactionRA2Yuri", "RA2YUR_", "RA2Yuri", "GLA", rules.List("Sides", "ThirdSide"), "Yuri (Yuri's Revenge)", "R:170 G:40 B:200", "GLA_ScoreScreen", "SUFactionLogoPage_GLA"),
        ]);

    static readonly Game TsGame = new("TS", "tiberiansun", "Tiberian Sun", "ts", ["RULES.INI", "FIRESTRM.INI"], ["ART.INI", "ARTFS.INI"],
        ["SOUND.INI", "SOUND01.INI"], [], "", "TSSFX", "AUD", true, rules =>
        [
            new Side("FactionTSGDISide", "TSGDI_", "TSGDI", "USA", rules.List("Sides", "GDI"), "GDI (Tiberian Sun)", "R:230 G:190 B:40", "America_ScoreScreen", "SAFactionLogoPage_US"),
            new Side("FactionTSNodSide", "TSNOD_", "TSNod", "GLA", rules.List("Sides", "Nod"), "Nod (Tiberian Sun)", "R:200 G:20 B:20", "GLA_ScoreScreen", "SUFactionLogoPage_GLA"),
        ]);

    public static int Run(string root) => Export(root, Ra2Game);

    public static int RunTiberianSun(string root) => Export(root, TsGame);

    /// <summary>Tiberian Sun's names for the output (RA2_ to TS_ ...), and its draw modules told they are Tiberian Sun's.</summary>
    static string Named(Game g, string text) => g.Tag != "TS" ? text : text
        .Replace("Class: RA2SpriteDraw\n", "Class: RA2SpriteDraw\n        Game: TS\n")
        .Replace("RA2CAMEO:", "TSCAMEO:").Replace("RA2FX:", "TSFX:").Replace("Ra2Snd_", "TsSnd_").Replace("Ra2Eva_", "TsEva_")
        .Replace("Command_RA2_", "Command_TS_").Replace("Command_Ra2Repair", "Command_TsRepair").Replace("RA2TransportCommandSet", "TSTransportCommandSet")
        .Replace("RA2CIV_", "TSCIV_").Replace("FactionRA2", "FactionTS").Replace("RA2_", "TS_")
        .Replace("(Red Alert 2)", "(Tiberian Sun)");

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A name safe as a plain YAML value (no ": " or " #").</summary>
    static string Plain(string s) => s.Replace(": ", " - ").Replace(" #", " ").Trim();

    /// <summary>The countries' names as players know them (RULES.INI's house names are the code names).</summary>
    static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Americans"] = "America", ["Alliance"] = "Korea", ["French"] = "France", ["Germans"] = "Germany", ["British"] = "Great Britain",
        ["Russians"] = "Russia", ["Confederation"] = "Cuba", ["Africans"] = "Libya", ["Arabs"] = "Iraq", ["YuriCountry"] = "Yuri",
        ["GDI"] = "GDI", ["Nod"] = "Nod",
    };

    static string CountryFaction(string house) => "FactionRA2" + house;

    /// <summary>The ZH damage type a warhead acts as (by its name and what it does).</summary>
    static string DamageTypeOf(string warhead, Ra2Ini rules)
    {
        string w = warhead.ToUpperInvariant();
        if (rules.Bool(warhead, "Radiation") || rules.Get(warhead, "Radiation").Length > 0 && rules.Get(warhead, "Radiation") != "no" || w.Contains("RAD"))
            return "RADIATION";
        if (rules.Bool(warhead, "Psychedelic") || rules.Bool(warhead, "MindControl") || w.Contains("PSYCH") || w.Contains("MIND"))
            return "MICROWAVE";
        if (w.Contains("FIRE") || w.Contains("FLAME") || w.Contains("NAPALM"))
            return "FLAME";
        if (w.Contains("PRISM") || w.Contains("LASER"))
            return "LASER";
        if (w.Contains("ELEC") || w.Contains("TESLA") || w.Contains("SHOCK"))
            return "PARTICLE_BEAM";
        if (w.Contains("SNIPE") || w == "HOLLOWPOINT" || w.Contains("KILLER"))
            return "SNIPER";
        if (w.Contains("FLAK") || w.Contains("AA"))
            return "JET_MISSILES";
        if (w.Contains("ORGANIC") || w.Contains("DOG") || w.Contains("CHOMP") || w.Contains("BITE"))
            return "MELEE";
        if (w.Contains("POISON") || w.Contains("GAS") || w.Contains("VIRUS"))
            return "POISON";
        if (w.Contains("SA") || w.Contains("MG") || w.Contains("BULLET"))
            return "SMALL_ARMS";
        if (w.Contains("AP") || w.Contains("SABOT") || w.Contains("CANNON"))
            return "ARMOR_PIERCING";
        return "EXPLOSION";
    }

    static int Export(string root, Game game)
    {
        using var ra2 = TdContent.Load(root, game.Content);
        var rules = new Ra2Ini();
        var art = new Ra2Ini();
        foreach (string f in game.Rules)
            if (ra2.Files.Exists(f))
                rules.Read(Encoding.Latin1.GetString(ra2.Files.ReadAllBytes(f)));
        foreach (string f in game.Art)
            if (ra2.Files.Exists(f))
                art.Read(Encoding.Latin1.GetString(ra2.Files.ReadAllBytes(f)));
        if (Environment.GetEnvironmentVariable("OPENWESTWOOD_DUMP_INI") is { Length: > 0 } dump)
        {
            foreach (string s in dump.Split(','))
            {
                Console.WriteLine($"[{s}]");
                foreach (var (k, v) in rules.Section(s))
                    Console.WriteLine($"{k}={v}");
                foreach (var (k, v) in art.Section(s))
                    Console.WriteLine($"art:{k}={v}");
            }
            return 0;
        }
        // OPENWESTWOOD_DUMP_SHP="E1.SHP,start,step,out.png": the 8 facings of a sequence side by side (grey by palette index).
        if (Environment.GetEnvironmentVariable("OPENWESTWOOD_DUMP_SHP") is { Length: > 0 } shp)
        {
            var p = shp.Split(',');
            var shape = new TsShape(ra2.Files.ReadAllBytes(p[0]));
            int start = int.Parse(p[1]), step = int.Parse(p[2]);
            var sheet = new OpenWestwood.Formats.Images.Image(shape.Width * 8, shape.Height);
            byte[]? pal = p.Length > 4 && ra2.Files.Exists(p[4]) ? ra2.Files.ReadAllBytes(p[4]) : null;
            for (int f = 0; f < 8; f++)
            {
                var px = shape.Frame(start + f * step);
                for (int y = 0; y < shape.Height; y++)
                    for (int x = 0; x < shape.Width; x++)
                    {
                        byte v = px[y * shape.Width + x];
                        if (v == 0)
                            sheet.SetPixel(f * shape.Width + x, y, 255, 255, 255, 255);
                        else if (pal != null)
                            sheet.SetPixel(f * shape.Width + x, y, (byte)(pal[v * 3] * 4), (byte)(pal[v * 3 + 1] * 4), (byte)(pal[v * 3 + 2] * 4), 255);
                        else
                            sheet.SetPixel(f * shape.Width + x, y, (byte)(v % 64 * 3), (byte)(v / 64 * 60), (byte)(255 - v), 255);
                    }
            }
            OpenWestwood.Formats.Images.PngWriter.Save(sheet, p[3]);
            return 0;
        }
        var strings =game.Csf.Length > 0 && ra2.Files.Exists(game.Csf) ? CsfFile.Read(ra2.Files.ReadAllBytes(game.Csf)) : new();
        // The sounds: SOUND.INI / SOUNDMD.INI name each sound (a weapon's Report, a unit's voices) and its .WAVs in AUDIO.BAG.
        var soundIni = new Ra2Ini();
        foreach (string f in game.Sounds)
            if (ra2.Files.Exists(f))
                soundIni.Read(Encoding.Latin1.GetString(ra2.Files.ReadAllBytes(f)));
        var soundEvents = new StringBuilder("AudioEvents:\n");
        var soundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // An audio event for an RA2 sound ("" if it has none): its files (minus the INI's $ / @ / # marks) picked at random.
        string Sound(string name, bool voice)
        {
            if (name.Length == 0)
                return "";
            // Red Alert 2: a sound section lists its files; Tiberian Sun: the value is the files themselves ("15-I000,15-I004").
            bool listed = soundIni.Has(name) && soundIni.Get(name, "Sounds").Length > 0;
            if (!listed && game.Tag != "TS")
                return "";
            string key = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c == '_'));
            if (key.Length > 32)
            {
                uint hash = 2166136261; // FNV-1a: a stable short name for a long list
                foreach (char c in name)
                    hash = (hash ^ c) * 16777619;
                key = $"{key[..24]}{hash:X8}";
            }
            string evt = "Ra2Snd_" + key;
            if (!soundNames.Add(evt))
                return evt;
            var entries = listed ? soundIni.Get(name, "Sounds") : name;
            var files = entries.Split([' ', ',', '	'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimStart('$', '@', '#', '~')).Where(s => s.Length > 0)
                .Select(s => $"/{game.SoundFolder}/{s}.{game.SoundExtension}").ToList();
            int volume = (int)Math.Clamp(soundIni.Num(listed ? name : entries.Split(',')[0], "Volume", 100), 10, 120);
            if (files.Count == 0)
                return "";
            soundEvents.Append($"  {evt}:\n    Sounds: {string.Join(' ', files)}\n    Control: random\n    Priority: normal\n    Limit: {(voice ? 1 : 3)}\n    Volume: {volume}\n");
            soundEvents.Append(voice ? "    Type: ui voice player\n" : "    Type: world shrouded everyone\n");
            return evt;
        }
        string output = Path.Combine(root, "mods", "zh", "rules", game.Output);
        Directory.CreateDirectory(output);
        var zhArmor = RuleYaml.Parse(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "armor.yaml")), "armor.yaml")["Armor"]!;

        var sides = game.Sides(rules);

        var objects = new StringBuilder("Objects:\n");
        var weapons = new StringBuilder("Weapons:\n");
        var projectiles = new StringBuilder("Objects:\n");
        var locomotors = new StringBuilder("Locomotors:\n");
        var commandSets = new StringBuilder("CommandSets:\n");
        var buttons = new StringBuilder("CommandButtons:\n");
        var armor = new StringBuilder("Armor:\n");
        var factions = new StringBuilder("PlayerTemplates:\n");
        buttons.Append("  Command_Ra2Repair:\n    Command: TD_REPAIR\n    TextLabel: Repair\n    ButtonImage: SSRepair\n    ButtonBorderType: ACTION\n    DescriptLabel: Repair this building (costs money as it mends)\n");
        commandSets.Append("  RA2TransportCommandSet:\n    1: Command_TransportExit\n    2: Command_TransportExit\n    3: Command_TransportExit\n    4: Command_TransportExit\n    5: Command_TransportExit\n    9: Command_Evacuate\n    11: Command_AttackMove\n    13: Command_Guard\n    14: Command_Stop\n");

        // ---- Armour: per RA2 armour class, each damage type at its warheads' average Verses ----
        var warheads = rules.TypeList("Warheads").Concat(rules.Sections.Where(s => rules.Get(s, "Verses").Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int column = 0; column < ArmorNames.Length; column++)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (zhArmor.TryGet(ArmorBase[column], out var b) && b.TryGet("Armor", out var list))
                foreach (var e in list.AsList())
                    if (RuleValue.Words(e.Value) is [var dt, var pct, ..])
                        entries[dt] = pct;
            foreach (var group in warheads.GroupBy(w => DamageTypeOf(w, rules)))
            {
                var values = group.Select(w => rules.List(w, "Verses")).Where(v => v.Length > column)
                    .Select(v => float.TryParse(v[column].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float p) ? p : 100).ToList();
                if (values.Count > 0)
                    entries[group.Key] = $"{(int)values.Average()}%";
            }
            armor.Append($"  RA2_Armor_{ArmorNames[column]}:\n    Armor:\n");
            foreach (var (dt, pct) in entries)
                armor.Append($"    - {dt} {pct}\n");
        }
        string ArmorName(string a) => ArmorNames.Contains(a.ToLowerInvariant()) ? $"RA2_Armor_{a.ToLowerInvariant()}" : "RA2_Armor_none";

        // ---- Weapons ----
        var weaponNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Weapon(string raName)
        {
            if (raName.Length == 0 || !rules.Has(raName))
                return null;
            if (weaponNames.TryGetValue(raName, out var done))
                return done;
            string name = "RA2_Weapon_" + raName.Replace('-', '_').Replace(' ', '_');
            weaponNames[raName] = name;
            string warhead = rules.Get(raName, "Warhead"), projectile = rules.Get(raName, "Projectile");
            float damage = rules.Num(raName, "Damage");
            // A spawner (the V3's, Dreadnought's and Boomer's launchers) launches its unit's Spawns= missile, whose damage and
            // warhead are [General]'s <rocket>Damage and [CombatDamage]'s <rocket>Warhead (the rocket named by <rocket>Type).
            string missile = "", missilePrefix = "";
            if (rules.Bool(raName, "Spawner"))
            {
                foreach (string s in rules.Sections)
                    if ((rules.Get(s, "Primary").Equals(raName, StringComparison.OrdinalIgnoreCase) || rules.Get(s, "Secondary").Equals(raName, StringComparison.OrdinalIgnoreCase))
                        && rules.Get(s, "Spawns") is { Length: > 0 } spawns)
                    {
                        missile = spawns;
                        break;
                    }
                foreach (var (key, value) in rules.Section("General"))
                    if (missile.Length > 0 && key.EndsWith("Type", StringComparison.OrdinalIgnoreCase) && value.Equals(missile, StringComparison.OrdinalIgnoreCase)
                        && rules.Section("General").ContainsKey(key[..^4] + "Damage"))
                        missilePrefix = key[..^4];
                if (missilePrefix.Length > 0)
                {
                    damage = rules.Num("General", missilePrefix + "Damage", damage);
                    // ([CombatDamage] names the V3's V3Warhead, not V3RocketWarhead.)
                    string shortPrefix = missilePrefix.EndsWith("Rocket", StringComparison.OrdinalIgnoreCase) ? missilePrefix[..^6] : missilePrefix;
                    warhead = rules.Get("CombatDamage", missilePrefix + "Warhead", rules.Get("CombatDamage", shortPrefix + "Warhead", warhead));
                }
            }
            string dt = damage < 0 ? "HEALING" : DamageTypeOf(warhead, rules);
            float spread = Math.Max(rules.Num(warhead, "CellSpread", 0.2f) * CellUnits, 3);
            float range = Math.Max(rules.Num(raName, "Range", 1) * CellUnits, 15);
            int burst = (int)Math.Max(1, rules.Num(raName, "Burst", 1));
            float rof = Math.Max(rules.Num(raName, "ROF", 15), 1) * 1000 / FramesPerSecond;
            weapons.Append($"  {name}:\n    PrimaryDamage: {F(Math.Abs(damage))}\n    PrimaryDamageRadius: {F(Math.Min(spread, 60))}\n");
            // (A rocket's launch sound is its own AuxSound1.)
            if (Sound(rules.Get(raName, "Report", missilePrefix.Length > 0 ? rules.Get(missile, "AuxSound1") : ""), false) is { Length: > 0 } report)
                weapons.Append($"    FireSound: {report}\n");
            weapons.Append($"    AttackRange: {F(range)}\n    DamageType: {dt}\n    DeathType: {(dt == "FLAME" ? "BURNED" : dt is "EXPLOSION" or "ARMOR_PIERCING" ? "EXPLODED" : dt is "LASER" or "PARTICLE_BEAM" ? "LASERED" : "NORMAL")}\n");
            if (burst > 1)
                weapons.Append($"    ClipSize: {burst}\n    ClipReloadTime: {(int)rof}\n    DelayBetweenShots: 150\n");
            else
                weapons.Append($"    DelayBetweenShots: {(int)rof}\n    ClipSize: 0\n    ClipReloadTime: 0\n");
            weapons.Append(dt == "HEALING" ? "    RadiusDamageAffects: ALLIES SELF\n" : "    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n");
            bool antiAir = rules.Bool(projectile, "AA"), antiGround = rules.Bool(projectile, "AG", true);
            weapons.Append($"    AntiGround: {(antiGround ? "Yes" : "No")}\n    AntiAirborneVehicle: {(antiAir ? "Yes" : "No")}\n    AntiAirborneInfantry: {(antiAir ? "Yes" : "No")}\n");
            // Its explosion: the warhead's first AnimList animation (an RA2 shape in the animation palette).
            string blast = rules.List(warhead, "AnimList").FirstOrDefault() ?? "";
            string image = rules.Get(projectile, "Image");
            bool visible = image.Length > 0 && !image.Equals("none", StringComparison.OrdinalIgnoreCase) && !rules.Bool(projectile, "Inviso");
            if (missilePrefix.Length > 0)
            {
                // The rocket itself (its own voxel, e.g. V3ROCKET), climbing to <rocket>Altitude and coming down on the target.
                string rocketImage = rules.Get(missile, "Image", missile);
                string proj = $"RA2_Projectile_Rocket_{rocketImage}".Replace('-', '_');
                float altitude = rules.Num("General", missilePrefix + "Altitude", 768) * LeptonUnits;
                // RocketLocomotion accelerates the whole way; this is roughly its average speed over a full-range flight.
                float speed = Math.Max(rules.Num(missile, "Speed", 15) * 256 / 100f * FramesPerSecond * LeptonUnits * 2.5f, 150);
                weapons.Append($"    ProjectileObject: {proj}\n    WeaponSpeed: {F(speed)}\n");
                if (blast.Length > 0)
                    weapons.Append($"    ProjectileDetonationFX: RA2FX:{blast}\n");
                if (projectileNames.Add(proj))
                {
                    projectiles.Append($"  {proj}:\n    DisplayName: {Plain(rules.Get(missile, "Name", missile))}\n    EditorSorting: SYSTEM\n    VisionRange: 0.0\n    KindOf: PROJECTILE\n");
                    projectiles.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    ArmorSet:\n      Armor: ProjectileArmor\n    Modules:\n");
                    projectiles.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {rocketImage}\n        Kind: Bullet\n        Voxel: {(art.Bool(rocketImage, "Voxel") || art.Bool(missile, "Voxel") ? "Yes" : "No")}\n");
                    projectiles.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
                    projectiles.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                    projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n        FirstHeight: {F(altitude)}\n        SecondHeight: {F(altitude)}\n        FirstPercentIndent: 20%\n        SecondPercentIndent: 75%\n        FlightPathAdjustDistPerSecond: 100\n        OrientToFlightPath: Yes\n");
                    projectiles.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
                }
            }
            else if (rules.Bool(raName, "IsElectricBolt"))
            {
                // The Tesla Trooper's, Tesla Tank's and Tesla Coil's bolt, drawn by the client ("ZAP").
                weapons.Append($"    FireFX: RA2FX:ZAP|{blast}\n");
            }
            else if (!visible)
            {
                if (blast.Length > 0)
                    weapons.Append($"    FireFX: RA2FX:|{blast}\n");
            }
            else
            {
                string proj = $"RA2_Projectile_{projectile}_{image}".Replace('-', '_');
                float speed = Math.Max(rules.Num(raName, "Speed", 40) * 256 / 100f * FramesPerSecond * LeptonUnits, 80);
                weapons.Append($"    ProjectileObject: {proj}\n    WeaponSpeed: {F(speed)}\n");
                if (blast.Length > 0)
                    weapons.Append($"    ProjectileDetonationFX: RA2FX:{blast}\n");
                if (projectileNames.Add(proj))
                {
                    bool homing = rules.Num(projectile, "ROT") > 0;
                    projectiles.Append($"  {proj}:\n    DisplayName: RA2 projectile\n    EditorSorting: SYSTEM\n    VisionRange: 0.0\n");
                    projectiles.Append(homing ? "    KindOf: PROJECTILE SMALL_MISSILE\n" : "    KindOf: PROJECTILE\n");
                    if (homing)
                        projectiles.Append($"    Locomotor: SET_NORMAL {proj}Locomotor\n");
                    projectiles.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    ArmorSet:\n      Armor: ProjectileArmor\n    Modules:\n");
                    projectiles.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {image}\n        Kind: Bullet\n        Voxel: {(art.Bool(image, "Voxel") ? "Yes" : "No")}\n");
                    projectiles.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
                    projectiles.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                    if (homing)
                    {
                        projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: MissileAIUpdate\n        TryToFollowTarget: Yes\n        FuelLifetime: 10000\n        InitialVelocity: {F(speed)}\n        IgnitionDelay: 0\n        DistanceToTravelBeforeTurning: 5\n        DistanceToTargetForLock: 60\n");
                        locomotors.Append($"  {proj}Locomotor:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    MinSpeed: 30\n    Acceleration: 400\n    Braking: 0\n    TurnRate: 180\n    MaxThrustAngle: 30\n    AllowAirborneMotiveForce: Yes\n    Appearance: THRUST\n    CloseEnoughDist: 1\n");
                    }
                    else
                    {
                        float arc = rules.Bool(projectile, "Arcing") ? 60 : 5;
                        projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n        FirstHeight: {F(arc)}\n        SecondHeight: {F(arc)}\n        FirstPercentIndent: 30%\n        SecondPercentIndent: 70%\n        FlightPathAdjustDistPerSecond: 100\n");
                    }
                    projectiles.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
                }
            }
            return name;
        }

        // Generic prerequisites ([General] PrerequisitePower...): POWER, FACTORY, BARRACKS, RADAR, TECH, PROC.
        var generic = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["POWER"] = rules.List("General", "PrerequisitePower"), ["FACTORY"] = rules.List("General", "PrerequisiteFactory"),
            ["BARRACKS"] = rules.List("General", "PrerequisiteBarracks"), ["RADAR"] = rules.List("General", "PrerequisiteRadar"),
            ["TECH"] = rules.List("General", "PrerequisiteTech"),
            ["PROC"] = [.. rules.List("General", "PrerequisiteProc"), .. rules.List("General", "PrerequisiteProcAlternate")],
        };
        // Any other [General] PrerequisiteX (Firestorm's PrerequisiteGDIFactory: GDIFACTORY).
        foreach (var (key, _) in rules.Section("General"))
            if (key.StartsWith("Prerequisite", StringComparison.OrdinalIgnoreCase) && key.Length > 12 && !key.EndsWith("Alternate", StringComparison.OrdinalIgnoreCase))
                generic.TryAdd(key[12..].ToUpperInvariant(), rules.List("General", key));

        var powers = new StringBuilder("SpecialPowers:\n");
        var ocls = new StringBuilder("ObjectCreationLists:\n");
        var exportedPowers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        float Seconds(string section, string key, float fallbackFrames) => rules.Num(section, key, fallbackFrames) / FramesPerSecond;

        foreach (var side in sides)
        {
            var hunters = new List<(string Name, string Type)>();
            bool Own(string type)
            {
                if (!rules.Has(type))
                    return false;
                int level = (int)rules.Num(type, "TechLevel", -1);
                if (level < 0 || level > BuildLevel)
                    return false;
                return rules.List(type, "Owner").Any(o => side.Countries.Contains(o, StringComparer.OrdinalIgnoreCase));
            }
            var buildingTypes = rules.TypeList("BuildingTypes").Where(Own).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var vehicleTypes = rules.TypeList("VehicleTypes").Where(Own).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var infantryTypes = rules.TypeList("InfantryTypes").Where(Own).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var aircraftTypes = rules.TypeList("AircraftTypes").Where(Own).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // The side's construction yard and MCV (its MCV's DeploysInto).
            string? mcv = rules.TypeList("VehicleTypes").FirstOrDefault(v => rules.Get(v, "DeploysInto") is { Length: > 0 } d && rules.Bool(d, "ConstructionYard") && Own(v));
            string? yard = mcv != null ? rules.Get(mcv, "DeploysInto") : null;
            if (yard != null && !buildingTypes.Contains(yard, StringComparer.OrdinalIgnoreCase))
                buildingTypes.Insert(0, yard);
            var names = buildingTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string N(string t) => side.Prefix + t.ToUpperInvariant();
            string Name(string t) => rules.Get(t, "UIName") is { Length: > 0 } ui && strings.TryGetValue(ui, out var s) && s.Length > 0 ? Plain(s) : Plain(rules.Get(t, "Name", t));
            string Image(string t) => rules.Get(t, "Image") is { Length: > 0 } i ? i : t;

            string Prerequisites(string t)
            {
                var lines = new List<string>();
                foreach (string need in rules.List(t, "Prerequisite"))
                {
                    var alternatives = (generic.TryGetValue(need, out var g) ? g : [need]).Where(names.Contains).Select(N).ToList();
                    if (alternatives.Count > 0)
                        lines.Add(string.Join(' ', alternatives));
                    else if (!names.Contains(need) && !generic.ContainsKey(need))
                        return "UNBUILDABLE"; // needs something this side never has
                }
                // Country units (RequiredHouses / ForbiddenHouses): only those countries' factions build them.
                var owners = rules.List(t, "Owner");
                var allowed = side.Countries.Where(c => owners.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (rules.List(t, "RequiredHouses") is { Length: > 0 } required)
                    allowed = allowed.Where(c => required.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                allowed = allowed.Where(c => !rules.List(t, "ForbiddenHouses").Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                bool countryOnly = allowed.Count < side.Countries.Length;
                if (allowed.Count == 0)
                    return "UNBUILDABLE";
                // Yuri's Revenge stolen-tech units (Chrono Commando, Psi-Commando, ...): a spy must first infiltrate that
                // side's Battle Lab, which gives the player the matching upgrade.
                var stolen = new[] { ("RequiresStolenAlliedTech", "Upgrade_RA2StolenAlliedTech"), ("RequiresStolenSovietTech", "Upgrade_RA2StolenSovietTech"),
                    ("RequiresStolenThirdTech", "Upgrade_RA2StolenThirdTech") }.Where(x => rules.Bool(t, x.Item1)).Select(x => x.Item2).ToList();
                if (lines.Count == 0 && !countryOnly && stolen.Count == 0)
                    return "    Prerequisites: {}\n";
                var sb = new StringBuilder("    Prerequisites:\n");
                if (lines.Count > 0)
                    sb.Append("      Object:\n");
                foreach (var l in lines)
                    sb.Append($"      - {l}\n");
                if (countryOnly)
                    sb.Append($"      Faction:\n      - {string.Join(' ', allowed.Select(CountryFaction))}\n");
                if (stolen.Count > 0)
                    sb.Append("      Upgrade:\n").Append(string.Concat(stolen.Select(u => $"      - {u}\n")));
                return sb.ToString();
            }

            // A building's superweapons (SuperWeapon / SuperWeapon2 in RULESMD.INI's SuperWeaponTypes):
            // (power name, button, module class, module settings, OCL nuggets or null, targeted).
            List<(string Power, string Button, string Module, string Settings, string? Ocl, bool Targeted)> SuperweaponsOf(string b)
            {
                var list = new List<(string, string, string, string, string?, bool)>();
                foreach (string sw in new[] { rules.Get(b, "SuperWeapon"), rules.Get(b, "SuperWeapon2") }.Where(s => s.Length > 0 && rules.Has(s)))
                {
                    string type = rules.Get(sw, "Type");
                    string power = $"RA2_Superweapon_{sw}";
                    string module = "Ra2Superweapon", settings = "";
                    string? ocl = null;
                    bool targeted = true;
                    float CellSpread(string warhead, float fallback) => rules.Num(warhead, "CellSpread", fallback);
                    switch (type)
                    {
                        case "MultiMissile" or "ChemMissile":
                        {
                            // A missile: its payload's damage where it lands (RA2's nuke: NukePayload, NUKE warhead, NUKEANIM;
                            // Tiberian Sun's multi-missile and chemical missile: their WeaponType's).
                            string launcher = rules.Get(sw, "WeaponType");
                            string payload = rules.Num(launcher, "Damage") > 0 ? launcher : "NukePayload";
                            float damage = rules.Num(payload, "Damage", 600);
                            string warhead = rules.Get(payload, "Warhead", "NUKE");
                            string anim = rules.List(warhead, "AnimList").FirstOrDefault() ?? (game.Tag == "TS" ? "" : "NUKEANIM");
                            string weaponName = $"RA2_Weapon_{payload}";
                            if (weaponNames.TryAdd(weaponName, weaponName))
                            {
                                weapons.Append($"  {weaponName}:\n    PrimaryDamage: {F(damage)}\n    PrimaryDamageRadius: {F(CellUnits * 3)}\n    SecondaryDamage: {F(damage / 2)}\n    SecondaryDamageRadius: {F(CellUnits * Math.Min(CellSpread(warhead, 10), 10) * 0.7f)}\n");
                                weapons.Append($"    AttackRange: 99999\n    DamageType: {DamageTypeOf(warhead, rules)}\n    DeathType: BURNED\n    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n");
                                if (anim.Length > 0)
                                    weapons.Append($"    FireFX: RA2FX:{anim}\n");
                            }
                            module = "OCLSpecialPower";
                            ocl = $"    - Type: FireWeapon\n      Weapon: {weaponName}\n";
                            break;
                        }
                        case "IonCannon":
                        {
                            // Tiberian Sun's Ion Cannon: IonCannonDamage with the IonCannonWH warhead, the beam (IonBeam) and blast (IonBlast).
                            string warhead = rules.Get("CombatDamage", "IonCannonWarhead", rules.Get("General", "IonCannonWarhead", "IonCannonWH"));
                            float damage = rules.Num("CombatDamage", "IonCannonDamage", rules.Num("General", "IonCannonDamage", 750));
                            if (weaponNames.TryAdd("RA2_Weapon_IonCannon", "RA2_Weapon_IonCannon"))
                            {
                                weapons.Append($"  RA2_Weapon_IonCannon:\n    PrimaryDamage: {F(damage)}\n    PrimaryDamageRadius: {F(CellUnits * 1.5f)}\n    SecondaryDamage: {F(damage / 3)}\n    SecondaryDamageRadius: {F(CellUnits * 3)}\n");
                                weapons.Append($"    AttackRange: 99999\n    DamageType: {DamageTypeOf(warhead, rules)}\n    DeathType: LASERED\n    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n");
                                weapons.Append($"    FireFX: RA2FX:{rules.Get("AudioVisual", "IonBeam", "IONBEAM")}|{rules.Get("AudioVisual", "IonBlast", "RING1")}\n");
                            }
                            module = "OCLSpecialPower";
                            ocl = "    - Type: FireWeapon\n      Weapon: RA2_Weapon_IonCannon\n";
                            break;
                        }
                        case "DropPod":
                        {
                            // Firestorm's drop pods: DropPodInfantryMinimum-Maximum riflemen from the sky.
                            int count = (int)((rules.Num("General", "DropPodInfantryMinimum", 3) + rules.Num("General", "DropPodInfantryMaximum", 5)) / 2);
                            module = "OCLSpecialPower";
                            ocl = $"    - Type: CreateObject\n      ObjectNames: {N("E1")}\n      Count: {count}\n      Offset: X:0 Y:0 Z:0\n";
                            break;
                        }
                        case "LightningStorm":
                        {
                            string warhead = rules.Get("General", "LightningWarhead", "IonWH");
                            var bolts = rules.List("AudioVisual", "WeatherConBolts");
                            string blast = rules.Get("AudioVisual", "WeatherConBoltExplosion");
                            if (weaponNames.TryAdd("RA2_Weapon_LightningBolt", "RA2_Weapon_LightningBolt"))
                            {
                                weapons.Append($"  RA2_Weapon_LightningBolt:\n    PrimaryDamage: {F(rules.Num("General", "LightningDamage", 250))}\n    PrimaryDamageRadius: {F(CellUnits * CellSpread(warhead, 2) * 0.5f)}\n");
                                weapons.Append($"    AttackRange: 99999\n    DamageType: {DamageTypeOf(warhead, rules)}\n    DeathType: LASERED\n    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n    FireFX: RA2FX:{bolts.FirstOrDefault() ?? "WCLBOLT1"}|{blast}\n");
                            }
                            settings = $"        Mode: LightningStorm\n        Weapon: RA2_Weapon_LightningBolt\n        Deferment: {F(Seconds("General", "LightningDeferment", 250))}\n        Duration: {F(Seconds("General", "LightningStormDuration", 180))}\n" +
                                       $"        HitDelay: {F(Seconds("General", "LightningHitDelay", 10))}\n        Spread: {F(rules.Num("General", "LightningCellSpread", 10))}\n        CloudFX: RA2FX:{rules.List("AudioVisual", "WeatherConClouds").FirstOrDefault() ?? "WCCLOUD1"}\n";
                            break;
                        }
                        case "IronCurtain":
                            settings = $"        Mode: IronCurtain\n        Radius: 1.5\n        Duration: {F(Seconds("CombatDamage", "IronCurtainDuration", 750))}\n        FX: RA2FX:{rules.Get("General", "IronCurtainInvokeAnim", "IRONBLST")}\n";
                            break;
                        case "ChronoSphere":
                            settings = $"        Mode: ChronoSphere\n        Radius: 1.5\n        FX: RA2FX:{rules.Get("General", "ChronoBlast", "CHRONOFD")}\n        ArriveFX: RA2FX:{rules.Get("General", "ChronoBlastDest", "CHRONOTG")}\n";
                            break;
                        case "PsychicDominator":
                            settings = $"        Mode: PsychicDominator\n        Radius: {F(CellSpread(rules.Get("General", "DominatorWarhead", "DominatorWH"), 4))}\n        Damage: {F(rules.Num("General", "DominatorDamage", 1000))}\n        FX: RA2FX:{rules.Get("General", "DominatorSecondAnim", "PDFXLOC")}\n";
                            break;
                        case "GeneticConverter":
                            settings = $"        Mode: GeneticConverter\n        Radius: 2.5\n        Unit: {N("BRUTE")}\n        FX: RA2FX:{rules.Get("General", "IronCurtainInvokeAnim", "IRONBLST")}\n";
                            break;
                        case "ForceShield":
                            settings = $"        Mode: ForceShield\n        Radius: {F(rules.Num("General", "ForceShieldRadius", 4))}\n        Duration: {F(Seconds("General", "ForceShieldDuration", 500))}\n        Blackout: {F(Seconds("General", "ForceShieldBlackoutDuration", 1000))}\n        FX: RA2FX:{rules.Get("General", "ForceShieldInvokeAnim", "FORCSHLD")}\n";
                            break;
                        case "ParaDrop" or "AmerParaDrop":
                        {
                            // Paratroopers: [General] AllyParaDropInf/Num, SovParaDropInf/Num, YuriParaDropInf/Num (America's own).
                            string who = type == "AmerParaDrop" ? "Amer" : side.SideName switch { "RA2Soviet" => "Sov", "RA2Yuri" => "Yuri", _ => "Ally" };
                            string inf = rules.List("General", who + "ParaDropInf").FirstOrDefault() ?? "E1";
                            int count = (int)(rules.List("General", who + "ParaDropNum").Select(n => float.TryParse(n, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0).FirstOrDefault() is > 0 and var c ? c : 6);
                            module = "OCLSpecialPower";
                            ocl = $"    - Type: CreateObject\n      ObjectNames: {N(inf)}\n      Count: {count}\n      Offset: X:0 Y:0 Z:0\n";
                            break;
                        }
                        case "SpyPlane" or "PsychicReveal":
                            module = "OCLSpecialPower";
                            ocl = "";
                            break;
                        case "EMPulse":
                        {
                            // Tiberian Sun's EMP Cannon (EMPulseCannon=yes): it fires its Primary weapon's pulse at the place; vehicles and
                            // buildings there are knocked out for the weapon's Damage in frames (EMEffect=yes warhead: no damage).
                            string pulse = rules.Get(b, "Primary", "EMPulseWeapon");
                            string warhead = rules.Get(pulse, "Warhead", "EMPuls");
                            settings = $"        Mode: EMPulse\n        Radius: {F(rules.Num(warhead, "Spread", 11) / 2)}\n        Duration: {F(rules.Num(pulse, "Damage", 1200) / FramesPerSecond)}\n" +
                                       $"        FX: RA2FX:{rules.List(warhead, "AnimList").FirstOrDefault() ?? "PULSEFX1"}\n";
                            if (Sound(rules.Get(pulse, "Report"), false) is { Length: > 0 } report)
                                settings += $"        Sound: {report}\n";
                            break;
                        }
                        case "HunterSeeker":
                        {
                            // The Hunter-Seeker droid ([General] GDIHunterSeeker / NodHunterSeeker): launched with no target, it picks one.
                            string hunter = rules.Get("General", side.SideName.Contains("Nod", StringComparison.OrdinalIgnoreCase) ? "NodHunterSeeker" : "GDIHunterSeeker");
                            if (!rules.Has(hunter))
                                continue;
                            settings = $"        Mode: HunterSeeker\n        Unit: {N(hunter)}\n";
                            hunters.Add((N(hunter), hunter));
                            targeted = false;
                            break;
                        }
                        case "Firestorm":
                        {
                            // Firestorm's generator: its wall sections burn for the charge time times ChargeToDrainRatio.
                            float charged = rules.Num(sw, "RechargeTime", 3) * 60 * rules.Num("General", "ChargeToDrainRatio", 0.333f);
                            settings = $"        Mode: Firestorm\n        Duration: {F(charged)}\n";
                            targeted = false;
                            break;
                        }
                        default:
                            continue; // ChronoWarp (the Chronosphere's second click), UnitDelivery
                    }
                    list.Add((power, $"Command_{power}", module, settings, ocl, targeted));
                    if (!exportedPowers.Add(power))
                        continue;
                    float reloadMs = rules.Num(sw, "RechargeTime", 5) * 60_000;
                    bool reveal = type is "SpyPlane" or "PsychicReveal";
                    float cursor = type switch { "MultiMissile" => CellUnits * 5, "LightningStorm" => CellUnits * rules.Num("General", "LightningCellSpread", 10) * 0.5f, "ForceShield" => CellUnits * rules.Num("General", "ForceShieldRadius", 4), "PsychicDominator" => CellUnits * 4, "GeneticConverter" => CellUnits * 2.5f, _ => CellUnits * 1.5f };
                    powers.Append($"  {power}:\n    Enum: SPECIAL_{power.ToUpperInvariant()}\n    ReloadTime: {(int)reloadMs}\n    PublicTimer: {(rules.Bool(sw, "ShowTimer") ? "Yes" : "No")}\n");
                    powers.Append($"    SharedSyncedTimer: No\n    ViewObjectDuration: {(reveal ? 30000 : 10000)}\n    ViewObjectRange: {(reveal ? F(CellUnits * 10) : "150")}\n");
                    powers.Append($"    RadiusCursorRadius: {F(cursor)}\n    ShortcutPower: Yes\n");
                    string label = rules.Get(sw, "UIName") is { Length: > 0 } ui && strings.TryGetValue(ui, out var shown) && shown.Length > 0 ? Plain(shown) : Plain(rules.Get(sw, "Name", sw));
                    buttons.Append($"  Command_{power}:\n    Command: SPECIAL_POWER\n    SpecialPower: {power}\n    Options: {(targeted ? "NEED_TARGET_POS CONTEXTMODE_COMMAND" : "OK_FOR_MULTI_SELECT")}\n");
                    buttons.Append($"    TextLabel: {label}\n    ButtonImage: RA2CAMEO:{rules.Get(sw, "SidebarImage", sw)}\n    ButtonBorderType: ACTION\n    DescriptLabel: {label}\n");
                }
                return list;
            }

            // What a spy does in a building (Spyable=yes): a Battle Lab's side tech, power, money, superweapon, radar.
            string? SpyEffect(string b)
            {
                if (!rules.Bool(b, "Spyable"))
                    return null;
                if (b.EndsWith("TECH", StringComparison.OrdinalIgnoreCase))
                    return "StolenTech " + (int)rules.Num(b, "AIBasePlanningSide", 0) switch { 1 => "Upgrade_RA2StolenSovietTech", 2 => "Upgrade_RA2StolenThirdTech", _ => "Upgrade_RA2StolenAlliedTech" };
                if (rules.Get(b, "SuperWeapon").Length > 0)
                    return "Superweapon";
                string makes = rules.Get(b, "Factory");
                if (makes.Equals("InfantryType", StringComparison.OrdinalIgnoreCase))
                    return "Veteran Upgrade_RA2VeteranInfantry";
                if (makes.Equals("UnitType", StringComparison.OrdinalIgnoreCase))
                    return "Veteran Upgrade_RA2VeteranVehicles";
                if (rules.Num(b, "Power") > 0)
                    return "Power";
                if (IsRefinery(b))
                    return "Money";
                if (rules.Bool(b, "Radar"))
                    return "Radar";
                return null;
            }

            string Button(string t)
            {
                string name = $"Command_RA2_{side.Prefix}{t.ToUpperInvariant()}";
                buttons.Append($"  {name}:\n    Command: UNIT_BUILD\n    Object: {N(t)}\n    TextLabel: {Name(t)}\n    ButtonImage: RA2CAMEO:{t}\n    ButtonBorderType: BUILD\n    DescriptLabel: {Name(t)}\n");
                return name;
            }

            string CommandSet(string name, IEnumerable<string> entries)
            {
                commandSets.Append($"  {name}:\n");
                int slot = 1;
                foreach (var e in entries)
                    commandSets.Append($"    {slot++}: {e}\n");
                return name;
            }

            bool Naval(string t) => rules.Bool(t, "Naval");
            // A refinery: Refinery=yes, or Yuri's (ResourceDestination=yes, its slaves bring the ore to it).
            bool IsRefinery(string t) => rules.Bool(t, "Refinery") || rules.Bool(t, "ResourceDestination") && rules.Get(t, "Enslaves").Length > 0;
            var buildable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in vehicleTypes.Concat(infantryTypes).Concat(aircraftTypes).Concat(buildingTypes))
                if (Prerequisites(t) != "UNBUILDABLE")
                    buildable.Add(t);
            if (yard != null)
                buildable.Add(yard);

            var slaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ---- Buildings ----
            foreach (string b in buildingTypes.Where(buildable.Contains))
            {
                string image = Image(b);
                var foundation = art.Get(image, "Foundation", "1x1").ToLowerInvariant().Split('x');
                int fw = int.TryParse(foundation[0], out int a) ? a : 1, fh = foundation.Length > 1 && int.TryParse(foundation[1], out int c) ? c : 1;
                int cost = (int)rules.Num(b, "Cost");
                float hp = rules.Num(b, "Strength", 500);
                string factory = rules.Get(b, "Factory");
                bool isYard = rules.Bool(b, "ConstructionYard");
                // (The EMP Cannon's weapon is its superweapon's pulse, fired only when the player uses it.)
                string? primary = rules.Bool(b, "EMPulseCannon") ? null : Weapon(rules.Get(b, "Primary"));
                string role = isYard ? "STRUCT_CONST" : IsRefinery(b) ? "STRUCT_REFINERY" : rules.Bool(b, "Radar") ? "STRUCT_RADAR" : factory.Equals("InfantryType", StringComparison.OrdinalIgnoreCase) ? "STRUCT_BARRACKS"
                    : factory.Equals("UnitType", StringComparison.OrdinalIgnoreCase) && !Naval(b) ? "STRUCT_WEAP" : factory.Equals("AircraftType", StringComparison.OrdinalIgnoreCase) ? "STRUCT_HELIPAD"
                    : rules.Num(b, "Power") > 0 ? "STRUCT_POWER" : rules.Bool(b, "Radar") ? "STRUCT_RADAR" : primary != null ? (rules.Bool(rules.Get(rules.Get(b, "Primary"), "Projectile"), "AA") ? "STRUCT_SAM" : "STRUCT_TURRET")
                    : rules.Bool(b, "AIBuildThis") && !rules.Bool(b, "Wall") && !rules.Bool(b, "WaterBound") && !Naval(b) ? "STRUCT_TECH" : "STRUCT_RA2";
                // Tiberian Sun's add-ons (a power turbine, an upgrade centre's upgrades) go on another building: not for the AI to build alone.
                if (rules.Get(b, "PowersUpBuilding").Length > 0)
                    role = "STRUCT_UPGRADE";
                objects.Append($"  {N(b)}:\n    DisplayName: {Name(b)}\n    Side: {side.SideName}\n    EditorSorting: STRUCTURE\n    ButtonImage: RA2CAMEO:{b}\n    SelectPortrait: RA2CAMEO:{b}\n");
                objects.Append($"    BuildCost: {cost}\n    BuildTime: {F(Math.Max(2, cost / 100f * 1.6f))}\n    TdRole: {role}\n");
                int power = (int)rules.Num(b, "Power");
                if (power != 0)
                    objects.Append($"    EnergyProduction: {power}\n");
                float sight = Math.Max(rules.Num(b, "Sight", 4), 2) * CellUnits;
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n    RadarPriority: STRUCTURE\n");
                if (Sound(rules.Get(b, "DieSound"), false) is { Length: > 0 } buildingDie)
                    objects.Append($"    SoundDie: {buildingDie}\n");
                var kinds = new List<string> { "PRELOAD", "STRUCTURE", "SELECTABLE", "IMMOBILE", "SCORE", "TD_OBJECT" };
                if (!rules.Bool(b, "Wall"))
                    kinds.Add("MP_COUNT_FOR_VICTORY");
                else
                    kinds.Add("WALK_ON_TOP_OF_WALL");
                if (rules.Bool(b, "Capturable"))
                    kinds.Add("CAPTURABLE");
                if (isYard)
                    kinds.AddRange(["COMMANDCENTER", "FS_FACTORY", "AUTO_RALLYPOINT"]);
                if (power > 0)
                    kinds.Add("FS_POWER");
                if (factory.Equals("InfantryType", StringComparison.OrdinalIgnoreCase))
                    kinds.AddRange(["FS_FACTORY", "FS_BARRACKS", "AUTO_RALLYPOINT"]);
                if (factory.Equals("UnitType", StringComparison.OrdinalIgnoreCase))
                    kinds.AddRange(Naval(b) ? ["SHIPYARD", "FS_FACTORY", "AUTO_RALLYPOINT"] : ["FS_FACTORY", "FS_WARFACTORY", "AUTO_RALLYPOINT"]);
                if (factory.Equals("AircraftType", StringComparison.OrdinalIgnoreCase))
                    kinds.AddRange(["FS_FACTORY", "FS_AIRFIELD"]);
                if (IsRefinery(b))
                    kinds.AddRange(["FS_SUPPLY_CENTER", "CASH_GENERATOR"]);
                if (primary != null)
                    kinds.AddRange(["CAN_ATTACK", "FS_BASE_DEFENSE"]);
                if (rules.Bool(b, "Radar"))
                    kinds.Add("FS_TECHNOLOGY");
                if (rules.Bool(b, "UnitRepair"))
                    kinds.Add("REPAIR_PAD");
                // (Red Alert 2 has no silos: a refinery's Storage= doesn't limit the money.)
                objects.Append($"    KindOf: {string.Join(' ', kinds)}\n");
                float halfX = fw * SizeUnits / 2, halfY = fh * SizeUnits / 2;
                objects.Append($"    Geometry: BOX\n    GeometryMajorRadius: {F(halfX)}\n    GeometryMinorRadius: {F(halfY)}\n    GeometryHeight: {F(Math.Max(art.Num(image, "Height", 2), 1) * 10)}\n    GeometryIsSmall: No\n");
                objects.Append($"    TdFootprint: 0 0 {F(halfX)} {F(halfY)}\n    BuildCompletion: PLACED_BY_PLAYER\n    PlacementViewAngle: 0\n    TdBuiltByYard: Yes\n");
                if (rules.Num(b, "Adjacent", 1) > 1)
                    objects.Append($"    TdAdjacent: {(int)rules.Num(b, "Adjacent", 1)}\n");
                if (SpyEffect(b) is { } spyEffect)
                    objects.Append($"    Ra2SpyEffect: {spyEffect}\n");
                string prereq = Prerequisites(b);
                objects.Append(prereq == "UNBUILDABLE" ? "    Prerequisites: {}\n" : prereq);
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(rules.Get(b, "Armor", "wood"))}\n      DamageFX: StructureDamageFXNoShake\n");
                if (primary != null)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: PRIMARY {primary}\n");
                // What it builds.
                IEnumerable<string> built = isYard ? buildingTypes.Where(x => x != yard && buildable.Contains(x))
                    : factory.Equals("InfantryType", StringComparison.OrdinalIgnoreCase) ? infantryTypes.Where(buildable.Contains)
                    : factory.Equals("UnitType", StringComparison.OrdinalIgnoreCase) ? vehicleTypes.Where(v => buildable.Contains(v) && Naval(v) == Naval(b) && v != mcv)
                    : factory.Equals("AircraftType", StringComparison.OrdinalIgnoreCase) ? aircraftTypes.Where(buildable.Contains) : [];
                bool isFactory = isYard || factory.Length > 0;
                var set = built.Select(Button).ToList();
                var superweapons = SuperweaponsOf(b);
                set.AddRange(superweapons.Select(s => s.Button));
                if (isFactory && !isYard)
                    set.Add("Command_SetRallyPoint");
                set.AddRange(["Command_Ra2Repair", "Command_Sell"]);
                objects.Append($"    CommandSet: {CommandSet($"{N(b)}CommandSet", set)}\n");
                objects.Append("    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {image}\n        Kind: Building\n        Voxel: No\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: StructureBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                objects.Append($"      ModuleTag_12:\n        Module: Behavior\n        Class: TdStructureRepair\n        RepairStep: {F(Math.Max(hp / 20, 5))}\n");
                if (primary != null)
                {
                    objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (rules.Bool(b, "Turret"))
                        objects.Append("        Turret:\n          TurretTurnRate: 180\n          ControlledWeaponSlots: PRIMARY\n");
                }
                if (isFactory)
                {
                    objects.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                    if (isYard)
                        objects.Append("        PlacesStructures: Yes\n");
                    else
                        objects.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: DefaultProductionExitUpdate\n        UnitCreatePoint: 'X: 0 Y: {F(-halfY - 10)} Z:0.0'\n        NaturalRallyPoint: 'X: 0 Y: {F(-halfY - 50)} Z:0.0'\n");
                }
                if (IsRefinery(b))
                {
                    objects.Append("      ModuleTag_08:\n        Module: Behavior\n        Class: SupplyCenterCreate\n");
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: SupplyCenterDockUpdate\n        AllowsPassthrough: No\n        NumberApproachPositions: -1\n");
                    objects.Append($"        DockStart: 'X: 0 Y: {F(-halfY - 20)} Z:0.0'\n        DockAction: 'X: 0 Y: {F(-halfY)} Z:0.0'\n        DockEnd: 'X: 0 Y: {F(-halfY)} Z:0.0'\n");
                    // Its free miner (FreeUnit=CMIN / HARV / SMIN).
                    if (rules.Get(b, "FreeUnit") is { Length: > 0 } freeUnit && vehicleTypes.Contains(freeUnit, StringComparer.OrdinalIgnoreCase))
                        objects.Append($"      ModuleTag_10:\n        Module: Behavior\n        Class: TdFreeUnitOnBuilt\n        Unit: {N(freeUnit)}\n");
                }
                // Firestorm wall sections: while their generator's firestorm burns, they destroy what crosses them and pass hits on to it.
                if (rules.Bool(b, "FirestormWall"))
                {
                    objects.Append($"      ModuleTag_14:\n        Module: Behavior\n        Class: TsFirestormWall\n        DamageToGenerator: {F(rules.Num("General", "DamageToFirestormDamageCoefficient", 0.1f))}\n");
                    foreach (var (key, anim) in new[] { ("ActiveFX", "FirestormIdleAnim"), ("GroundFX", "FirestormGroundAnim"), ("AirFX", "FirestormAirAnim") })
                        if (rules.Get("AudioVisual", anim) is { Length: > 0 } fx)
                            objects.Append($"        {key}: RA2FX:{fx}\n");
                }
                if (rules.Bool(b, "UnitRepair"))
                    objects.Append("      ModuleTag_11:\n        Module: Behavior\n        Class: RepairDockUpdate\n        TimeForFullHeal: 5000\n        NumberApproachPositions: 3\n");
                // Yuri's refinery keeps its slaves (Enslaves / SlavesNumber, a lost one replaced after SlaveRegenRate).
                if (rules.Get(b, "Enslaves") is { Length: > 0 } slave)
                {
                    slaves.Add(slave);
                    // (Spawn leave through an exit, as a supply center's truck does.)
                    if (!isFactory)
                        objects.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: SupplyCenterProductionExitUpdate\n        UnitCreatePoint: 'X: 0 Y: {F(-halfY - 10)} Z:0.0'\n");
                    objects.Append($"      ModuleTag_13:\n        Module: Behavior\n        Class: SpawnBehavior\n        SpawnNumber: {(int)rules.Num(b, "SlavesNumber", 5)}\n        SpawnReplaceDelay: {F(rules.Num(b, "SlaveRegenRate", 500) / FramesPerSecond * 1000)}\n");
                    objects.Append($"        SpawnTemplateName: {N(slave)}\n        InitialBurst: {(int)rules.Num(b, "SlavesNumber", 5)}\n        SlavesHaveFreeWill: Yes\n        CanReclaimOrphans: Yes\n        ExitByBudding: Yes\n");
                }
                int powerTag = 20;
                foreach (var (sw, _, module, settings, ocl, _) in superweapons)
                {
                    string oclName = $"OCL_{sw}_{side.Prefix.TrimEnd('_')}";
                    objects.Append($"      ModuleTag_{powerTag++}:\n        Module: Behavior\n        Class: {module}\n        SpecialPowerTemplate: {sw}\n{settings}");
                    if (ocl != null)
                    {
                        objects.Append($"        OCL: {oclName}\n        CreateLocation: CREATE_AT_LOCATION\n");
                        if (exportedPowers.Add(oclName))
                            ocls.Append($"  {oclName}:\n" + (ocl.Length > 0 ? $"    Nuggets:\n{ocl}" : "    Nuggets: []\n"));
                    }
                }
            }

            // ---- Units ----
            void Mobile(string t, string kind, string locomotor, float radius)
            {
                string image = Image(t);
                int cost = (int)rules.Num(t, "Cost");
                float hp = rules.Num(t, "Strength", 100);
                float sight = Math.Max(rules.Num(t, "Sight", 5), 1) * CellUnits;
                string? primary = Weapon(rules.Get(t, "Primary")), secondary = Weapon(rules.Get(t, "Secondary"));
                string role = rules.Bool(t, "Harvester") || rules.Bool(t, "Slaved") ? "UNIT_HARVESTER" : t == mcv ? "UNIT_MCV" : kind == "Infantry" ? "INFANTRY_E1" : kind == "Aircraft" ? "AIRCRAFT_RA2" : "UNIT_RA2";
                objects.Append($"  {N(t)}:\n    DisplayName: {Name(t)}\n    Side: {side.SideName}\n    EditorSorting: {(kind == "Infantry" ? "INFANTRY" : "VEHICLE")}\n");
                objects.Append($"    ButtonImage: RA2CAMEO:{t}\n    SelectPortrait: RA2CAMEO:{t}\n    BuildCost: {cost}\n    BuildTime: {F(Math.Max(2, cost / 100f * 1.6f * Math.Max(rules.Num(t, "BuildTimeMultiplier", 1), 0.2f)))}\n    TdRole: {role}\n");
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n    IsTrainable: Yes\n    RadarPriority: UNIT\n");
                foreach (var (key, ini) in new[] { ("VoiceSelect", "VoiceSelect"), ("VoiceMove", "VoiceMove"), ("VoiceAttack", "VoiceAttack"), ("VoiceCreate", "VoiceSelect") })
                    if (Sound(rules.Get(t, ini), true) is { Length: > 0 } voice)
                        objects.Append($"    {key}: {voice}\n");
                if (Sound(rules.Get(t, game.Tag == "TS" && rules.Get(t, "DieSound").Length == 0 ? "VoiceDie" : "DieSound"), false) is { Length: > 0 } die)
                    objects.Append($"    SoundDie: {die}\n");
                var kinds = new List<string> { "PRELOAD", "SELECTABLE", "SCORE", "TD_OBJECT" };
                if (primary != null)
                    kinds.Add("CAN_ATTACK");
                kinds.Add(kind switch { "Infantry" => "INFANTRY", "Aircraft" => "AIRCRAFT", _ => "VEHICLE" });
                if (role == "UNIT_HARVESTER")
                    kinds.Add("HARVESTER");
                if (role == "UNIT_MCV")
                    kinds.Add("TD_MCV");
                if (kind == "Aircraft")
                    kinds.Add("VTOL");
                objects.Append($"    KindOf: {string.Join(' ', kinds)}\n    Locomotor: SET_NORMAL {locomotor}\n");
                objects.Append($"    Geometry: {(kind == "Infantry" ? "CYLINDER" : "BOX")}\n    GeometryMajorRadius: {F(radius)}\n    GeometryMinorRadius: {F(radius)}\n    GeometryHeight: 12\n    GeometryIsSmall: Yes\n");
                if (kind == "Infantry")
                    objects.Append("    CrushableLevel: 0\n    TransportSlotCount: 1\n");
                else if (kind is "Unit" or "Vessel")
                    objects.Append($"    CrusherLevel: {(rules.Bool(t, "Crusher") ? 2 : 1)}\n    CrushableLevel: 2\n");
                string prereq = Prerequisites(t);
                objects.Append(prereq == "UNBUILDABLE" ? "    Prerequisites: {}\n" : prereq);
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(rules.Get(t, "Armor", "none"))}\n");
                var slots = new List<string>();
                if (primary != null) slots.Add($"PRIMARY {primary}");
                if (secondary != null) slots.Add($"SECONDARY {secondary}");
                if (slots.Count > 0)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: [{string.Join(", ", slots)}]\n");
                int passengers = (int)rules.Num(t, "Passengers");
                objects.Append(passengers > 0 ? "    CommandSet: RA2TransportCommandSet\n" : "    CommandSet: GenericCommandSet\n");
                objects.Append("    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {image}\n        Kind: {kind}\n        Voxel: {(art.Bool(image, "Voxel") ? "Yes" : "No")}\n");
                if (art.Get(image, "Sequence") is { Length: > 0 } sequence)
                    objects.Append($"        Sequence: {sequence}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                if (role == "UNIT_HARVESTER")
                    objects.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: {(rules.Bool(t, "Slaved") ? 2 : 9)}\n        SupplyCenterActionDelay: 1500\n        SupplyWarehouseActionDelay: 600\n        SupplyWarehouseScanDistance: 900\n");
                else
                {
                    objects.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: {(passengers > 0 ? "TransportAIUpdate" : "AIUpdateInterface")}\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (rules.Bool(t, "Turret"))
                        objects.Append("        Turret:\n          TurretTurnRate: 180\n          ControlledWeaponSlots: PRIMARY\n");
                }
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                objects.Append(kind == "Infantry"
                    ? "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 1000\n"
                    : "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 100\n        FX: FINAL FX_GenericTankDeathExplosion\n");
                if (t == mcv && yard != null)
                    objects.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {N(yard)}\n");
                if (passengers > 0)
                    objects.Append($"      ModuleTag_08:\n        Module: Behavior\n        Class: TransportContain\n        Slots: {passengers}\n        AllowInsideKindOf: INFANTRY\n        ExitDelay: 250\n        NumberOfExitPaths: 1\n        GoAggressiveOnExit: Yes\n        DamagePercentToUnits: 100%\n");
                if (rules.Bool(t, "Cloakable"))
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: StealthUpdate\n        StealthDelay: 2000\n        StealthForbiddenConditions: ATTACKING FIRING_PRIMARY\n        InnateStealth: Yes\n        OrderIdleEnemiesToAttackMeUponReveal: No\n");
                if (rules.Bool(t, "Engineer"))
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Capture\n");
                if (rules.Bool(t, "C4"))
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Demolish\n        Consumed: No\n");
                if (rules.Bool(t, "Infiltrate") && rules.Bool(t, "Agent"))
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Spy\n");
                // The Slave Miner deploys into Yuri's refinery (DeploysInto), as an MCV does.
                if (t != mcv && rules.Get(t, "DeploysInto") is { Length: > 0 } into && names.Contains(into))
                    objects.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {N(into)}\n");
            }

            float Speed(string t) => rules.Num(t, "Speed", 4) * 256 / 100f * FramesPerSecond * LeptonUnits;
            float Rot(string t) => Math.Max(rules.Num(t, "ROT", 5), 1) * FramesPerSecond * 360 / 256f;
            foreach (string v in vehicleTypes.Where(buildable.Contains).Append(mcv ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string loco = $"{N(v)}Locomotor";
                bool naval = Naval(v);
                bool hover = rules.Get(v, "Locomotor").Contains("4A582742", StringComparison.OrdinalIgnoreCase);
                locomotors.Append($"  {loco}:\n    Surfaces: {(naval ? "WATER" : hover ? "GROUND WATER" : "GROUND")}\n    Speed: {F(Speed(v))}\n    SpeedDamaged: {F(Speed(v) * 0.75f)}\n");
                locomotors.Append($"    TurnRate: {F(Rot(v))}\n    TurnRateDamaged: {F(Rot(v))}\n    Acceleration: 1000\n    AccelerationDamaged: 1000\n    Braking: 1000\n    MinTurnSpeed: 0\n");
                locomotors.Append($"    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: {(naval || hover ? "HOVER" : "TREADS")}\n");
                Mobile(v, naval ? "Vessel" : "Unit", loco, 14);
            }
            foreach (string i in infantryTypes.Where(buildable.Contains))
            {
                string loco = $"{N(i)}Locomotor";
                float speed = Speed(i) * InfantrySpeedFactor;
                locomotors.Append($"  {loco}:\n    Surfaces: GROUND RUBBLE\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n");
                locomotors.Append("    TurnRate: 500\n    TurnRateDamaged: 500\n    Acceleration: 100\n    AccelerationDamaged: 100\n    Braking: 100\n    MinTurnSpeed: 0\n");
                locomotors.Append("    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: TWO_LEGS\n    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
                Mobile(i, "Infantry", loco, 5);
            }
            foreach (string s in slaves.Where(s => rules.Has(s) && !infantryTypes.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                string loco = $"{N(s)}Locomotor";
                float speed = Speed(s) * InfantrySpeedFactor;
                locomotors.Append($"  {loco}:\n    Surfaces: GROUND RUBBLE\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n");
                locomotors.Append("    TurnRate: 500\n    TurnRateDamaged: 500\n    Acceleration: 100\n    AccelerationDamaged: 100\n    Braking: 100\n    MinTurnSpeed: 0\n");
                locomotors.Append("    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: TWO_LEGS\n    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
                Mobile(s, "Infantry", loco, 5);
            }
            foreach (string a in aircraftTypes.Where(buildable.Contains))
            {
                string loco = $"{N(a)}Locomotor";
                float speed = Math.Max(Speed(a), 50);
                locomotors.Append($"  {loco}:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n");
                locomotors.Append($"    TurnRate: {F(Math.Max(Rot(a), 120))}\n    TurnRateDamaged: {F(Math.Max(Rot(a), 120))}\n    Acceleration: 60\n    AccelerationDamaged: 60\n    Lift: 120\n    LiftDamaged: 80\n    Braking: 240\n    MinTurnSpeed: 0\n");
                locomotors.Append("    PreferredHeight: 60\n    AllowAirborneMotiveForce: Yes\n    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n    Appearance: HOVER\n    Apply2DFrictionWhenAirborne: Yes\n    AirborneTargetingHeight: 30\n    LocomotorWorksWhenDead: Yes\n");
                Mobile(a, "Aircraft", loco, 16);
            }

            // ---- Tiberian Sun's Hunter-Seekers: launched by the superweapon, never built ----
            foreach (var (hunterName, h) in hunters.Distinct())
            {
                string image = Image(h);
                float hp = rules.Num(h, "Strength", 500);
                objects.Append($"  {hunterName}:\n    DisplayName: {Name(h)}\n    Side: {side.SideName}\n    EditorSorting: VEHICLE\n    RadarPriority: UNIT\n");
                objects.Append($"    VisionRange: {F(rules.Num(h, "Sight", 7) * CellUnits)}\n    ShroudClearingRange: {F(rules.Num(h, "Sight", 7) * CellUnits)}\n");
                objects.Append("    KindOf: PRELOAD VEHICLE TD_OBJECT NO_COLLIDE\n    Geometry: BOX\n    GeometryMajorRadius: 10\n    GeometryMinorRadius: 10\n    GeometryHeight: 10\n    GeometryIsSmall: Yes\n");
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(rules.Get(h, "Armor", "light"))}\n");
                objects.Append("    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {image}\n        Kind: Unit\n        Voxel: {(art.Bool(image, "Voxel") ? "Yes" : "No")}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                objects.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: TsHunterSeekerUpdate\n        Speed: {F(Speed(h))}\n        FlightHeight: {F(rules.Num(h, "FlightLevel", 400) * LeptonUnits)}\n");
                objects.Append($"        DetonateProximity: {F(rules.Num("General", "HunterSeekerDetonateProximity", 150) * LeptonUnits)}\n");
                if (Weapon(rules.Get(h, "Primary")) is { } bomb)
                    objects.Append($"        Weapon: {bomb}\n");
            }

            // ---- The player templates: one a country ([Sides]), sharing the side's arsenal plus its own units ----
            foreach (string country in side.Countries)
            {
                string shown = CountryNames.GetValueOrDefault(country, country);
                factions.Append($"  {CountryFaction(country)}:\n    Side: {side.SideName}\n    BaseSide: {side.BaseSide}\n    PlayableSide: Yes\n    StartMoney: 0\n    PreferredColor: {side.Color}\n");
                factions.Append($"    DisplayName: {shown} (Red Alert 2)\n");
                if (mcv != null)
                    factions.Append($"    StartingUnit0: {N(mcv)}\n");
                factions.Append($"    TdFaction: Yes\n    ScoreScreenImage: {side.Score}\n    LoadScreenImage: {side.Logo}\n    BeaconName: MultiplayerBeacon\n    OldFaction: Yes\n");
            }
        }

        // ---- Map scenery: ore and gems, trees, and the maps' neutral buildings (civilian and tech) ----
        var scenery = new StringBuilder("Objects:\n");
        // Ore (TIB) and gems (GEM): a supply pile a cell, as rich as its overlay frame (0-11), drawn flat on the ground.
        foreach (var (kind, image, perFrame) in new[] { ("Ore", "TIB01", 1), ("Gem", "GEM01", 2) })
            for (int n = 1; n <= 12; n++)
            {
                scenery.Append($"  RA2_{kind}{n:00}:\n    DisplayName: {kind}\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE SUPPLY_SOURCE STRUCTURE TD_OBJECT\n");
                scenery.Append("    Geometry: CYLINDER\n    GeometryMajorRadius: 10\n    GeometryMinorRadius: 10\n    GeometryHeight: 3\n    GeometryIsSmall: Yes\n");
                scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: InvulnerableAllArmor\n    Modules:\n");
                scenery.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {image}\n        Kind: Ore\n        Voxel: No\n");
                scenery.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 1.0\n        InitialHealth: 1.0\n");
                scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyWarehouseCreate\n");
                scenery.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: SupplyWarehouseDockUpdate\n        StartingBoxes: {n * perFrame}\n        DeleteWhenEmpty: Yes\n        AllowsPassthrough: Yes\n        NumberApproachPositions: -1\n");
                if (kind == "Ore")
                    scenery.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: TdTiberium\n        Mode: Tiberium\n        Seed: RA2_Ore01\n        Poison: {(game.PoisonResources ? "Yes" : "No")}\n");
            }
        // Trees and other terrain objects ([TerrainTypes]).
        foreach (string t in rules.TypeList("TerrainTypes").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            float hp = rules.Num(t, "Strength", 800);
            string terrainImage = rules.Get(t, "Image") is { Length: > 0 } ti ? ti : t;
            scenery.Append($"  RA2_Terrain_{t.ToUpperInvariant()}:\n    DisplayName: {t}\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE TD_OBJECT\n");
            scenery.Append("    Geometry: CYLINDER\n    GeometryMajorRadius: 12\n    GeometryMinorRadius: 12\n    GeometryHeight: 30\n    GeometryIsSmall: No\n");
            scenery.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(rules.Get(t, "Armor", "wood"))}\n    Modules:\n");
            scenery.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {terrainImage}\n        Kind: Terrain\n        Voxel: No\n");
            scenery.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
            scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
        }
        // Neutral buildings (no playable owner): civilian houses one can garrison, tech buildings one can capture.
        var playable = sides.SelectMany(s => s.Countries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string b in rules.TypeList("BuildingTypes").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!rules.Has(b) || rules.List(b, "Owner").Any(playable.Contains) && rules.Num(b, "TechLevel", -1) >= 0)
                continue;
            string image = rules.Get(b, "Image") is { Length: > 0 } bi ? bi : b;
            var foundation = art.Get(image, "Foundation", "1x1").ToLowerInvariant().Split('x');
            int fw = int.TryParse(foundation[0], out int fa) ? fa : 1, fh = foundation.Length > 1 && int.TryParse(foundation[1], out int fc) ? fc : 1;
            float hp = rules.Num(b, "Strength", 500), halfX = fw * SizeUnits / 2, halfY = fh * SizeUnits / 2;
            string shown = rules.Get(b, "UIName") is { Length: > 0 } ui && strings.TryGetValue(ui, out var s) && s.Length > 0 ? Plain(s) : Plain(rules.Get(b, "Name", b));
            var kinds = new List<string> { "PRELOAD", "STRUCTURE", "SELECTABLE", "IMMOBILE", "TD_OBJECT" };
            bool capturable = rules.Bool(b, "Capturable") && rules.Bool(b, "NeedsEngineer");
            if (capturable)
                kinds.Add("CAPTURABLE");
            if (rules.Bool(b, "CanBeOccupied"))
                kinds.Add("GARRISONABLE_UNTIL_DESTROYED");
            scenery.Append($"  RA2CIV_{b.ToUpperInvariant()}:\n    DisplayName: {shown}\n    EditorSorting: CIVILIAN_BUILDING\n    KindOf: {string.Join(' ', kinds)}\n");
            scenery.Append($"    Geometry: BOX\n    GeometryMajorRadius: {F(halfX)}\n    GeometryMinorRadius: {F(halfY)}\n    GeometryHeight: {F(Math.Max(art.Num(image, "Height", 2), 1) * 10)}\n    GeometryIsSmall: No\n");
            scenery.Append($"    TdFootprint: 0 0 {F(halfX)} {F(halfY)}\n    VisionRange: {F(CellUnits * 4)}\n    ShroudClearingRange: {F(CellUnits * 4)}\n");
            scenery.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(rules.Get(b, "Armor", "wood"))}\n      DamageFX: StructureDamageFXNoShake\n");
            scenery.Append("    Modules:\n");
            scenery.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: RA2SpriteDraw\n        Image: {image}\n        Kind: Building\n        Voxel: No\n");
            scenery.Append($"      ModuleTag_02:\n        Module: Body\n        Class: StructureBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
            scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
            scenery.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n");
            if (rules.Bool(b, "CanBeOccupied"))
                scenery.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: GarrisonContain\n        ContainMax: {(int)rules.Num(b, "MaxNumberOccupants", 5)}\n        EnterSound: GarrisonEnter\n        ExitSound: GarrisonExit\n");
            // The oil derrick: ProduceCashStartup when taken, then ProduceCashAmount every ProduceCashDelay frames.
            if (rules.Num(b, "ProduceCashAmount") > 0)
                scenery.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: AutoDepositUpdate\n        DepositTiming: {F(rules.Num(b, "ProduceCashDelay", 150) / FramesPerSecond * 1000)}\n        DepositAmount: {(int)rules.Num(b, "ProduceCashAmount")}\n        InitialCaptureBonus: {(int)rules.Num(b, "ProduceCashStartup")}\n");
        }
        File.WriteAllText(Path.Combine(output, "scenery.yaml"), "# GENERATED by 'OpenWestwood.Tools export-ra2-factions'.\n" + Named(game, scenery.ToString()));

        const string header = "# GENERATED by 'OpenWestwood.Tools export-ra2-factions' from Red Alert 2 / Yuri's Revenge's RULESMD.INI and ARTMD.INI (mods/redalert2).\n" +
                              "# Red Alert 2's Allies, Soviets and Yuri as Zero Hour engine factions. Edit freely, or re-export.\n";
        File.WriteAllText(Path.Combine(output, "factions.yaml"), Named(game, header + factions));
        File.WriteAllText(Path.Combine(output, "objects.yaml"), Named(game, header + objects));
        File.WriteAllText(Path.Combine(output, "projectiles.yaml"), Named(game, header + projectiles));
        File.WriteAllText(Path.Combine(output, "weapons.yaml"), Named(game, header + weapons));
        File.WriteAllText(Path.Combine(output, "locomotors.yaml"), Named(game, header + locomotors));
        File.WriteAllText(Path.Combine(output, "armor.yaml"), Named(game, header + armor));
        File.WriteAllText(Path.Combine(output, "command-sets.yaml"), Named(game, header + commandSets));
        File.WriteAllText(Path.Combine(output, "command-buttons.yaml"), Named(game, header + buttons));
        File.WriteAllText(Path.Combine(output, "special-powers.yaml"), Named(game, header + powers));
        File.WriteAllText(Path.Combine(output, "sound-effects.yaml"), Named(game, header + soundEvents));

        // The announcer (EVA.INI / EVAMD.INI): each line has an Allied, a Soviet (Russian) and a Yuri reading.
        var evaIni = new Ra2Ini();
        foreach (string f in game.Eva)
            if (ra2.Files.Exists(f))
                evaIni.Read(Encoding.Latin1.GetString(ra2.Files.ReadAllBytes(f)));
        var evaEvents = new (string Event, string Eva)[]
        {
            ("LowPower", "EVA_LowPower"), ("InsufficientFunds", "EVA_InsufficientFunds"), ("TdInsufficientFunds", "EVA_InsufficientFunds"),
            ("BuildingLost", "EVA_CriticalStrucureLost"), ("BaseUnderAttack", "EVA_OurBaseIsUnderAttack"), ("UnitLost", "EVA_UnitLost"),
            ("UpgradeComplete", "EVA_UpgradeComplete"), ("UnitReady", "EVA_UnitReady"), ("BuildingComplete", "EVA_ConstructionComplete"),
            ("TdConstructionComplete", "EVA_ConstructionComplete"), ("TdBuildingCaptured", "EVA_BuildingCaptured"), ("TdCannotDeployHere", "EVA_CannotDeployHere"),
            ("Ra2ChronoSelectDestination", "EVA_SelectTarget"), ("Ra2Chronoshift", "EVA_ChronosphereActivated"), ("Ra2IronCurtain", "EVA_IronCurtainActivated"),
            ("Ra2LightningStorm", "EVA_LightningStormCreated"), ("Ra2GeneticMutator", "EVA_GeneticMutatorActivated"), ("Ra2BuildingInfiltrated", "EVA_BuildingInfiltrated"),
        };
        var evaSides = new[] { ("RA2Allies", "Allied"), ("RA2Soviet", "Russian"), ("RA2Yuri", "Yuri") };
        var dialog = new StringBuilder("DialogEvents:\n");
        var sideEva = new StringBuilder("SideEvaSounds:\n");
        var evaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (evt, eva) in evaEvents)
        {
            if (!evaIni.Has(eva))
                continue;
            sideEva.Append($"  {evt}:\n");
            foreach (var (side, key) in evaSides)
            {
                string file = evaIni.Get(eva, key);
                if (file.Length == 0 || file.Equals("Dummy", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (evaFiles.Add(file))
                    dialog.Append($"  Ra2Eva_{file}:\n    Filename: /RA2SFX/{file}.wav\n");
                sideEva.Append($"    {side}: Ra2Eva_{file}\n");
            }
        }
        // (No EVA lines, as for Tiberian Sun: no file. Empty sections used to wipe every other game's speech.)
        if (evaFiles.Count > 0)
            File.WriteAllText(Path.Combine(output, "eva.yaml"), Named(game, header + dialog + sideEva));
        else
            File.Delete(Path.Combine(output, "eva.yaml"));
        File.WriteAllText(Path.Combine(output, "object-creation-lists.yaml"), Named(game, header + ocls));
        Console.WriteLine($"Wrote {game.Title} factions to {output} ({weaponNames.Count} weapons)");
        return 0;
    }
}
