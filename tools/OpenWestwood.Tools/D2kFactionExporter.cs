using System.Globalization;
using System.Text;
using OpenWestwood.Core.Rules;
using OpenWestwood.Formats.OpenRA;

namespace OpenWestwood.Tools;

/// <summary>
/// "export-d2k-factions": Dune 2000's Atreides, Harkonnen and Ordos as Zero Hour engine factions, from the
/// Dune 2000 game data in content/dune2000 (the rules of the OpenRA Dune 2000 install the user supplied,
/// with the original DATA.R16 art). Written to mods/zh/rules/d2k. They play like the TD factions: an MCV
/// unfolds into a Construction Yard, which builds structures that are then placed next to the base.
/// Conversions (a Dune 2000 cell is 1024 rule units; the game runs 25 ticks a second):
/// <list type="bullet">
/// <item>distances: cells x 36 world units (as TD); sizes: TdScale.Cell (36) a cell, the art 32 pixels a cell;</item>
/// <item>speeds: units a tick / 1024 x 36 x 25; reload and build times: ticks x 40 ms;</item>
/// <item>health and damage: the rules' values / 100;</item>
/// <item>damage against armour: each warhead's Versus table, its damage type chosen by what it is good against.</item>
/// </list>
/// Simplifications: building upgrades (upgrade.*) aren't needed for anything; concrete isn't built; tech levels are all on.
/// </summary>
static class D2kFactionExporter
{
    const float CellUnits = 36f, SizeUnits = 36f, TicksPerSecond = 25f;

    sealed record Side(string Name, string Faction, string Prefix, string BaseSide, string Color, string DisplayName, string ScoreImage, string LoadImage);

    static readonly Side[] Sides =
    [
        new("atreides", "FactionD2KAtreides", "D2KATR_", "USA", "R:40 G:90 B:255", "Atreides (Dune 2000)", "America_ScoreScreen", "SAFactionLogoPage_US"),
        new("harkonnen", "FactionD2KHarkonnen", "D2KHAR_", "China", "R:220 G:30 B:30", "Harkonnen (Dune 2000)", "China_ScoreScreen", "SNFactionLogoPage_China"),
        new("ordos", "FactionD2KOrdos", "D2KORD_", "GLA", "R:30 G:200 B:60", "Ordos (Dune 2000)", "GLA_ScoreScreen", "SUFactionLogoPage_GLA"),
    ];

    // The Dune 2000 armour types and the Zero Hour armour each one starts from.
    static readonly (string Type, string Base)[] Armors =
    [
        ("none", "HumanArmor"), ("wood", "StructureArmor"), ("wall", "StructureArmor"), ("building", "StructureArmor"),
        ("cy", "StructureArmor"), ("light", "TruckArmor"), ("harvester", "TruckArmor"), ("heavy", "TankArmor"),
        ("invulnerable", "InvulnerableAllArmor"),
    ];

    // What the computer player (TdFactionAI) knows each structure as (TD's types).
    static readonly Dictionary<string, string> StructureRoles = new()
    {
        ["construction_yard"] = "STRUCT_CONST", ["wind_trap"] = "STRUCT_POWER", ["refinery"] = "STRUCT_REFINERY",
        ["barracks"] = "STRUCT_BARRACKS", ["light_factory"] = "STRUCT_LIGHT_FACTORY", ["heavy_factory"] = "STRUCT_WEAP",
        ["outpost"] = "STRUCT_RADAR", ["medium_gun_turret"] = "STRUCT_TURRET", ["large_gun_turret"] = "STRUCT_OBELISK",
        ["silo"] = "STRUCT_STORAGE", ["repair_pad"] = "STRUCT_REPAIR", ["high_tech_factory"] = "STRUCT_HELIPAD",
        ["research_centre"] = "STRUCT_EYE", ["palace"] = "STRUCT_TEMPLE", ["starport"] = "STRUCT_AIRSTRIP", ["wall"] = "STRUCT_WALL",
    };

    // Which factory makes each production queue.
    static readonly Dictionary<string, string> QueueFactory = new()
    {
        ["Building"] = "construction_yard", ["Infantry"] = "barracks", ["Vehicle"] = "light_factory",
        ["Armor"] = "heavy_factory", ["Aircraft"] = "high_tech_factory", ["Starport"] = "starport",
    };

    // Left out: concrete, the transports and the worm (not playable units), the fire turret and the flamer
    // (removed at the user's request; the flamer's "icon" is a plain soldier frame).
    static readonly string[] Skipped = ["frigate", "sandworm_fremen", "fire_turret", "flamer_guy"];

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static int Run(string root)
    {
        string mod = Path.Combine(root, "content", "dune2000", "mod");
        if (!Directory.Exists(mod))
        {
            Console.Error.WriteLine($"No Dune 2000 data in {mod}");
            return 1;
        }
        string output = Path.Combine(root, "mods", "zh", "rules", "d2k");
        Directory.CreateDirectory(output);

        var ruleFiles = new[] { "misc", "player", "world", "defaults", "infantry", "vehicles", "aircraft", "structures", "starport", "husks" }
            .Select(n => Path.Combine(mod, "rules", n + ".yaml")).Where(File.Exists);
        var rules = MiniYaml.Resolve(MiniYaml.Combine(ruleFiles.Select(f => MiniYaml.Parse(File.ReadAllText(f)))));
        var weaponRules = MiniYaml.Resolve(MiniYaml.Combine(Directory.GetFiles(Path.Combine(mod, "weapons"), "*.yaml").Select(f => MiniYaml.Parse(File.ReadAllText(f)))));
        var names = Fluent(Path.Combine(mod, "fluent", "rules.ftl"));
        var zhArmor = RuleYaml.Parse(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "armor.yaml")), "armor.yaml")["Armor"]!;

        var objects = new StringBuilder("Objects:\n");
        var weapons = new StringBuilder("Weapons:\n");
        var projectiles = new StringBuilder("Objects:\n");
        var locomotors = new StringBuilder("Locomotors:\n");
        var commandSets = new StringBuilder("CommandSets:\n");
        var buttons = new StringBuilder("CommandButtons:\n");
        buttons.Append("  Command_D2kRepair:\n    Command: TD_REPAIR\n    TextLabel: Repair\n    ButtonImage: SSRepair\n    ButtonBorderType: ACTION\n    DescriptLabel: Repair this building (costs money as it mends)\n");
        var factions = new StringBuilder("PlayerTemplates:\n");
        var powers = new StringBuilder("SpecialPowers:\n");
        var ocls = new StringBuilder("ObjectCreationLists:\n");
        // The building upgrades (upgrade.conyard ... upgrade.hightech): player upgrades researched at that building.
        var upgrades = new StringBuilder("Upgrades:\n");
        var strikePlanes = new StringBuilder();
        var usedSounds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var soundGroups = new SortedDictionary<string, string[]>();
        // The sounds there are: SOUND.RS's, and the mod's own bits (some named in the rules are in neither).
        var soundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string soundRs = Path.Combine(root, "content", "dune2000", "data", "SOUND.RS");
        if (File.Exists(soundRs))
            using (var rs = new OpenWestwood.Core.FileSystem.RsArchive(soundRs))
                soundFiles.UnionWith(rs.FilePaths);
        string bits = Path.Combine(mod, "bits");
        if (Directory.Exists(bits))
            soundFiles.UnionWith(Directory.EnumerateFiles(bits, "*.wav").Select(Path.GetFileName)!);
        bool SoundExists(string file) => soundFiles.Contains(file);
        // The high tech factory's AirstrikePower, if this house gets it (it needs the upgrade, which only Atreides can research).
        MiniYamlNode? Airstrike(MiniYamlNode htf) => htf.Child("AirstrikePower");
        var upgradeAt = new Dictionary<string, string>
        {
            ["upgrade.conyard"] = "construction_yard", ["upgrade.barracks"] = "barracks", ["upgrade.light"] = "light_factory",
            ["upgrade.heavy"] = "heavy_factory", ["upgrade.hightech"] = "high_tech_factory",
        };
        string UpgradeName(string key) => "Upgrade_D2K_" + key["upgrade.".Length..];
        foreach (var (key, _) in upgradeAt)
            if (rules.TryGetValue(key, out var up))
                upgrades.Append($"  {UpgradeName(key)}:\n    DisplayName: {(up.Child("Tooltip")?.Get("Name") is { Length: > 0 } id && names.TryGetValue(id, out var un) ? un : key)}\n    Type: PLAYER\n" +
                    $"    BuildTime: {F(Num(up.Child("Buildable")?.Get("BuildDuration"), 625) / TicksPerSecond)}\n    BuildCost: {(int)Num(up.Child("Valued")?.Get("Cost"), 1000)}\n    ResearchSound: NoSound\n");

        // ---- Who provides which prerequisite ----
        var providers = new List<(string Token, string Actor, string[] Factions)>();
        foreach (var (key, actor) in rules)
        {
            if (key.StartsWith('^'))
                continue;
            foreach (var p in actor.All("ProvidesPrerequisite"))
            {
                string token = p.Get("Prerequisite");
                if (token.Length == 0)
                    token = key;
                var f = p.Get("Factions").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                providers.Add((token, key, f));
            }
        }

        // ---- Weapons (shared): damage type from the warhead's Versus table ----
        var used = new Dictionary<string, string>(); // D2K weapon -> ZH weapon name
        var versusByType = new Dictionary<string, List<Dictionary<string, int>>>();
        string ExportWeapon(string weapon)
        {
            if (used.TryGetValue(weapon, out var existing))
                return existing;
            string name = "D2K_Weapon_" + weapon.Replace('.', '_');
            used[weapon] = name;
            // Weapon names are matched ignoring case, as the rules do ("deathhand" is "DeathHand").
            string? key = weaponRules.Keys.FirstOrDefault(k => k.Equals(weapon, StringComparison.OrdinalIgnoreCase));
            if (key == null || !weaponRules.TryGetValue(key, out var w))
                return name;
            var damage = w.All("Warhead").FirstOrDefault(h => h.Value == "SpreadDamage" || h.Value == "TargetDamage");
            float dmg = damage != null ? Num(damage.Get("Damage")) / 100f : 0;
            float spread = damage != null ? Math.Max(MiniYaml.Cells(damage.Get("Spread")) * CellUnits, 3) : 3;
            // A cluster (the Death Hand: 36 bomblets over 6x6 cells) becomes one blast over the cluster's area:
            // a point there is caught by about six bomblets' blasts, so it takes six bomblets' damage.
            if (w.All("Warhead").FirstOrDefault(h => h.Value == "FireCluster") is { } cluster
                && weaponRules.Keys.FirstOrDefault(k => k.Equals(cluster.Get("Weapon"), StringComparison.OrdinalIgnoreCase)) is { } bombletKey
                && weaponRules[bombletKey].All("Warhead").FirstOrDefault(h => h.Value == "SpreadDamage") is { } bomblet)
            {
                damage = bomblet;
                dmg = Num(bomblet.Get("Damage")) / 100f * 6;
                var dims = cluster.Get("Dimensions").Split(',', StringSplitOptions.TrimEntries);
                spread = (dims.Length > 0 && int.TryParse(dims[0], out int cells) ? cells / 2f : 3) * CellUnits;
            }
            var versus = new Dictionary<string, int>();
            if (damage?.Child("Versus") is { } vs)
                foreach (var v in vs.Children)
                    versus[v.Key] = (int)Num(v.Value);
            string dt = DamageType(weapon, versus);
            if (!versusByType.TryGetValue(dt, out var list))
                versusByType[dt] = list = new();
            list.Add(versus);
            float range = MiniYaml.Cells(w.Get("Range")) * CellUnits;
            float reload = Math.Max(Num(w.Get("ReloadDelay"), 50), 1) * 1000 / TicksPerSecond;
            int burst = (int)Math.Max(Num(w.Get("Burst"), 1), 1);
            weapons.Append($"  {name}:\n    PrimaryDamage: {F(dmg)}\n    PrimaryDamageRadius: {F(spread)}\n");
            weapons.Append($"    AttackRange: {F(Math.Max(range, 15))}\n    DamageType: {dt}\n");
            weapons.Append($"    DeathType: {(dt == "FLAME" ? "BURNED" : dt == "EXPLOSION" ? "EXPLODED" : "NORMAL")}\n");
            if (burst > 1)
                weapons.Append($"    ClipSize: {burst}\n    ClipReloadTime: {(int)reload}\n    DelayBetweenShots: {(int)(Num(w.Get("BurstDelays"), 5) * 1000 / TicksPerSecond)}\n");
            else
                weapons.Append($"    DelayBetweenShots: {(int)reload}\n    ClipSize: 0\n    ClipReloadTime: 0\n");
            weapons.Append("    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n");
            // Its sound (Report: a .WAV of SOUND.RS; several are alternatives).
            var reports = w.Get("Report").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(SoundExists).ToArray();
            if (reports.Length > 0)
            {
                string sound = Path.GetFileNameWithoutExtension(reports[0]).ToUpperInvariant();
                weapons.Append($"    FireSound: D2kSnd_{sound}\n");
                foreach (string r in reports)
                    usedSounds.Add(r.ToUpperInvariant());
                soundGroups[sound] = reports.Select(r => r.ToUpperInvariant()).ToArray();
            }
            // The Deviator's ChangeOwner warhead: vehicles hit serve the firer for Duration ticks.
            if (w.All("Warhead").FirstOrDefault(h => h.Value == "ChangeOwner") is { } turn)
                weapons.Append($"    TdChangeOwnerSeconds: {F(Num(turn.Get("Duration"), 405) / TicksPerSecond)}\n    TdChangeOwnerRadius: {F(Math.Max(Num(turn.Get("Range"), 512) / 1024f * CellUnits, 18))}\n");
            string targets = w.Get("ValidTargets");
            bool air = targets.Contains("Air"), ground = targets.Length == 0 || targets.Contains("Ground");
            weapons.Append($"    AntiGround: {(ground ? "Yes" : "No")}\n    AntiAirborneVehicle: {(air ? "Yes" : "No")}\n    AntiAirborneInfantry: {(air ? "Yes" : "No")}\n");
            // The explosion: the CreateEffect warhead's first animation, played by the client from the D2K art.
            string explosion = w.All("Warhead").Where(h => h.Value == "CreateEffect").Select(h =>
                (h.Get("Image") is { Length: > 0 } image ? image : "explosion") + "/" + h.Get("Explosions").Split(',')[0].Trim()).FirstOrDefault(e => !e.EndsWith('/')) ?? "";
            // LeaveSmudge: a crater where it lands (the client picks sand or rock).
            if (w.All("Warhead").Any(h => h.Value == "LeaveSmudge"))
                explosion += "|crater";
            var projectile = w.Child("Projectile");
            bool visible = projectile != null && projectile.Value is "Bullet" or "Missile" && projectile.Get("Image") is { Length: > 0 } && !projectile.Get("Image").Equals("null", StringComparison.OrdinalIgnoreCase);
            if (visible)
            {
                string image = projectile!.Get("Image");
                string proj = $"D2K_Projectile_{weapon.Replace('.', '_')}";
                float speed = Math.Max(Num(projectile.Get("Speed"), 400) / 1024f * CellUnits * TicksPerSecond, 60);
                weapons.Append($"    ProjectileObject: {proj}\n    WeaponSpeed: {F(speed)}\n");
                if (explosion.Length > 0)
                    weapons.Append($"    ProjectileDetonationFX: D2KFX:{explosion}\n");
                bool homing = projectile.Value == "Missile";
                projectiles.Append($"  {proj}:\n    DisplayName: D2K projectile\n    EditorSorting: SYSTEM\n    VisionRange: 0.0\n");
                // The weapon's own contrail (ContrailLength, in ticks), as OpenRA draws it.
                if (Num(projectile.Get("ContrailLength")) is > 0 and var contrail)
                    projectiles.Append($"    TdContrailLength: {(int)contrail}\n");
                projectiles.Append(homing ? "    KindOf: PROJECTILE SMALL_MISSILE\n" : "    KindOf: PROJECTILE\n");
                if (homing)
                    projectiles.Append($"    Locomotor: SET_NORMAL {proj}Locomotor\n");
                projectiles.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    ArmorSet:\n      Armor: ProjectileArmor\n    Modules:\n");
                projectiles.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: {image}\n        TdKind: Bullet\n        TdType: {projectile.Value}\n");
                projectiles.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
                projectiles.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                if (homing)
                {
                    projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: MissileAIUpdate\n        TryToFollowTarget: Yes\n        FuelLifetime: 10000\n        InitialVelocity: {F(speed)}\n        IgnitionDelay: 0\n        DistanceToTravelBeforeTurning: 5\n        DistanceToTargetForLock: 60\n");
                    locomotors.Append($"  {proj}Locomotor:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    MinSpeed: 30\n    Acceleration: 400\n    Braking: 0\n    TurnRate: 180\n    MaxThrustAngle: 30\n    AllowAirborneMotiveForce: Yes\n    Appearance: THRUST\n    CloseEnoughDist: 1\n");
                }
                else
                {
                    float arc = projectile.Get("LaunchAngle") is { Length: > 0 } ? 40 : 5;
                    projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n        FirstHeight: {F(arc)}\n        SecondHeight: {F(arc)}\n        FirstPercentIndent: 30%\n        SecondPercentIndent: 70%\n        FlightPathAdjustDistPerSecond: 100\n");
                }
                projectiles.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
            }
            else if (explosion.Length > 0)
                weapons.Append($"    FireFX: D2KFX:|{explosion}\n"); // an instant hit: the explosion where it lands
            return name;
        }

        foreach (var side in Sides)
        {
            // ---- What this house can build ----
            bool Provided(string token, out List<string> actors)
            {
                actors = providers.Where(p => p.Token == token && (p.Factions.Length == 0 || p.Factions.Contains(side.Name))).Select(p => p.Actor).Distinct().ToList();
                return actors.Count > 0;
            }
            List<List<string>>? Requirements(MiniYamlNode actor)
            {
                var needs = new List<List<string>>();
                foreach (string raw in actor.Child("Buildable")?.Get("Prerequisites").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [])
                {
                    string t = raw.TrimStart('~');
                    bool not = t.StartsWith('!');
                    t = t.TrimStart('!');
                    if (t.StartsWith("techlevel."))
                        continue;
                    // An upgrade: needed researched, and this house must be able to research it.
                    if (t.StartsWith("upgrade."))
                    {
                        if (not)
                            continue;
                        if (!rules.TryGetValue(t, out var up) || Requirements(up) == null)
                            return null;
                        needs.Add(["@" + t]);
                        continue;
                    }
                    bool has = t is not ("disabled" or "global-auto-concrete") && Provided(t, out _);
                    if (not)
                    {
                        if (has && t is not "global-auto-concrete")
                            return null;
                        continue;
                    }
                    if (!has)
                        return null;
                    Provided(t, out var actors);
                    needs.Add(actors);
                }
                return needs;
            }

            var buildable = new Dictionary<string, (MiniYamlNode Node, string Queue, List<List<string>> Needs)>();
            foreach (var (key, actor) in rules)
            {
                if (key.StartsWith('^') || Skipped.Contains(key.Split('.')[0]) || actor.Child("Buildable") is not { } b)
                    continue;
                string queue = b.Get("Queue").Split(',')[0].Trim();
                if (!QueueFactory.ContainsKey(queue))
                    continue;
                if (Requirements(actor) is { } needs)
                    buildable[key] = (actor, queue, needs);
            }
            // The yard comes from the MCV, and the MCV from the heavy factory.
            buildable["construction_yard"] = (rules["construction_yard"], "None", new());
            // The palace's summoned units (ProduceActorPower): Atreides' Fremen, Ordos' Saboteur.
            if (side.Name == "atreides" && rules.TryGetValue("fremen", out var fremen))
                buildable["fremen"] = (fremen, "None", new());
            if (side.Name == "ordos" && rules.TryGetValue("saboteur", out var saboteur))
                buildable["saboteur"] = (saboteur, "None", new());
            var exported = buildable.Keys.ToHashSet();
            string N(string actor) => side.Prefix + actor.ToUpperInvariant().Replace('.', '_');

            string Prerequisites(List<List<string>> needs)
            {
                var lines = needs.Where(alts => !alts[0].StartsWith('@')).Select(alts => string.Join(' ', alts.Where(exported.Contains).Select(N))).Where(l => l.Length > 0).ToList();
                var ups = needs.Where(alts => alts[0].StartsWith('@')).Select(alts => UpgradeName(alts[0][1..])).ToList();
                if (lines.Count == 0 && ups.Count == 0)
                    return "    Prerequisites: {}\n";
                var sb = new StringBuilder("    Prerequisites:\n");
                if (lines.Count > 0)
                    sb.Append("      Object:\n");
                foreach (var l in lines)
                    sb.Append($"      - {l}\n");
                if (ups.Count > 0)
                    sb.Append("      Upgrade:\n");
                foreach (var u in ups)
                    sb.Append($"      - {u}\n");
                return sb.ToString();
            }

            string Image(string key, MiniYamlNode actor)
            {
                var sprites = actor.Child("RenderSprites");
                if (sprites?.Child("FactionImages")?.Get(side.Name) is { Length: > 0 } fi)
                    return fi;
                return sprites?.Get("Image") is { Length: > 0 } image ? image : key;
            }

            string Name(MiniYamlNode actor, string key) =>
                actor.Child("Tooltip")?.Get("Name") is { Length: > 0 } id && names.TryGetValue(id, out var n) ? n : key;

            // ---- Factory command sets ----
            foreach (var (queue, factory) in QueueFactory)
            {
                if (!exported.Contains(factory))
                    continue;
                var items = buildable.Where(kv => kv.Value.Queue == queue)
                    .OrderBy(kv => Num(kv.Value.Node.Child("Buildable")!.Get("BuildPaletteOrder"), 999)).Select(kv => kv.Key).ToList();
                if (queue == "Armor" && exported.Contains("mcv") && !items.Contains("mcv"))
                    items.Add("mcv");
                commandSets.Append($"  {N(factory)}CommandSet:\n");
                int slot = 1;
                foreach (string item in items)
                {
                    string button = $"Command_D2K_{N(item)}";
                    string image = Image(item, buildable[item].Node);
                    buttons.Append($"  {button}:\n    Command: UNIT_BUILD\n    Object: {N(item)}\n    TextLabel: {Name(buildable[item].Node, item)}\n    ButtonImage: D2KCAMEO:{image}\n    ButtonBorderType: BUILD\n    DescriptLabel: {Name(buildable[item].Node, item)}\n");
                    commandSets.Append($"    {slot++}: {button}\n");
                }
                // This building's upgrade, if this house can research it.
                foreach (var (upKey, at) in upgradeAt)
                    if (at == factory && rules.TryGetValue(upKey, out var up) && Requirements(up) != null)
                    {
                        string button = $"Command_D2K_{side.Prefix}{UpgradeName(upKey)}";
                        string image = Image(upKey, up);
                        buttons.Append($"  {button}:\n    Command: PLAYER_UPGRADE\n    Upgrade: {UpgradeName(upKey)}\n    TextLabel: {Name(up, upKey)}\n    ButtonImage: D2KCAMEO:{image}\n    ButtonBorderType: UPGRADE\n    DescriptLabel: {Name(up, upKey)}\n");
                        commandSets.Append($"    {slot++}: {button}\n");
                    }
                if (factory == "high_tech_factory" && Airstrike(rules[factory]) != null && Requirements(rules["upgrade.hightech"]) != null)
                    commandSets.Append($"    {slot++}: Command_{side.Prefix}Airstrike\n");
                if (queue != "Building")
                    commandSets.Append($"    {slot++}: Command_SetRallyPoint\n");
                commandSets.Append($"    {slot++}: Command_D2kRepair\n");
                commandSets.Append($"    {slot}: Command_Sell\n");
            }

            // ---- Objects ----
            foreach (var (key, (actor, queue, needs)) in buildable)
            {
                string image = Image(key, actor);
                string display = Name(actor, key);
                int cost = (int)Num(actor.Child("Valued")?.Get("Cost"), 0);
                float buildDuration = Num(actor.Child("Buildable")?.Get("BuildDuration"), -1);
                float buildSeconds = buildDuration > 0 ? buildDuration / TicksPerSecond : buildDuration == 0 ? 0.1f : Math.Max(2, cost / 100f * 1.6f); // Starport orders: no build time
                float hp = Num(actor.Child("Health")?.Get("HP"), 10000) / 100f;
                string armorSet = "D2K_Armor_" + (actor.Child("Armor")?.Get("Type") is { Length: > 0 } at ? at : "none");
                float sight = Math.Max(MiniYaml.Cells(actor.Child("RevealsShroud")?.Get("Range") ?? "") , 2) * CellUnits;
                var armaments = actor.All("Armament").ToList();
                var weaponList = armaments.Select((a, i) => (Slot: i == 0 ? "PRIMARY" : "SECONDARY", Weapon: ExportWeapon(a.Get("Weapon")))).Take(2).ToList();
                bool isBuilding = actor.Child("Building") != null || actor.Child("D2kBuilding") != null;
                bool isAircraft = actor.Child("Aircraft") != null;
                bool isInfantry = !isBuilding && !isAircraft && actor.Child("Mobile")?.Get("Locomotor") == "foot";
                string kind = isBuilding ? "Building" : isAircraft ? "Aircraft" : isInfantry ? "Infantry" : "Unit";
                string role = isBuilding ? StructureRoles.GetValueOrDefault(key, "STRUCT_D2K")
                    : key switch { "harvester" => "UNIT_HARVESTER", "mcv" => "UNIT_MCV", _ => isInfantry ? "INFANTRY_D2K" : isAircraft ? "AIRCRAFT_D2K" : "UNIT_D2K" };

                objects.Append($"  {N(key)}:\n    DisplayName: {display}\n    Side: {side.Prefix.TrimEnd('_')}\n");
                objects.Append($"    EditorSorting: {(isBuilding ? "STRUCTURE" : isInfantry ? "INFANTRY" : "VEHICLE")}\n");
                objects.Append($"    ButtonImage: D2KCAMEO:{image}\n    SelectPortrait: D2KCAMEO:{image}\n");
                objects.Append($"    BuildCost: {cost}\n    BuildTime: {F(buildSeconds)}\n");
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n");
                if (actor.Child("Power")?.Get("Amount") is { Length: > 0 } power && (int)Num(power) != 0)
                    objects.Append($"    EnergyProduction: {(int)Num(power)}\n");
                objects.Append(Prerequisites(needs));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {armorSet}\n");
                if (weaponList.Count > 0)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: [{string.Join(", ", weaponList.Select(w => $"{w.Slot} {w.Weapon}"))}]\n");

                if (isBuilding)
                {
                    var b = actor.Child("D2kBuilding") ?? actor.Child("Building")!;
                    var (occupy, footprint, center, dimsX, dimsY) = Footprint(b);
                    var kinds = new List<string> { "PRELOAD", "STRUCTURE", "SELECTABLE", "IMMOBILE", "SCORE", "TD_OBJECT", "MP_COUNT_FOR_VICTORY" };
                    if (key == "construction_yard")
                        kinds.AddRange(["COMMANDCENTER", "FS_FACTORY", "AUTO_RALLYPOINT"]);
                    if (key == "wind_trap")
                        kinds.Add("FS_POWER");
                    if (key == "refinery")
                        kinds.AddRange(["FS_SUPPLY_CENTER", "CASH_GENERATOR"]);
                    if (QueueFactory.ContainsValue(key) && key != "construction_yard")
                        kinds.AddRange(["FS_FACTORY", "AUTO_RALLYPOINT"]);
                    if (weaponList.Count > 0)
                        kinds.AddRange(["CAN_ATTACK", "FS_BASE_DEFENSE"]);
                    if (actor.Child("Capturable") != null)
                        kinds.Add("CAPTURABLE");
                    if (key == "repair_pad")
                        kinds.Add("REPAIR_PAD");
                    // Room for harvested spice (StoresPlayerResources).
                    if (actor.Child("StoresPlayerResources")?.Get("Capacity") is { Length: > 0 } capacity && Num(capacity) > 0)
                        objects.Append($"    TdStorage: {(int)Num(capacity)}\n");
                    objects.Append($"    RadarPriority: STRUCTURE\n    KindOf: {string.Join(' ', kinds)}\n");
                    objects.Append($"    Geometry: BOX\n    GeometryMajorRadius: {F(occupy.HalfX)}\n    GeometryMinorRadius: {F(occupy.HalfY)}\n    GeometryHeight: {F(Math.Min(dimsX, dimsY) * SizeUnits / 2)}\n    GeometryIsSmall: No\n");
                    objects.Append("    BuildCompletion: PLACED_BY_PLAYER\n    PlacementViewAngle: 0\n    TdBuiltByYard: Yes\n");
                    // Where the sprites' centre (the rules' CenterPosition) is, and the placement footprint, from the object's position.
                    objects.Append($"    TdArtOffset: 'X: {F(center.X - occupy.X)} Y: {F(center.Y - occupy.Y)} Z:0.0'\n");
                    objects.Append($"    TdFootprint: {F(footprint.X - occupy.X)} {F(footprint.Y - occupy.Y)} {F(footprint.HalfX)} {F(footprint.HalfY)}\n");
                    // The solid cells' centres from the object (for concrete, the rock-only rule and the rock damage).
                    objects.Append($"    D2kCells: '{string.Join(' ', SolidCells(b).Select(c => $"{F(c.X - occupy.X)},{F(c.Y - occupy.Y)}"))}'\n");
                    if (!b.Get("TerrainTypes").Contains("Sand"))
                        objects.Append("    TdRockOnly: Yes\n");
                    string factoryQueue = QueueFactory.FirstOrDefault(q => q.Value == key).Key ?? "";
                    // The palace's power (NukePower / ProduceActorPower): Death Hand, Fremen or a Saboteur.
                    string palacePower = "";
                    if (key == "palace" && actor.Child(side.Name switch { "harkonnen" => "NukePower", "atreides" => "ProduceActorPower@fremen", _ => "ProduceActorPower@saboteur" }) is { } palaceNode)
                    {
                        palacePower = $"D2K_Superweapon_{side.Prefix.TrimEnd('_')}";
                        float seconds = Num(palaceNode.Get("ChargeInterval"), 2250) / TicksPerSecond;
                        bool targeted = palaceNode.Key == "NukePower";
                        powers.Append($"  {palacePower}:\n    Enum: SPECIAL_{palacePower.ToUpperInvariant()}\n    ReloadTime: {(int)(seconds * 1000)}\n    PublicTimer: {(targeted ? "Yes" : "No")}\n    SharedSyncedTimer: No\n    ViewObjectDuration: 10000\n    ViewObjectRange: 250\n    RadiusCursorRadius: {(targeted ? 120 : 0)}\n    ShortcutPower: Yes\n");
                        if (targeted)
                            ocls.Append($"  OCL_{palacePower}:\n    Nuggets:\n    - Type: FireWeapon\n      Weapon: {ExportWeapon(palaceNode.Get("MissileWeapon"))}\n");
                        else
                        {
                            var units = palaceNode.Get("Actors").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            ocls.Append($"  OCL_{palacePower}:\n    Nuggets:\n    - Type: CreateObject\n      ObjectNames: {N(units[0])}\n      Count: {units.Length}\n      Offset: X:-20 Y:-70 Z:0\n");
                        }
                        string name = names.TryGetValue(palaceNode.Get("Name") + "", out var n) ? n : targeted ? "Death Hand" : units0(palaceNode);
                        buttons.Append($"  Command_{palacePower}:\n    Command: SPECIAL_POWER\n    SpecialPower: {palacePower}\n    Options: {(targeted ? "NEED_TARGET_POS CONTEXTMODE_COMMAND" : "OK_FOR_MULTI_SELECT")}\n    TextLabel: {name}\n    ButtonImage: D2KCAMEO:{(targeted ? "deathhand" : palaceNode.Get("Actors").Split(',')[0].Trim())}\n    ButtonBorderType: ACTION\n    DescriptLabel: {name}\n");
                        commandSets.Append($"  {N(key)}CommandSet:\n    1: Command_{palacePower}\n    2: Command_D2kRepair\n    3: Command_Sell\n");
                    }
                    objects.Append($"    CommandSet: {(palacePower.Length > 0 ? $"{N(key)}CommandSet" : factoryQueue.Length > 0 && exported.Contains(key) ? $"{N(key)}CommandSet" : "D2KStructureCommandSet")}\n");
                    objects.Append("    Modules:\n");
                    objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: {image}\n        TdKind: Building\n        TdType: {role}\n");
                    objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: StructureBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                    objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                    // Dune 2000's repair button: 10% of the health back each second for a share of the price.
                    // Concrete: laid, then gone (KillsSelf RemoveInstead); other buildings suffer off concrete (D2kBuilding's rock damage).
                    if (key.StartsWith("concrete"))
                        objects.Append("      ModuleTag_16:\n        Module: Behavior\n        Class: D2kConcrete\n");
                    else if (actor.Child("D2kBuilding") is { } d2kb)
                        objects.Append($"      ModuleTag_16:\n        Module: Behavior\n        Class: D2kRockDamage\n        Damage: {F(Num(d2kb.Get("Damage"), 500) / 100f)}\n        DamageInterval: {F(Num(d2kb.Get("DamageInterval"), 100) / TicksPerSecond)}\n        DamageThreshold: {(int)Num(d2kb.Get("DamageThreshold"), 50)}\n");
                    objects.Append($"      ModuleTag_12:\n        Module: Behavior\n        Class: TdStructureRepair\n        RepairStep: {F(hp / 10)}\n        RepairPercent: 25\n");
                    // The high tech factory's Airstrike (AirstrikePower, Atreides once upgraded): three ornithopters bomb the target.
                    if (key == "high_tech_factory" && Airstrike(actor) is { } strike && Requirements(rules["upgrade.hightech"]) != null)
                    {
                        string strikePower = $"D2K_Superweapon_{side.Prefix}Airstrike";
                        string plane = $"{side.Prefix}ORNITHOPTER_STRIKE";
                        var orni = rules["ornithopter"];
                        float orniSpeed = Num(orni.Child("Aircraft")?.Get("Speed"), 224) / 1024f * CellUnits * TicksPerSecond;
                        powers.Append($"  {strikePower}:\n    Enum: SPECIAL_{strikePower.ToUpperInvariant()}\n    ReloadTime: {(int)(Num(strike.Get("ChargeInterval"), 7500) / TicksPerSecond * 1000)}\n    RequiredUpgrade: {UpgradeName("upgrade.hightech")}\n" +
                            "    PublicTimer: No\n    SharedSyncedTimer: No\n    ViewObjectDuration: 10000\n    ViewObjectRange: 150\n    RadiusCursorRadius: 60\n    ShortcutPower: Yes\n");
                        ocls.Append($"  OCL_{strikePower}:\n    Nuggets:\n    - Type: DeliverPayload\n      Transport: {plane}\n      FormationSize: {(int)Num(strike.Get("SquadSize"), 3)}\n      FormationSpacing: 40.0\n" +
                            "      StartAtPreferredHeight: Yes\n      StartAtMaxSpeed: Yes\n      MaxAttempts: 1\n      DeliveryDistance: 300\n      DiveStartDistance: 220\n      DiveEndDistance: 40\n      StrafingWeaponSlot: PRIMARY\n");
                        buttons.Append($"  Command_{side.Prefix}Airstrike:\n    Command: SPECIAL_POWER\n    SpecialPower: {strikePower}\n    Options: NEED_TARGET_POS CONTEXTMODE_COMMAND\n    TextLabel: Airstrike\n    ButtonImage: D2KCAMEO:icon/{strike.Get("Icon")}\n    ButtonBorderType: ACTION\n    DescriptLabel: Airstrike\n");
                        objects.Append($"      ModuleTag_15:\n        Module: Behavior\n        Class: OCLSpecialPower\n        SpecialPowerTemplate: {strikePower}\n        OCL: OCL_{strikePower}\n        CreateLocation: CREATE_AT_EDGE_NEAR_TARGET\n");
                        // The ornithopter (only ever flown by this power).
                        strikePlanes.Append($"  {plane}:\n    DisplayName: Ornithopter\n    Side: {side.Prefix.TrimEnd('_')}\n    EditorSorting: VEHICLE\n    ButtonImage: D2KCAMEO:ornithopter\n    SelectPortrait: D2KCAMEO:ornithopter\n");
                        strikePlanes.Append($"    KindOf: PRELOAD CAN_ATTACK VEHICLE AIRCRAFT SCORE TD_OBJECT IGNORED_IN_GUI\n    Locomotor: SET_NORMAL {plane}Locomotor\n");
                        strikePlanes.Append($"    ArmorSet:\n      Conditions: None\n      Armor: D2K_Armor_{orni.Child("Armor")?.Get("Type") ?? "light"}\n");
                        strikePlanes.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: PRIMARY {ExportWeapon(orni.Child("Armament")?.Get("Weapon") ?? "OrniBomb")}\n");
                        strikePlanes.Append("    Geometry: BOX\n    GeometryMajorRadius: 14\n    GeometryMinorRadius: 14\n    GeometryHeight: 6\n    GeometryIsSmall: Yes\n    Modules:\n");
                        strikePlanes.Append("      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: ornithopter\n        TdKind: Aircraft\n        TdType: AIRCRAFT_D2K\n");
                        strikePlanes.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(Num(orni.Child("Health")?.Get("HP"), 8000) / 100f)}\n        InitialHealth: {F(Num(orni.Child("Health")?.Get("HP"), 8000) / 100f)}\n");
                        strikePlanes.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DeliverPayloadAIUpdate\n      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                        strikePlanes.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 100\n        FX: FINAL FX_GenericTankDeathExplosion\n");
                        locomotors.Append($"  {plane}Locomotor:\n    Surfaces: AIR\n    Speed: {F(orniSpeed)}\n    SpeedDamaged: {F(orniSpeed)}\n    MinSpeed: {F(orniSpeed * 0.5f)}\n    TurnRate: 120\n    TurnRateDamaged: 120\n    MaxThrustAngle: 60\n" +
                            "    Acceleration: 120\n    AccelerationDamaged: 120\n    Lift: 120\n    LiftDamaged: 80\n    Braking: 10\n    MinTurnSpeed: 60\n    PreferredHeight: 80\n    AllowAirborneMotiveForce: Yes\n" +
                            "    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n    CirclingRadius: 100\n    Appearance: WINGS\n");
                    }
                    if (palacePower.Length > 0)
                        objects.Append($"      ModuleTag_13:\n        Module: Behavior\n        Class: OCLSpecialPower\n        SpecialPowerTemplate: {palacePower}\n        OCL: OCL_{palacePower}\n        CreateLocation: CREATE_AT_LOCATION\n");
                    if (key == "repair_pad")
                        objects.Append("      ModuleTag_11:\n        Module: Behavior\n        Class: RepairDockUpdate\n        TimeForFullHeal: 5000\n        NumberApproachPositions: 3\n");
                    if (weaponList.Count > 0)
                    {
                        objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                        if (actor.Child("Turreted") != null)
                            objects.Append("        Turret:\n          TurretTurnRate: 180\n          ControlledWeaponSlots: PRIMARY\n");
                    }
                    if (factoryQueue.Length > 0)
                    {
                        objects.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                        if (factoryQueue == "Building")
                            objects.Append("        PlacesStructures: Yes\n");
                        else
                        {
                            // Units leave from the bottom edge.
                            float bottom = -footprint.HalfY + (footprint.Y - occupy.Y);
                            // The Starport holds its orders for the frigate (BulkProductionQueue: DeliveryDelay 1500 ticks, MaxCapacity 6).
                            objects.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: {(factoryQueue == "Starport" ? "D2kStarportExit" : "DefaultProductionExitUpdate")}\n        UnitCreatePoint: 'X: 0 Y: {F(bottom + 8)} Z:0.0'\n        NaturalRallyPoint: 'X: 0 Y: {F(bottom - 40)} Z:0.0'\n");
                        }
                    }
                    if (key == "refinery")
                    {
                        // The harvester docks where the rules' DockHost says, from the sprites' centre.
                        var offset = Coord(actor.Child("DockHost")?.Get("DockOffset") ?? "1c0,512,0");
                        float dx = center.X - occupy.X + offset.X, dy = center.Y - occupy.Y + offset.Y;
                        objects.Append("      ModuleTag_08:\n        Module: Behavior\n        Class: SupplyCenterCreate\n");
                        objects.Append($"      ModuleTag_09:\n        Module: Behavior\n        Class: SupplyCenterDockUpdate\n        AllowsPassthrough: No\n        NumberApproachPositions: -1\n        DockActionOffset: 'X: {F(dx)} Y: {F(dy)} Z:0.0'\n        DockStartOffset: 'X: {F(dx)} Y: {F(dy - SizeUnits)} Z:0.0'\n        DockEndOffset: 'X: {F(dx)} Y: {F(dy - SizeUnits)} Z:0.0'\n");
                        objects.Append($"      ModuleTag_10:\n        Module: Behavior\n        Class: TdFreeUnitOnBuilt\n        Unit: {N("harvester")}\n");
                    }
                    continue;
                }

                // ---- Units ----
                var mobile = actor.Child("Mobile");
                float speedRaw = isAircraft ? Num(actor.Child("Aircraft")!.Get("Speed"), 200) : Num(mobile?.Get("Speed"), 50);
                float speed = speedRaw / 1024f * CellUnits * TicksPerSecond;
                float turn = Num(isAircraft ? actor.Child("Aircraft")!.Get("TurnSpeed") : mobile?.Get("TurnSpeed"), 20);
                float turnDegrees = Math.Max(turn * 360f / 1024f * TicksPerSecond, 90); // WAngle a tick
                string loco = $"{N(key)}Locomotor";
                if (isAircraft)
                    locomotors.Append($"  {loco}:\n    Surfaces: AIR\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n    TurnRate: {F(turnDegrees)}\n    TurnRateDamaged: {F(turnDegrees)}\n    Acceleration: 60\n    AccelerationDamaged: 60\n    Lift: 120\n    LiftDamaged: 80\n    Braking: 240\n    MinTurnSpeed: 0\n    PreferredHeight: 40\n    AllowAirborneMotiveForce: Yes\n    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n    Appearance: HOVER\n    Apply2DFrictionWhenAirborne: Yes\n    AirborneTargetingHeight: 30\n    LocomotorWorksWhenDead: Yes\n");
                else if (isInfantry)
                    locomotors.Append($"  {loco}:\n    Surfaces: GROUND RUBBLE\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed)}\n    TurnRate: 500\n    TurnRateDamaged: 500\n    Acceleration: 100\n    AccelerationDamaged: 100\n    Braking: 100\n    MinTurnSpeed: 0\n    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: TWO_LEGS\n    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
                else
                    locomotors.Append($"  {loco}:\n    Surfaces: GROUND\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed * 0.8f)}\n    TurnRate: {F(turnDegrees)}\n    TurnRateDamaged: {F(turnDegrees)}\n    Acceleration: 1000\n    AccelerationDamaged: 1000\n    Braking: 1000\n    MinTurnSpeed: 0\n    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: {(mobile?.Get("Locomotor") is "tank" or "devastator" ? "TREADS" : "FOUR_WHEELS")}\n");

                var unitKinds = new List<string> { "PRELOAD", "SELECTABLE", "SCORE", "TD_OBJECT", isInfantry ? "INFANTRY" : isAircraft ? "AIRCRAFT" : "VEHICLE" };
                if (weaponList.Count > 0)
                    unitKinds.Add("CAN_ATTACK");
                if (key == "harvester")
                    unitKinds.Add("HARVESTER");
                if (isAircraft)
                    unitKinds.Add("VTOL");
                float radius = isInfantry ? 4.5f : key is "harvester" or "mcv" or "devastator" ? 15 : 11.5f;
                objects.Append($"    IsTrainable: Yes\n    RadarPriority: UNIT\n    KindOf: {string.Join(' ', unitKinds)}\n");
                objects.Append($"    Locomotor: SET_NORMAL {loco}\n");
                objects.Append($"    Geometry: {(isInfantry ? "CYLINDER" : "BOX")}\n    GeometryMajorRadius: {F(radius)}\n    GeometryMinorRadius: {F(radius)}\n    GeometryHeight: 11\n    GeometryIsSmall: Yes\n");
                objects.Append(isInfantry ? "    CrushableLevel: 0\n    TransportSlotCount: 1\n" : "    CrusherLevel: 1\n    CrushableLevel: 2\n");
                objects.Append("    CommandSet: GenericCommandSet\n    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: {image}\n        TdKind: {kind}\n        TdType: {role}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                if (key == "harvester")
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: 9\n        SupplyCenterActionDelay: 3000\n        SupplyWarehouseActionDelay: 600\n        SupplyWarehouseScanDistance: 900\n");
                else
                {
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (actor.Child("Turreted") is { } turret)
                        objects.Append($"        Turret:\n          TurretTurnRate: {F(Math.Max(Num(turret.Get("TurnSpeed"), 20) * 360f / 1024f * TicksPerSecond, 120))}\n          ControlledWeaponSlots: PRIMARY\n");
                }
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                objects.Append(isInfantry
                    ? "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 1000\n"
                    : "      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 100\n        FX: FINAL FX_GenericTankDeathExplosion\n");
                if (key == "mcv")
                    objects.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {N("construction_yard")}\n");
                // Engineers capture buildings (Captures), saboteurs blow them up (Demolition); both are used up.
                if (actor.Child("Captures") != null && key == "engineer")
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Capture\n");
                if (actor.Child("Demolition") != null)
                    objects.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Demolish\n        Consumed: Yes\n");
                // What draws sandworms (AttractsWorms), and the thumper's deploy.
                if (actor.Child("AttractsWorms") is { } attracts)
                    objects.Append($"      ModuleTag_11:\n        Module: Behavior\n        Class: D2kWormAttractor\n        Intensity: {(int)Num(attracts.Get("Intensity"), 100)}\n" +
                        (attracts.Get("Falloff") is { Length: > 0 } falloff ? $"        Falloff: {falloff.Replace(",", "")}\n" : "") +
                        (attracts.Get("RequiresCondition").Contains("deployed") ? "        OnlyWhenDeployed: Yes\n" : ""));
                // Carryalls ferry vehicles that have far to go (AutoCarryall, AutoCarryable / CarryableHarvester).
                if (actor.Child("AutoCarryall") is { } carryall)
                    objects.Append($"      ModuleTag_13:\n        Module: Behavior\n        Class: D2kCarryall\n        BeforeLoadDelay: {F(Num(carryall.Get("BeforeLoadDelay"), 10) / TicksPerSecond)}\n        BeforeUnloadDelay: {F(Num(carryall.Get("BeforeUnloadDelay"), 15) / TicksPerSecond)}\n");
                if (actor.Child("AutoCarryable") != null || actor.Child("CarryableHarvester") != null)
                    objects.Append("      ModuleTag_14:\n        Module: Behavior\n        Class: D2kCarryable\n        MinDistance: 6\n");
                if (key == "thumper")
                    objects.Append("      ModuleTag_12:\n        Module: Behavior\n        Class: D2kThumper\n");
                // Cloaking units (the Stealth Raider): hidden unless firing.
                if (actor.Child("Cloak") != null)
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: StealthUpdate\n        StealthDelay: 1500\n        StealthForbiddenConditions: ATTACKING FIRING_PRIMARY\n        InnateStealth: Yes\n        OrderIdleEnemiesToAttackMeUponReveal: No\n");
            }

            factions.Append($"  {side.Faction}:\n    Side: {side.Prefix.TrimEnd('_')}\n    BaseSide: {side.BaseSide}\n    PlayableSide: Yes\n    StartMoney: 0\n");
            factions.Append($"    PreferredColor: {side.Color}\n    DisplayName: {side.DisplayName}\n    StartingUnit0: {N("mcv")}\n    TdFaction: Yes\n");
            factions.Append($"    ScoreScreenImage: {side.ScoreImage}\n    LoadScreenImage: {side.LoadImage}\n    BeaconName: MultiplayerBeacon\n    OldFaction: Yes\n");
        }
        commandSets.Append("  D2KStructureCommandSet:\n    1: Command_D2kRepair\n    2: Command_Sell\n");

        // ---- Armour: each D2K type from its Zero Hour base, with the warheads' Versus averaged per damage type ----
        var armor = new StringBuilder("Armor:\n");
        foreach (var (type, baseName) in Armors)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (zhArmor.TryGet(baseName, out var b) && b.TryGet("Armor", out var list))
                foreach (var e in list.AsList())
                    if (RuleValue.Words(e.Value) is [var dt, var pct, ..])
                        entries[dt] = pct;
            foreach (var (dt, tables) in versusByType)
            {
                var values = tables.Select(t => t.TryGetValue(type, out int v) ? v : 100).ToList();
                entries[dt] = $"{(int)values.Average()}%";
            }
            armor.Append($"  D2K_Armor_{type}:\n    Armor:\n");
            foreach (var (dt, pct) in entries)
                armor.Append($"    - {dt} {pct}\n");
        }

        // ---- Spice for Dune 2000 maps: a supply pile per 2x2 cells, sized by how much spice it covers ----
        var scenery = new StringBuilder("Objects:\n");
        for (int boxes = 1; boxes <= 12; boxes++)
        {
            scenery.Append($"  D2K_Spice{boxes:00}:\n    DisplayName: Spice\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE SUPPLY_SOURCE STRUCTURE TD_OBJECT\n");
            scenery.Append("    Geometry: CYLINDER\n    GeometryMajorRadius: 10\n    GeometryMinorRadius: 10\n    GeometryHeight: 3\n    GeometryIsSmall: Yes\n");
            scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: InvulnerableAllArmor\n    Modules:\n");
            scenery.Append("      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: resources\n        TdKind: Spice\n        TdType: Spice\n");
            scenery.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 1.0\n        InitialHealth: 1.0\n");
            scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyWarehouseCreate\n");
            scenery.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: SupplyWarehouseDockUpdate\n        StartingBoxes: {boxes}\n        DeleteWhenEmpty: Yes\n        AllowsPassthrough: Yes\n        NumberApproachPositions: -1\n");
        }
        // The sandworm (Creeps' sandworm: 1000 health, heavy armour, Speed 42), the map's worm spawners and spice bloom points.
        var worm = rules.GetValueOrDefault("sandworm");
        float wormSpeed = Num(worm?.Child("Mobile")?.Get("Speed"), 42) / 1024f * CellUnits * TicksPerSecond;
        scenery.Append("  D2K_Sandworm:\n    DisplayName: Sandworm\n    EditorSorting: CREEP\n    KindOf: VEHICLE TD_OBJECT CAN_SEE_THROUGH_STRUCTURE\n    Locomotor: SET_NORMAL D2K_SandwormLocomotor\n");
        scenery.Append($"    VisionRange: {F(5 * CellUnits)}\n    ShroudClearingRange: {F(5 * CellUnits)}\n    CrushableLevel: 255\n    CrusherLevel: 3\n");
        scenery.Append("    Geometry: CYLINDER\n    GeometryMajorRadius: 14\n    GeometryMinorRadius: 14\n    GeometryHeight: 6\n    GeometryIsSmall: Yes\n");
        scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: D2K_Armor_heavy\n    Modules:\n");
        scenery.Append("      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: sandworm\n        TdKind: Worm\n        TdType: Worm\n");
        scenery.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {F(Num(worm?.Child("Health")?.Get("HP"), 100000) / 100f)}\n        InitialHealth: {F(Num(worm?.Child("Health")?.Get("HP"), 100000) / 100f)}\n");
        scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: AIUpdateInterface\n      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 50.0\n");
        scenery.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: D2kSandworm\n        WanderMoveRadius: {F(Num(worm?.Child("Sandworm")?.Get("WanderMoveRadius"), 5))}\n");
        scenery.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: DestroyDie\n");
        locomotors.Append($"  D2K_SandwormLocomotor:\n    Surfaces: GROUND\n    Speed: {F(wormSpeed)}\n    SpeedDamaged: {F(wormSpeed)}\n    TurnRate: 180\n    TurnRateDamaged: 180\n    Acceleration: 200\n    AccelerationDamaged: 200\n    Braking: 200\n    MinTurnSpeed: 0\n    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: OTHER\n");
        scenery.Append("  D2K_WormSpawner:\n    DisplayName: Worm Spawner\n    EditorSorting: SYSTEM\n    KindOf: IMMOBILE NO_COLLIDE UNATTACKABLE INERT TD_OBJECT\n    Modules:\n");
        scenery.Append("      ModuleTag_01:\n        Module: Behavior\n        Class: D2kWormSpawner\n        InitialDelay: 360\n        SpawnInterval: 240\n        Maximum: 4\n");
        scenery.Append("  D2K_SpiceBloomPoint:\n    DisplayName: Spice Bloom\n    EditorSorting: SYSTEM\n    KindOf: IMMOBILE NO_COLLIDE TD_OBJECT\n    Modules:\n");
        scenery.Append("      ModuleTag_01:\n        Module: Draw\n        Class: D2KSpriteDraw\n        Image: spicebloom\n        TdKind: Bloom\n        TdType: Bloom\n");
        scenery.Append($"      ModuleTag_02:\n        Module: Behavior\n        Class: D2kSpiceBloom\n        Weapon: {ExportWeapon("BloomExplosion")}\n        Spice: D2K_Spice03\n");
        File.WriteAllText(Path.Combine(output, "scenery.yaml"), "# GENERATED by 'OpenWestwood.Tools export-d2k-factions'.\n" + scenery);

        const string header = "# GENERATED by 'OpenWestwood.Tools export-d2k-factions' from the Dune 2000 data in content/dune2000.\n# Dune 2000's houses as Zero Hour engine factions. Edit freely, or re-export.\n";
        File.WriteAllText(Path.Combine(output, "factions.yaml"), header + factions);
        File.WriteAllText(Path.Combine(output, "objects.yaml"), header + objects + strikePlanes);
        File.WriteAllText(Path.Combine(output, "projectiles.yaml"), header + projectiles);
        File.WriteAllText(Path.Combine(output, "weapons.yaml"), header + weapons);
        File.WriteAllText(Path.Combine(output, "locomotors.yaml"), header + locomotors);
        File.WriteAllText(Path.Combine(output, "armor.yaml"), header + armor);
        File.WriteAllText(Path.Combine(output, "command-sets.yaml"), header + commandSets);
        File.WriteAllText(Path.Combine(output, "command-buttons.yaml"), header + buttons);
        File.WriteAllText(Path.Combine(output, "special-powers.yaml"), header + powers);
        File.WriteAllText(Path.Combine(output, "upgrades.yaml"), header + upgrades);
        File.WriteAllText(Path.Combine(output, "sound-effects.yaml"), header + "AudioEvents:\n" + string.Concat(soundGroups.Select(g =>
            $"  D2kSnd_{g.Key}:\n    Sounds: {string.Join(' ', g.Value.Select(f => "/" + f))}\n    Control: interrupt random\n    Priority: normal\n    Limit: 3\n    Volume: 80\n    Type: world shrouded everyone\n")));
        File.WriteAllText(Path.Combine(output, "object-creation-lists.yaml"), header + ocls);
        Console.WriteLine($"Wrote Dune 2000 factions to {output} ({used.Count} weapons)");
        return 0;
    }

    /// <summary>A summoning power's name when its text is missing: "Fremen", "Saboteur".</summary>
    static string units0(MiniYamlNode power) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(power.Get("Actors").Split(',')[0].Trim());

    /// <summary>The Zero Hour damage type for a warhead: by name for flame and sonic, else by what its Versus favours.</summary>
    static string DamageType(string weapon, Dictionary<string, int> versus)
    {
        string w = weapon.ToLowerInvariant();
        if (w.Contains("flame") || w.Contains("fire"))
            return "FLAME";
        if (w.Contains("sonic"))
            return "SONIC";
        int none = versus.GetValueOrDefault("none", 100), heavy = versus.GetValueOrDefault("heavy", 100);
        if (none >= 70 && heavy <= 40)
            return "SMALL_ARMS";
        if (heavy >= 70 && none <= 50)
            return "ARMOR_PIERCING";
        return "EXPLOSION";
    }

    /// <summary>
    /// A building's cells ("Footprint: xxx xxx ===" rows, x = solid, = = bib) as boxes in world units from the
    /// footprint's middle: the solid cells, the whole footprint, and the sprites' centre (the rules' CenterPosition:
    /// the middle of Dimensions plus LocalCenterOffset). +Y is north.
    /// </summary>
    static ((float X, float Y, float HalfX, float HalfY) Occupy, (float X, float Y, float HalfX, float HalfY) Footprint, (float X, float Y) Center, int DimsX, int DimsY)
        Footprint(MiniYamlNode building)
    {
        var rows = building.Get("Footprint").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0)
            rows = ["x"];
        var dims = building.Get("Dimensions").Split(',', StringSplitOptions.TrimEntries);
        int dimsX = dims.Length == 2 && int.TryParse(dims[0], out int dx) ? dx : rows.Max(r => r.Length);
        int dimsY = dims.Length == 2 && int.TryParse(dims[1], out int dy) ? dy : rows.Length;
        (float, float, float, float) Box(Func<char, bool> take)
        {
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    if (take(rows[y][x]))
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
            if (maxX < 0)
                (minX, maxX, minY, maxY) = (0, dimsX - 1, 0, dimsY - 1);
            float cx = (minX + maxX + 1) / 2f, cy = (minY + maxY + 1) / 2f;
            return ((cx - dimsX / 2f) * SizeUnits, -(cy - dimsY / 2f) * SizeUnits, (maxX - minX + 1) * SizeUnits / 2, (maxY - minY + 1) * SizeUnits / 2);
        }
        var local = Coord(building.Get("LocalCenterOffset"));
        return (Box(c => c == 'x'), Box(c => c != '_' && c != ' '), (local.X, local.Y), dimsX, dimsY);
    }

    /// <summary>The centres of a building's solid ("x") cells, in the same frame as <see cref="Footprint"/>.</summary>
    static List<(float X, float Y)> SolidCells(MiniYamlNode building)
    {
        var rows = building.Get("Footprint").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0)
            rows = ["x"];
        var dims = building.Get("Dimensions").Split(',', StringSplitOptions.TrimEntries);
        int dimsX = dims.Length == 2 && int.TryParse(dims[0], out int dx) ? dx : rows.Max(r => r.Length);
        int dimsY = dims.Length == 2 && int.TryParse(dims[1], out int dy) ? dy : rows.Length;
        var cells = new List<(float, float)>();
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == 'x')
                    cells.Add(((x + 0.5f - dimsX / 2f) * SizeUnits, -(y + 0.5f - dimsY / 2f) * SizeUnits));
        return cells;
    }

    /// <summary>A rule-unit offset "x,y,z" (1024 to a cell, +y south) in world units (+Y north).</summary>
    static (float X, float Y) Coord(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        float P(int i) => i < parts.Length ? MiniYaml.Cells(parts[i]) * SizeUnits : 0;
        return (P(0), -P(1));
    }

    static float Num(string? value, float fallback = 0) =>
        float.TryParse((value ?? "").Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>The ".name" texts of a Fluent file ("actor-x =" then "    .name = Text").</summary>
    static Dictionary<string, string> Fluent(string file)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(file))
            return result;
        string? id = null;
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Contains('='))
                id = line[..line.IndexOf('=')].Trim();
            else if (id != null && line.TrimStart().StartsWith(".name") && line.Contains('='))
                result[id + ".name"] = line[(line.IndexOf('=') + 1)..].Trim();
        }
        return result;
    }
}
