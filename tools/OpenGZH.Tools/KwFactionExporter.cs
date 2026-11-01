using System.Globalization;
using System.Text;
using System.Xml.Linq;
using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Westwood;
using OpenGZH.Logic.Core;

namespace OpenGZH.Tools;

/// <summary>
/// export-kw-factions: Kane's Wrath's GDI, Nod and Scrin, with their six sub-factions (Steel Talons, ZOCOM, Black Hand,
/// Marked of Kane, Reaper-17, Traveler-59), as Zero Hour engine factions (mods/zh/rules/kw/), from EA's source XML
/// (content/kaneswrath/sdk/Xml) and the game's own models, textures and portraits (drawn through SageArtPackage).
///
/// Each faction's arsenal is what its MCV's construction yard, and everything that builds, can make (their command
/// sets). Kane's Wrath is a SAGE game like Zero Hour, so most values carry over as they are: costs, build times (s),
/// health, ranges, speeds (world units a second), its damage types and armour tables, its locomotors.
/// It plays on the Tiberian Dawn framework the other C&amp;C factions use (sidebar, power, harvesters, the TD AI).
/// Squads (a HordeContain of N soldiers) are built as N soldiers at the squad's price.
/// </summary>
public static class KwFactionExporter
{
    const string P = "KW_";
    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Plain(string s) => s.Replace(": ", " - ").Replace(" #", " ").Replace("\n", " ").Trim();

    sealed record Faction(string Template, string Display, string Side, string BaseSide, string Mcv, string Color);

    static readonly Faction[] Factions =
    [
        new("FactionKWGDI", "GDI (Kane's Wrath)", "KWGDI", "USA", "GDIMCV", "R:230 G:190 B:40"),
        new("FactionKWSteelTalons", "Steel Talons (Kane's Wrath)", "KWGDI", "USA", "SteelTalonsMCV", "R:120 G:140 B:160"),
        new("FactionKWZOCOM", "ZOCOM (Kane's Wrath)", "KWGDI", "USA", "ZOCOMMCV", "R:90 G:170 B:230"),
        new("FactionKWNod", "Nod (Kane's Wrath)", "KWNod", "GLA", "NODMCV", "R:200 G:20 B:20"),
        new("FactionKWBlackHand", "Black Hand (Kane's Wrath)", "KWNod", "GLA", "BlackHandMCV", "R:60 G:60 B:60"),
        new("FactionKWMarkedOfKane", "Marked of Kane (Kane's Wrath)", "KWNod", "GLA", "MarkedOfKaneMCV", "R:150 G:200 B:60"),
        new("FactionKWScrin", "Scrin (Kane's Wrath)", "KWScrin", "China", "AlienMCV", "R:170 G:40 B:200"),
        new("FactionKWReaper17", "Reaper-17 (Kane's Wrath)", "KWScrin", "China", "Reaper17MCV", "R:200 G:80 B:160"),
        new("FactionKWTraveler59", "Traveler-59 (Kane's Wrath)", "KWScrin", "China", "Traveler59MCV", "R:110 G:60 B:220"),
    ];

    static readonly HashSet<string> BuildCommands = new(StringComparer.OrdinalIgnoreCase) { "UNIT_BUILD", "CONSTRUCTION_YARD_CONSTRUCT", "DOZER_CONSTRUCT" };

    public static int Run(string root)
    {
        string sdk = Path.Combine(root, "content", "kaneswrath", "sdk", "Xml");
        if (!Directory.Exists(sdk))
        {
            Console.Error.WriteLine($"{sdk} not found (copy EA's Kane's Wrath modding data there).");
            return 1;
        }
        var x = new KwXml(sdk);
        var strings = LoadStrings(Path.Combine(root, "content", "kaneswrath"));
        if (Environment.GetEnvironmentVariable("KW_DEBUG") is { Length: > 0 } debug)
        {
            Console.WriteLine(x.Get("GameObject", debug)?.ToString() ?? "(none)");
            Console.WriteLine($"{strings.Count} strings; e.g. " + string.Join(" | ", strings.Keys.Where(k => k.Contains("Predator", StringComparison.OrdinalIgnoreCase)).Take(5)));
            return 0;
        }
        var e = new Exporter(x, strings);
        foreach (var faction in Factions)
            e.ExportFaction(faction);
        string output = Path.Combine(root, "mods", "zh", "rules", "kw");
        Directory.CreateDirectory(output);
        e.Write(output);
        Console.WriteLine("Weapons without damage: " + string.Join(" ", e.FailedWeapons));
        Console.WriteLine($"Wrote Kane's Wrath factions to {output} ({e.ObjectCount} objects, {e.WeaponCount} weapons)");
        return 0;
    }

    /// <summary>cnc3.csf, the newest patch's (its labels are "NAME:GDIPredatorTank"...).</summary>
    static Dictionary<string, string> LoadStrings(string kw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string big in new[] { @"Lang-english\1.0\English.big", @"Lang-english\1.1\patch1.big", @"Lang-english\1.2\patch2.big" })
        {
            string path = Path.Combine(kw, big);
            if (!File.Exists(path))
                continue;
            try
            {
                using var arc = new BigArchive(path);
                if (arc.FilePaths.FirstOrDefault(f => f.EndsWith(".csf", StringComparison.OrdinalIgnoreCase)) is not { } csf)
                    continue;
                using var s = arc.Open(csf);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                foreach (var (k, v) in CsfFile.Read(OpenGZH.Formats.Sage.SageManifest.Unpack(ms.ToArray())))
                    result[k] = v;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
            }
        }
        return result;
    }

    sealed class Exporter(KwXml x, Dictionary<string, string> strings)
    {
        readonly StringBuilder objects = new("Objects:\n");
        readonly StringBuilder weapons = new("Weapons:\n");
        readonly StringBuilder locomotors = new("Locomotors:\n");
        readonly StringBuilder armor = new("Armor:\n");
        readonly StringBuilder commandSets = new("CommandSets:\n");
        readonly StringBuilder buttons = new("CommandButtons:\n");
        readonly StringBuilder factions = new("PlayerTemplates:\n");
        readonly HashSet<string> doneObjects = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string?> doneWeapons = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> doneArmor = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> doneLocomotors = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> doneButtons = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> doneSets = new(StringComparer.OrdinalIgnoreCase);

        public int ObjectCount => doneObjects.Count;
        public int WeaponCount => doneWeapons.Count(w => w.Value != null);
        public IEnumerable<string> FailedWeapons => doneWeapons.Where(w => w.Value == null).Select(w => w.Key);

        XElement? Obj(string id) => x.Get("GameObject", id);

        string Text(string? label, string fallback)
        {
            if (label is { Length: > 0 } l && strings.TryGetValue(l, out var s) && s.Length > 0)
                return Plain(s);
            return Plain(fallback);
        }

        string DisplayName(XElement o, string id) => Text(KwXml.Child(o, "DisplayName")?.Value.Trim(), id);

        string[] KindOf(XElement o) => x.Attr(o, "KindOf").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool Is(XElement o, string kind) => KindOf(o).Contains(kind, StringComparer.OrdinalIgnoreCase);

        /// <summary>The squad's soldier and how many (a HordeContain's InitialPayload), or null.</summary>
        (string Member, int Count)? Squad(XElement o)
        {
            foreach (var m in KwXml.AllModules(o))
                if (m.Name.LocalName == "HordeContain" && KwXml.Child(m, "InitialPayload") is { } payload && x.Attr(payload, "Name") is { Length: > 0 } member && Obj(member) != null)
                    return (member, (int)Math.Max(1, x.Num(payload, "Count", 1)));
            return null;
        }

        /// <summary>The objects a command set's build commands make.</summary>
        IEnumerable<string> Builds(XElement o)
        {
            var set = x.Get("LogicCommandSet", x.Attr(o, "CommandSet"));
            foreach (var cmd in KwXml.Children(set, "Cmd"))
            {
                var command = x.Get("LogicCommand", cmd.Value.Trim());
                if (command != null && BuildCommands.Contains(x.Attr(command, "Type")) && KwXml.Child(command, "Object")?.Value.Trim() is { Length: > 0 } made && Obj(made) != null)
                    yield return made;
            }
        }

        /// <summary>What the MCV becomes: its ReplaceSelfUpgrade's replacement, or (Scrin) what its unpacking object's death creates.</summary>
        string? YardOf(string mcv)
        {
            string current = mcv;
            for (int step = 0; step < 4; step++)
            {
                var o = Obj(current);
                if (o == null)
                    return null;
                if (step > 0 && Is(o, "CONSTRUCTION_YARD"))
                    return current;
                string? next = null;
                foreach (var m in KwXml.AllModules(o))
                {
                    if (m.Name.LocalName == "ReplaceSelfUpgrade" && KwXml.Child(m, "ReplacementTemplate")?.Value.Trim() is { Length: > 0 } r)
                        next = r;
                    else if (m.Name.LocalName == "CreateObjectDie" && next == null && x.Get("ObjectCreationList", x.Attr(m, "CreationList")) is { } ocl
                             && ocl.Descendants(KwXml.Ns + "CreateObject").Select(c => c.Value.Trim()).FirstOrDefault(v => v.Length > 0 && !v.Contains('<')) is { } made)
                        next = made.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }
                if (next == null)
                    return null;
                current = next;
            }
            return null;
        }

        public void ExportFaction(Faction f)
        {
            string? yard = YardOf(f.Mcv);
            if (yard == null || Obj(f.Mcv) == null)
            {
                Console.Error.WriteLine($"{f.Template}: no MCV/yard ({f.Mcv})");
                return;
            }
            // Everything buildable, from the yard down.
            var tree = new List<string> { f.Mcv, yard };
            var seen = new HashSet<string>(tree, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tree.Count; i++)
                foreach (string made in Builds(Obj(tree[i])!))
                    if (seen.Add(made))
                        tree.Add(made);
            // (Squads are built as their soldiers: those are what get exported.)
            var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in tree)
                exported.Add(Squad(Obj(t)!) is { } sq ? sq.Member : t);

            foreach (string t in tree)
            {
                var o = Obj(t)!;
                if (Squad(o) is { } squad)
                    ExportObject(squad.Member, f, exported, o, squad.Count);
                else
                    ExportObject(t, f, exported, null, 1);
            }
            factions.Append($"  {f.Template}:\n    Side: {f.Side}\n    BaseSide: {f.BaseSide}\n    PlayableSide: Yes\n    StartMoney: 0\n    PreferredColor: {f.Color}\n");
            factions.Append($"    DisplayName: {f.Display}\n    StartingUnit0: {P}{f.Mcv}\n    TdFaction: Yes\n");
            factions.Append($"    ScoreScreenImage: {(f.BaseSide == "USA" ? "America" : f.BaseSide)}_ScoreScreen\n    LoadScreenImage: {(f.BaseSide switch { "USA" => "SAFactionLogoPage_US", "China" => "SNFactionLogoPage_China", _ => "SUFactionLogoPage_GLA" })}\n");
            factions.Append("    BeaconName: MultiplayerBeacon\n    OldFaction: Yes\n");
            Console.WriteLine($"{f.Template}: {tree.Count} buildable ({yard})");
        }

        /// <summary>Is this object (or something it inherits from) the given one? (Requirements name the base faction's buildings.)</summary>
        bool InheritsFrom(string id, string ancestor)
        {
            for (int i = 0; i < 8 && id.Length > 0; i++)
            {
                if (id.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
                    return true;
                id = (string?)RawInherit(id) ?? "";
            }
            return false;
        }

        string? RawInherit(string id) => x.RawAttr("GameObject", id, "inheritFrom");

        string Prerequisites(XElement o, HashSet<string> exported)
        {
            var lines = new List<string>();
            foreach (var dep in KwXml.Children(o, "GameDependency"))
                foreach (var need in KwXml.Children(dep, "RequiredObject").Select(r => r.Value.Trim()).Where(r => r.Length > 0))
                {
                    var alternatives = exported.Where(t => InheritsFrom(t, need)).Select(t => P + t).ToList();
                    if (alternatives.Count > 0)
                        lines.Add(string.Join(' ', alternatives));
                }
            if (lines.Count == 0)
                return "    Prerequisites: {}\n";
            return "    Prerequisites:\n      Object:\n" + string.Concat(lines.Distinct().Select(l => $"      - {l}\n"));
        }

        string Role(XElement o, string id, Faction f, bool isMcv)
        {
            if (isMcv)
                return "UNIT_MCV";
            var modules = KwXml.AllModules(o).Select(m => m.Name.LocalName).ToHashSet();
            if (Is(o, "STRUCTURE"))
            {
                if (Is(o, "CONSTRUCTION_YARD"))
                    return "STRUCT_CONST";
                if (modules.Contains("SupplyCenterDockUpdate"))
                    return "STRUCT_REFINERY";
                if (Is(o, "FS_BARRACKS"))
                    return "STRUCT_BARRACKS";
                if (Is(o, "FS_WAR_FACTORY"))
                    return "STRUCT_WEAP";
                if (Is(o, "FS_AIR_FIELD") || Is(o, "AIRFIELD"))
                    return "STRUCT_HELIPAD";
                if (Is(o, "FS_POWER"))
                    return "STRUCT_POWER";
                if (Is(o, "FS_RADAR"))
                    return "STRUCT_RADAR";
                if (Is(o, "FS_TECH_CENTER"))
                    return "STRUCT_TECH";
                if (Is(o, "FS_BASE_DEFENSE") || Is(o, "CAN_ATTACK"))
                    return Is(o, "AA_STRUCT") ? "STRUCT_SAM" : "STRUCT_TURRET";
                return "STRUCT_RA2";
            }
            if (modules.Contains("SupplyTruckAIUpdate"))
                return "UNIT_HARVESTER";
            if (Is(o, "INFANTRY"))
                return "INFANTRY_E1";
            if (Is(o, "AIRCRAFT"))
                return "AIRCRAFT_RA2";
            return "UNIT_RA2";
        }

        void ExportObject(string id, Faction f, HashSet<string> exported, XElement? squad, int count)
        {
            if (!doneObjects.Add(id))
                return;
            var o = Obj(id)!;
            // A squad's soldier takes the squad's price, time, portrait, name and requirements.
            var shown = squad ?? o;
            bool structure = Is(o, "STRUCTURE");
            bool isMcv = id.Equals(f.Mcv, StringComparison.OrdinalIgnoreCase);
            bool infantry = Is(o, "INFANTRY");
            bool aircraft = Is(o, "AIRCRAFT");
            string role = Role(o, id, f, isMcv);
            string name = P + id;
            var sb = objects;
            sb.Append($"  {name}:\n    DisplayName: {DisplayName(shown, x.Attr(shown, "id"))}\n    Side: {f.Side}\n");
            sb.Append($"    EditorSorting: {(structure ? "STRUCTURE" : infantry ? "INFANTRY" : "VEHICLE")}\n");
            if (x.Attr(shown, "ButtonImage") is { Length: > 0 } button)
                sb.Append($"    ButtonImage: KWCAMEO:{button}\n");
            if (x.Attr(shown, "SelectPortrait") is { Length: > 0 } portrait)
                sb.Append($"    SelectPortrait: KWCAMEO:{portrait}\n");
            float cost = x.Num(shown, "BuildCost");
            sb.Append($"    BuildCost: {(int)cost}\n    BuildTime: {F(Math.Max(1, x.Num(shown, "BuildTime", 5)))}\n    TdRole: {role}\n");
            if (structure && x.Num(o, "EnergyProduction") is var energy and not 0)
                sb.Append($"    EnergyProduction: {(int)energy}\n");
            var vision = KwXml.Child(o, "VisionInfo");
            float sight = x.Num(vision, "VisionRange", structure ? 300 : 250);
            sb.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(x.Num(vision, "ShroudClearingRange", sight))}\n");
            sb.Append($"    RadarPriority: {(structure ? "STRUCTURE" : "UNIT")}\n");
            if (!structure)
                sb.Append("    IsTrainable: Yes\n");

            // Kinds: the Zero Hour ones Kane's Wrath shares, and the framework's.
            var kinds = KindOf(o).Where(k => Flags.KindOf.IndexOf(k) >= 0 && k is not ("CAN_ATTACK" or "HORDE")).ToList();
            kinds.AddRange(["PRELOAD", "SELECTABLE", "SCORE", "TD_OBJECT"]);
            if (structure)
            {
                kinds.AddRange(["STRUCTURE", "IMMOBILE"]);
                if (!Is(o, "WALL_SEGMENT") && !Is(o, "WALL_HUB"))
                    kinds.Add("MP_COUNT_FOR_VICTORY");
                switch (role)
                {
                    case "STRUCT_CONST": kinds.AddRange(["COMMANDCENTER", "FS_FACTORY", "AUTO_RALLYPOINT"]); break;
                    case "STRUCT_BARRACKS": kinds.AddRange(["FS_FACTORY", "FS_BARRACKS", "AUTO_RALLYPOINT"]); break;
                    case "STRUCT_WEAP": kinds.AddRange(["FS_FACTORY", "FS_WARFACTORY", "AUTO_RALLYPOINT"]); break;
                    case "STRUCT_HELIPAD": kinds.AddRange(["FS_FACTORY", "FS_AIRFIELD"]); break;
                    case "STRUCT_REFINERY": kinds.AddRange(["FS_SUPPLY_CENTER", "CASH_GENERATOR"]); break;
                    case "STRUCT_POWER": kinds.Add("FS_POWER"); break;
                    case "STRUCT_RADAR": kinds.Add("FS_TECHNOLOGY"); break;
                }
            }
            else
            {
                kinds.Add(infantry ? "INFANTRY" : aircraft ? "AIRCRAFT" : "VEHICLE");
                if (role == "UNIT_HARVESTER")
                    kinds.Add("HARVESTER");
                if (isMcv)
                    kinds.Add("TD_MCV");
                if (aircraft)
                    kinds.Add("VTOL");
            }

            // Weapons.
            var slots = new List<string>();
            bool turret = false, turretPitch = false;
            float turretRate = 180;
            var weaponSetUpdate = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "WeaponSetUpdate");
            foreach (var slot in weaponSetUpdate?.Elements() ?? [])
            {
                if (!slot.Name.LocalName.StartsWith("WeaponSlot"))
                    continue;
                foreach (var w in KwXml.Children(slot, "Weapon"))
                {
                    if (x.Attr(w, "ObjectStatus").Length > 0 || x.Attr(w, "Upgrade").Length > 0)
                        continue;
                    string ordering = x.Attr(w, "Ordering", "PRIMARY_WEAPON").Replace("_WEAPON", "");
                    if (ordering is not ("PRIMARY" or "SECONDARY" or "TERTIARY") || slots.Any(s => s.StartsWith(ordering + " ")))
                        continue;
                    if (Weapon(x.Attr(w, "Template"), o) is { } weapon)
                        slots.Add($"{ordering} {weapon}");
                }
                if (slot.Name.LocalName == "WeaponSlotTurret" && slots.Count > 0)
                {
                    turret = true;
                    var settings = KwXml.Child(slot, "TurretSettings");
                    turretRate = x.Num(settings, "TurretTurnRate", 180);
                    turretPitch = x.Bool(settings, "AllowsPitch");
                }
            }
            if (slots.Count > 0)
                kinds.Add("CAN_ATTACK");
            if (structure && slots.Count > 0)
                kinds.Add("FS_BASE_DEFENSE");
            sb.Append($"    KindOf: {string.Join(' ', kinds.Distinct())}\n");

            // Geometry: the first shape.
            var shape = KwXml.Child(KwXml.Child(o, "Geometry"), "Shape");
            string geometry = x.Attr(shape, "Type", infantry ? "CYLINDER" : "BOX").ToUpperInvariant();
            float major = x.Num(shape, "MajorRadius", infantry ? 5 : 15), minor = x.Num(shape, "MinorRadius", major);
            sb.Append($"    Geometry: {geometry}\n    GeometryMajorRadius: {F(major)}\n    GeometryMinorRadius: {F(minor)}\n    GeometryHeight: {F(x.Num(shape, "Height", 12))}\n");
            sb.Append($"    GeometryIsSmall: {(structure ? "No" : "Yes")}\n");
            if (structure)
            {
                sb.Append($"    TdFootprint: 0 0 {F(major)} {F(minor)}\n    BuildCompletion: PLACED_BY_PLAYER\n    PlacementViewAngle: 0\n    TdBuiltByYard: Yes\n    TdAdjacent: 6\n");
            }
            else if (infantry)
                sb.Append("    CrushableLevel: 0\n    TransportSlotCount: 1\n");
            else if (!aircraft)
                sb.Append($"    CrusherLevel: {(Is(o, "HUGE_VEHICLE") ? 3 : 1)}\n    CrushableLevel: 2\n");
            sb.Append(Prerequisites(shown, exported));

            // Armour, locomotor, weapons.
            string armorName = ArmorSet(o, structure, infantry);
            sb.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {armorName}\n");
            if (structure)
                sb.Append("      DamageFX: StructureDamageFXNoShake\n");
            if (slots.Count > 0)
                sb.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: [{string.Join(", ", slots)}]\n");
            if (!structure)
                sb.Append($"    Locomotor: SET_NORMAL {Locomotor(o, infantry, aircraft)}\n");

            // Transport.
            int passengers = 0;
            foreach (var m in KwXml.AllModules(o))
                if (m.Name.LocalName is "TransportContain" or "HordeTransportContain" or "GarrisonContain")
                    passengers = Math.Max(passengers, (int)x.Num(m, "Slots", x.Num(m, "ContainMax", 0)));

            // What it builds.
            var builds = Builds(o).ToList();
            var set = new List<string>();
            var quantities = new List<string>();
            foreach (string made in builds)
            {
                var mo = Obj(made)!;
                string unit = made;
                if (Squad(mo) is { } sq)
                {
                    unit = sq.Member;
                    if (sq.Count > 1)
                        quantities.Add($"{P}{sq.Member} {sq.Count}");
                }
                if (exported.Contains(unit))
                    set.Add(Button(unit, mo));
            }
            bool factory = builds.Count > 0;
            if (structure)
                set.AddRange(factory && role != "STRUCT_CONST" ? ["Command_SetRallyPoint", "Command_KwRepair", "Command_Sell"] : ["Command_KwRepair", "Command_Sell"]);
            string commandSet = structure || factory ? CommandSet($"{name}CommandSet", set) : passengers > 0 ? "KWTransportCommandSet" : "GenericCommandSet";
            sb.Append($"    CommandSet: {commandSet}\n");
            AddVoices(sb, o);

            // ---- Modules ----
            sb.Append("    Modules:\n");
            sb.Append(Draw(o, squad));
            // (The last body: an object's own replaces the base object's default.)
            float hp = x.Num(KwXml.Modules(o, "Body").LastOrDefault(b => x.Attr(b, "MaxHealth").Length > 0), "MaxHealth", structure ? 1000 : 100);
            sb.Append($"      ModuleTag_02:\n        Module: Body\n        Class: {(structure ? "StructureBody" : "ActiveBody")}\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
            if (structure)
            {
                sb.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                sb.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                sb.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: TdStructureRepair\n        RepairStep: {F(Math.Max(hp / 20, 5))}\n");
                if (slots.Count > 0)
                {
                    sb.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (turret)
                        sb.Append($"        Turret:\n          TurretTurnRate: {F(turretRate)}\n          ControlledWeaponSlots: PRIMARY\n");
                }
                if (factory)
                {
                    sb.Append("      ModuleTag_07:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                    if (role == "STRUCT_CONST" || builds.Any(b => Is(Obj(b)!, "STRUCTURE")))
                        sb.Append("        PlacesStructures: Yes\n");
                    if (quantities.Count > 0)
                        sb.Append("        QuantityModifier:\n" + string.Concat(quantities.Distinct().Select(q => $"        - {q}\n")));
                    if (role != "STRUCT_CONST")
                    {
                        var exit = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName.EndsWith("ProductionExitUpdate"));
                        var create = KwXml.Child(exit, "UnitCreatePoint");
                        var rally = KwXml.Child(exit, "NaturalRallyPoint");
                        float cx = x.Num(create, "x"), cy = x.Num(create, "y", -minor - 10);
                        float rx = x.Num(rally, "x", cx), ry = x.Num(rally, "y", cy - 40);
                        sb.Append($"      ModuleTag_08:\n        Module: Behavior\n        Class: DefaultProductionExitUpdate\n        UnitCreatePoint: 'X: {F(cx)} Y: {F(cy)} Z:0.0'\n        NaturalRallyPoint: 'X: {F(rx)} Y: {F(ry)} Z:0.0'\n");
                    }
                }
                if (role == "STRUCT_REFINERY")
                {
                    sb.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: SupplyCenterCreate\n");
                    sb.Append("      ModuleTag_10:\n        Module: Behavior\n        Class: SupplyCenterDockUpdate\n        AllowsPassthrough: No\n        NumberApproachPositions: -1\n");
                    sb.Append($"        DockStart: 'X: 0 Y: {F(-minor - 25)} Z:0.0'\n        DockAction: 'X: 0 Y: {F(-minor - 5)} Z:0.0'\n        DockEnd: 'X: 0 Y: {F(-minor - 5)} Z:0.0'\n");
                    string? harvester = KwXml.AllModules(o).Where(m => m.Name.LocalName == "SpawnBehavior").Select(m => KwXml.Child(m, "SpawnTemplate")?.Value.Trim()).FirstOrDefault(s => s is { Length: > 0 });
                    if (harvester != null && Obj(harvester) != null)
                    {
                        exported.Add(harvester);
                        sb.Append($"      ModuleTag_11:\n        Module: Behavior\n        Class: TdFreeUnitOnBuilt\n        Unit: {P}{harvester}\n");
                        pendingFree.Add((harvester, f, exported));
                    }
                }
            }
            else
            {
                if (role == "UNIT_HARVESTER")
                {
                    // A load: 300 credits (Kane's Wrath's harvester), in Zero Hour's 75-credit boxes.
                    sb.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: 4\n        SupplyCenterActionDelay: 1500\n        SupplyWarehouseActionDelay: 600\n        SupplyWarehouseScanDistance: 1500\n");
                }
                else
                {
                    sb.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: {(passengers > 0 ? "TransportAIUpdate" : "AIUpdateInterface")}\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (turret)
                        sb.Append($"        Turret:\n          TurretTurnRate: {F(turretRate)}\n          ControlledWeaponSlots: PRIMARY\n" + (turretPitch ? "          AllowsPitch: Yes\n" : ""));
                }
                sb.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                var death = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "SlowDeath");
                float destroy = Math.Clamp(x.Ms(death, "DestructionDelay", infantry ? 3000 : 500), 100, 8000);
                sb.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: {F(destroy)}\n");
                if (!infantry)
                    sb.Append($"        SinkDelay: {F(x.Ms(death, "SinkDelay", 3000))}\n        SinkRate: {F(x.Num(death, "SinkRate", 1) * 0.5f)}\n        FX: INITIAL FX_GenericTankDeathExplosion\n");
                if (isMcv && YardOf(id) is { } yard)
                    sb.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {P}{yard}\n");
                if (passengers > 0)
                    sb.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: TransportContain\n        Slots: {passengers}\n        AllowInsideKindOf: INFANTRY\n        ExitDelay: 250\n        NumberOfExitPaths: 1\n        GoAggressiveOnExit: Yes\n        DamagePercentToUnits: 100%\n");
                if (KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "StealthDetectorUpdate") is { } detector)
                    sb.Append($"      ModuleTag_08:\n        Module: Behavior\n        Class: StealthDetectorUpdate\n        DetectionRate: 500\n        DetectionRange: {F(x.Num(detector, "DetectionRange", 150))}\n");
                if (Is(o, "CAN_CAPTURE") || KwXml.AllModules(o).Any(m => m.Name.LocalName == "EngineerContain") || id.Contains("Engineer", StringComparison.OrdinalIgnoreCase))
                    if (!slots.Any())
                        sb.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: TdInfiltrate\n        Mode: Capture\n");
            }
            // Free units promised by refineries (their harvesters) that the build tree didn't reach.
            while (pendingFree.Count > 0)
            {
                var (h, hf, hexported) = pendingFree[0];
                pendingFree.RemoveAt(0);
                ExportObject(h, hf, hexported, null, 1);
            }
        }

        readonly List<(string, Faction, HashSet<string>)> pendingFree = new();

        void AddVoices(StringBuilder sb, XElement o)
        {
            // (Kane's Wrath's sounds are in its compiled audio streams: not yet played.)
        }

        string Button(string unit, XElement made)
        {
            string name = $"Command_KW_{unit}";
            if (doneButtons.Add(name))
            {
                string label = DisplayName(made, x.Attr(made, "id"));
                buttons.Append($"  {name}:\n    Command: UNIT_BUILD\n    Object: {P}{unit}\n    TextLabel: {label}\n");
                if (x.Attr(made, "ButtonImage") is { Length: > 0 } image)
                    buttons.Append($"    ButtonImage: KWCAMEO:{image}\n");
                buttons.Append($"    ButtonBorderType: BUILD\n    DescriptLabel: {label}\n");
            }
            return name;
        }

        string CommandSet(string name, IEnumerable<string> entries)
        {
            if (!doneSets.Add(name))
                return name;
            commandSets.Append($"  {name}:\n");
            int slot = 1;
            foreach (var e in entries)
                commandSets.Append($"    {slot++}: {e}\n");
            return name;
        }

        // ---- Draw ----

        static readonly System.Text.RegularExpressions.Regex SubObjectScript = new(@"CurDrawable(Show|Hide)SubObject\(""([^""]+)""\)");

        string Draw(XElement o, XElement? squad)
        {
            // The draw module with a model (squads draw nothing themselves; their soldiers do).
            XElement? draw = null;
            string model = "";
            foreach (var d in KwXml.Modules(o, "Draws"))
            {
                var def = KwXml.Children(d, "ModelConditionState").FirstOrDefault(s => x.Attr(s, "ParseCondStateType") == "PARSE_DEFAULT");
                if (x.Attr(KwXml.Child(def, "Model"), "Name") is { Length: > 0 } m)
                {
                    draw = d;
                    model = m;
                    break;
                }
            }
            if (draw == null)
                return "      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        DefaultConditionState:\n          Model: None\n";
            string cls = draw.Name.LocalName switch { "TankDraw" => "W3DTankDraw", _ => "W3DModelDraw" };
            var sb = new StringBuilder($"      ModuleTag_01:\n        Module: Draw\n        Class: {cls}\n        OkToChangeModelColor: Yes\n");
            if (cls == "W3DTankDraw")
                sb.Append("        TrackMarks: EXTnkTrack.tga\n        TreadAnimationRate: 2.0\n        TreadDriveSpeedFraction: 0.3\n        TreadPivotSpeedFraction: 0.6\n");

            var modelStates = KwXml.Children(draw, "ModelConditionState").ToList();
            var animStates = KwXml.Children(draw, "AnimationState").ToList();
            var defModel = modelStates.First(s => x.Attr(s, "ParseCondStateType") == "PARSE_DEFAULT");
            var defAnim = animStates.FirstOrDefault(s => x.Attr(s, "ParseCondStateType") == "PARSE_DEFAULT");

            // Sub-objects hidden at first: the default state's script hides them, or an upgrade shows them.
            var hidden = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in SubObjectScript.Matches(KwXml.Child(defAnim, "Script")?.Value ?? ""))
                if (m.Groups[1].Value == "Hide")
                    hidden.Add(m.Groups[2].Value.ToUpperInvariant());
            foreach (var up in KwXml.AllModules(o).Where(m => m.Name.LocalName == "SubObjectsUpgrade"))
                hidden.AddRange(x.Attr(up, "ShowSubObjects").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToUpperInvariant()));
            // Muzzle flashes appear only when firing.
            hidden.AddRange(["FXMUZZLEFLASH", "MUZZLEFLASH"]);

            sb.Append("        DefaultConditionState:\n");
            sb.Append(StateBody(defModel, defAnim, model, true));
            sb.Append($"          HideSubObject: {string.Join(' ', hidden.Distinct())}\n");

            // Other states whose flags Zero Hour knows.
            var states = new Dictionary<string, (XElement? Model, XElement? Anim)>();
            static string Key(string flags) => string.Join(' ', flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.ToUpperInvariant()));
            bool Known(string flags) => flags.Length > 0 && flags.Split(' ').All(f => Flags.ModelCondition.IndexOf(f) >= 0);
            foreach (var s in modelStates.Where(s => x.Attr(s, "ParseCondStateType") != "PARSE_DEFAULT"))
            {
                string k = Key(x.Attr(s, "ConditionsYes"));
                if (Known(k) && x.Attr(s, "ConditionsNo").Length == 0 && !states.ContainsKey(k))
                    states[k] = (s, null);
            }
            foreach (var s in animStates.Where(s => x.Attr(s, "ParseCondStateType") != "PARSE_DEFAULT" && KwXml.Children(s, "Animation").Any()))
            {
                string k = Key(x.Attr(s, "ConditionsYes"));
                if (!Known(k) || x.Attr(s, "ConditionsNo").Length > 0)
                    continue;
                states[k] = states.TryGetValue(k, out var v) ? (v.Model, v.Anim ?? s) : (null, s);
            }
            if (states.Count > 0)
            {
                sb.Append("        ConditionState:\n");
                foreach (var (k, (ms, an)) in states.Take(40))
                {
                    sb.Append($"          {k}:\n");
                    string body = StateBody(ms, an, x.Attr(KwXml.Child(ms, "Model"), "Name") is { Length: > 0 } sm ? sm : model, false, "            ");
                    sb.Append(body.Length > 0 ? body : $"            Model: {P}{model}\n");
                }
            }
            return sb.ToString();
        }

        string StateBody(XElement? modelState, XElement? animState, string model, bool isDefault, string indent = "          ")
        {
            var sb = new StringBuilder($"{indent}Model: {P}{model}\n");
            if (modelState != null)
            {
                if (KwXml.Child(modelState, "Turret") is { } t && x.Attr(t, "TurretNameKey") is { Length: > 0 } turret)
                {
                    sb.Append($"{indent}Turret: {turret.ToUpperInvariant()}\n");
                    if (x.Attr(t, "TurretPitch") is { Length: > 0 } pitch)
                        sb.Append($"{indent}TurretPitch: {pitch.ToUpperInvariant()}\n");
                }
                foreach (var (tag, key) in new[] { ("WeaponFireFXBone", "WeaponFireFXBone"), ("WeaponMuzzleFlash", "WeaponMuzzleFlash"), ("WeaponLaunchBone", "WeaponLaunchBone"), ("WeaponRecoilBone", "WeaponRecoilBone") })
                {
                    var bones = KwXml.Children(modelState, tag)
                        .Select(b => (Slot: x.Attr(b, "WeaponSlotType").Replace("_WEAPON", ""), Bone: x.Attr(b, "BoneName")))
                        .Where(b => b.Bone.Length > 0 && b.Slot is "PRIMARY" or "SECONDARY" or "TERTIARY").DistinctBy(b => b.Slot).ToList();
                    if (bones.Count == 1)
                        sb.Append($"{indent}{key}: {bones[0].Slot} {bones[0].Bone.ToUpperInvariant()}\n");
                    else if (bones.Count > 1)
                        sb.Append($"{indent}{key}: [{string.Join(", ", bones.Select(b => $"{b.Slot} {b.Bone.ToUpperInvariant()}"))}]\n");
                }
            }
            var anims = KwXml.Children(animState, "Animation").Select(a => x.Attr(a, "AnimationName")).Where(a => a.Length > 0).ToList();
            if (anims.Count > 0)
            {
                string mode = KwXml.Children(animState, "Animation").Select(a => x.Attr(a, "AnimationMode")).FirstOrDefault() ?? "";
                if (isDefault && anims.Count > 1)
                    sb.Append($"{indent}IdleAnimation: [{string.Join(", ", anims.Take(4).Select(a => P + a))}]\n{indent}AnimationMode: ONCE\n");
                else
                    sb.Append($"{indent}Animation: {P}{anims[0]}\n{indent}AnimationMode: {(mode is "ONCE" or "ONCE_BACKWARDS" && !isDefault ? "ONCE" : "LOOP")}\n");
            }
            // Sub-objects this state shows or hides.
            var shows = new List<string>();
            var hides = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in SubObjectScript.Matches(KwXml.Child(animState, "Script")?.Value ?? ""))
                (m.Groups[1].Value == "Show" ? shows : hides).Add(m.Groups[2].Value.ToUpperInvariant());
            if (!isDefault)
            {
                if (shows.Count > 0)
                    sb.Append($"{indent}ShowSubObject: {string.Join(' ', shows.Distinct())}\n");
                if (hides.Count > 0)
                    sb.Append($"{indent}HideSubObject: {string.Join(' ', hides.Distinct())}\n");
            }
            return sb.ToString();
        }

        // ---- Armour ----

        string ArmorSet(XElement o, bool structure, bool infantry)
        {
            var set = KwXml.Children(o, "ArmorSet").FirstOrDefault(a => x.Attr(a, "Conditions").Length == 0) ?? KwXml.Children(o, "ArmorSet").FirstOrDefault();
            string id = x.Attr(set, "Armor");
            var template = x.Get("ArmorTemplate", id);
            if (template == null)
                return structure ? "StructureArmor" : infantry ? "HumanArmor" : "TankArmor";
            string name = P + "Armor_" + id;
            if (doneArmor.Add(name))
            {
                armor.Append($"  {name}:\n    Armor:\n");
                bool hasDefault = false;
                foreach (var a in KwXml.Children(template, "Armor"))
                {
                    string type = x.Attr(a, "Damage").ToUpperInvariant();
                    if (type.Length == 0)
                        continue;
                    hasDefault |= type == "DEFAULT";
                    armor.Append($"    - {type} {F(x.Num(a, "Percent", 100))}%\n");
                }
                if (!hasDefault)
                    armor.Append("    - DEFAULT 100%\n");
            }
            return name;
        }

        // ---- Locomotors ----

        string Locomotor(XElement o, bool infantry, bool aircraft)
        {
            var set = KwXml.Children(o, "LocomotorSet").FirstOrDefault(l => x.Attr(l, "Condition", "NORMAL") == "NORMAL") ?? KwXml.Children(o, "LocomotorSet").FirstOrDefault();
            string id = x.Attr(set, "Locomotor");
            float speed = x.Num(set, "Speed", infantry ? 30 : 50);
            var t = x.Get("LocomotorTemplate", id);
            string name = $"{P}Loco_{(id.Length > 0 ? id : "Default")}_{(int)speed}";
            if (!doneLocomotors.Add(name))
                return name;
            var surfaces = x.Attr(t, "Surfaces", aircraft ? "AIR" : "GROUND").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToUpperInvariant()).Where(s => s is "GROUND" or "WATER" or "CLIFF" or "AIR" or "RUBBLE").ToList();
            if (surfaces.Count == 0)
                surfaces.Add(aircraft ? "AIR" : "GROUND");
            if (infantry && !surfaces.Contains("RUBBLE"))
                surfaces.Add("RUBBLE");
            float damagedPct = x.Attr(t, "MaxSpeedDamaged") is { Length: > 0 } dmg && dmg.EndsWith('%') ? KwXml.ParseNumber(dmg) / 100f : 1;
            float turnTime = Math.Max(0.1f, x.Ms(t, "TurnTimeSeconds", 1000) / 1000f);
            float accel = speed / Math.Max(0.05f, x.Ms(t, "AccelerationSeconds", 500) / 1000f);
            float braking = speed / Math.Max(0.05f, x.Ms(t, "BrakingSeconds", 500) / 1000f);
            string appearance = x.Attr(t, "Appearance", infantry ? "TWO_LEGS" : aircraft ? "HOVER" : "TREADS").ToUpperInvariant();
            if (appearance is not ("TWO_LEGS" or "FOUR_WHEELS" or "TREADS" or "HOVER" or "THRUST" or "WINGS" or "CLIMBER" or "OTHER" or "MOTORCYCLE"))
                appearance = infantry ? "TWO_LEGS" : aircraft ? "HOVER" : "TREADS";
            locomotors.Append($"  {name}:\n    Surfaces: {string.Join(' ', surfaces)}\n    Speed: {F(speed)}\n    SpeedDamaged: {F(speed * damagedPct)}\n");
            locomotors.Append($"    TurnRate: {F(360 / turnTime)}\n    TurnRateDamaged: {F(360 / turnTime)}\n    Acceleration: {F(accel)}\n    AccelerationDamaged: {F(accel)}\n    Braking: {F(braking)}\n    MinTurnSpeed: 0\n");
            if (aircraft)
            {
                // (Flown as helicopters for now: Kane's Wrath's jets need its airfield's landing pads.)
                float height = x.Num(t, "PreferredHeight", 100);
                locomotors.Append($"    Lift: 120\n    LiftDamaged: 80\n    PreferredHeight: {F(height)}\n    AllowAirborneMotiveForce: Yes\n    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n");
                locomotors.Append($"    Appearance: HOVER\n    Apply2DFrictionWhenAirborne: Yes\n    AirborneTargetingHeight: {F(height / 2)}\n    LocomotorWorksWhenDead: Yes\n");
            }
            else
            {
                locomotors.Append($"    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: {appearance}\n");
                if (infantry)
                    locomotors.Append("    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
            }
            return name;
        }

        // ---- Weapons ----

        /// <summary>The damage a weapon does: its own DamageNugget, or its projectile's warhead's.</summary>
        XElement? DamageOf(XElement weapon, int depth = 0)
        {
            var nuggets = KwXml.Child(weapon, "Nuggets");
            if (KwXml.Child(nuggets, "DamageNugget") is { } dn)
                return dn;
            if (depth < 3 && KwXml.Child(nuggets, "ProjectileNugget") is { } pn && x.Get("WeaponTemplate", x.Attr(pn, "WarheadTemplate")) is { } warhead)
                return DamageOf(warhead, depth + 1);
            if (depth < 3 && KwXml.Child(nuggets, "ActivateLaserNugget") is { } ln && x.Get("WeaponTemplate", x.Attr(ln, "HitGroundWarheadTemplate", x.Attr(ln, "WarheadTemplate"))) is { } lw)
                return DamageOf(lw, depth + 1);
            return null;
        }

        static readonly HashSet<string> DeathTypes = new(StringComparer.OrdinalIgnoreCase) { "NORMAL", "BURNED", "EXPLODED", "POISONED", "TOPPLED", "CRUSHED", "LASERED", "DETONATED", "SPLATTED" };

        float Seconds(XElement w, string element, float fallbackMs)
        {
            var e = KwXml.Child(w, element);
            if (e == null)
                return fallbackMs;
            return (x.Ms(e, "MinSeconds", fallbackMs) + x.Ms(e, "MaxSeconds", fallbackMs)) / 2;
        }

        string? Weapon(string id, XElement owner)
        {
            if (id.Length == 0)
                return null;
            if (doneWeapons.TryGetValue(id, out var done))
                return done;
            doneWeapons[id] = null;
            var w = x.Get("WeaponTemplate", id);
            if (w == null)
                return null;
            string dt, death;
            float amount, radius;
            float reload = Seconds(w, "ClipReloadTime", 1000), firing = Seconds(w, "FiringDuration", 0);
            if (DamageOf(w) is { } damage)
            {
                dt = x.Attr(damage, "DamageType", "EXPLOSION").ToUpperInvariant();
                amount = x.Num(damage, "Damage");
                death = x.Attr(damage, "DeathType", "NORMAL").ToUpperInvariant();
                radius = x.Num(damage, "Radius");
            }
            else if (KwXml.AllModules(owner).FirstOrDefault(m => m.Name.LocalName == "LinearDamageUpdate") is { } linear
                     && x.Get("WeaponTemplate", x.Attr(linear, "SweepWeapon")) is { } sweep && DamageOf(sweep) is { } sweepDamage)
            {
                // The sonic emitter / shatterer: a shock wave sweeping out along a line (SweepWeapon's damage).
                dt = x.Attr(sweepDamage, "DamageType", "SONIC").ToUpperInvariant();
                amount = x.Num(sweepDamage, "Damage");
                death = x.Attr(sweepDamage, "DeathType", "NORMAL").ToUpperInvariant();
                radius = Math.Max(x.Num(linear, "Radius", 10), x.Num(sweepDamage, "Radius"));
            }
            else if (KwXml.AllModules(owner).FirstOrDefault(m => m.Name.LocalName == "BeamSpecialAbilityUpdate" && x.Num(m, "DamagePerSecond") > 0) is { } beam)
            {
                // The beam cannon: a continuous beam (DamagePerSecond), a second's worth a shot.
                dt = "LASER";
                amount = x.Num(beam, "DamagePerSecond");
                death = "LASERED";
                radius = 5;
                reload = 1000;
                firing = 0;
            }
            else
                return null;
            string name = P + "Weapon_" + id;
            doneWeapons[id] = name;
            if (amount < 0)
                dt = "HEALING";
            if (!DeathTypes.Contains(death))
                death = "NORMAL";
            float range = Math.Max(x.Num(w, "AttackRange", 100), 10);
            int clip = (int)x.Num(w, "ClipSize", 0);
            weapons.Append($"  {name}:\n    PrimaryDamage: {F(Math.Abs(amount))}\n    PrimaryDamageRadius: {F(radius)}\n    AttackRange: {F(range)}\n");
            if (x.Num(w, "MinimumAttackRange") is > 0 and var min)
                weapons.Append($"    MinimumAttackRange: {F(min)}\n");
            weapons.Append($"    DamageType: {dt}\n    DeathType: {death}\n");
            if (clip > 1)
                weapons.Append($"    ClipSize: {clip}\n    ClipReloadTime: {F(reload)}\n    DelayBetweenShots: {F(Math.Max(firing, 100))}\n");
            else
                weapons.Append($"    DelayBetweenShots: {F(Math.Max(reload + firing, 100))}\n    ClipSize: 0\n    ClipReloadTime: 0\n");
            string affects = x.Attr(w, "RadiusDamageAffects", "ALLIES ENEMIES NEUTRALS");
            weapons.Append($"    RadiusDamageAffects: {(dt == "HEALING" ? "ALLIES SELF" : affects)}\n");
            string anti = x.Attr(w, "AntiMask", "ANTI_GROUND");
            bool ground = anti.Contains("ANTI_GROUND") || anti.Contains("ANTI_STRUCTURE"), air = anti.Contains("ANTI_AIRBORNE");
            weapons.Append($"    AntiGround: {(ground ? "Yes" : "No")}\n    AntiAirborneVehicle: {(air ? "Yes" : "No")}\n    AntiAirborneInfantry: {(air ? "Yes" : "No")}\n");
            // Zero Hour's look for its kind of damage (Kane's Wrath's particle effects aren't converted yet).
            var (fire, hit) = dt switch
            {
                "GUN" or "SNIPER" => ("WeaponFX_GenericMachineGunFire", ""),
                "CANNON" => ("WeaponFX_GenericTankGunNoTracer", "WeaponFX_GenericTankShellDetonation"),
                "GRENADE" or "ROCKET" => ("WeaponFX_GenericTankGunNoTracerSmall", "WeaponFX_JetMissileDetonation"),
                "FLAME" => ("WeaponFX_DragonTankFlameWeapon", ""),
                "LASER" or "MAGNETIC" or "SHOCKWAVE" or "SONIC" => ("WeaponFX_GenericTankGunNoTracerSmall", ""),
                _ => ("WeaponFX_GenericTankGunNoTracerSmall", ""),
            };
            weapons.Append($"    FireFX: {fire}\n");
            if (hit.Length > 0)
                weapons.Append($"    ProjectileDetonationFX: {hit}\n");
            return name;
        }

        public void Write(string output)
        {
            const string header = "# GENERATED by 'OpenGZH.Tools export-kw-factions' from Kane's Wrath's source XML (content/kaneswrath/sdk).\n" +
                                  "# GDI, Nod, the Scrin and their sub-factions as Zero Hour engine factions. Edit freely, or re-export.\n";
            commandSets.Append("  KWTransportCommandSet:\n    1: Command_TransportExit\n    2: Command_TransportExit\n    3: Command_TransportExit\n    4: Command_TransportExit\n    5: Command_TransportExit\n    9: Command_Evacuate\n    11: Command_AttackMove\n    13: Command_Guard\n    14: Command_Stop\n");
            buttons.Append("  Command_KwRepair:\n    Command: TD_REPAIR\n    TextLabel: Repair\n    ButtonImage: SSRepair\n    ButtonBorderType: ACTION\n    DescriptLabel: Repair this building (costs money as it mends)\n");
            File.WriteAllText(Path.Combine(output, "factions.yaml"), header + factions);
            File.WriteAllText(Path.Combine(output, "objects.yaml"), header + objects);
            File.WriteAllText(Path.Combine(output, "weapons.yaml"), header + weapons);
            File.WriteAllText(Path.Combine(output, "locomotors.yaml"), header + locomotors);
            File.WriteAllText(Path.Combine(output, "armor.yaml"), header + armor);
            File.WriteAllText(Path.Combine(output, "command-sets.yaml"), header + commandSets);
            File.WriteAllText(Path.Combine(output, "command-buttons.yaml"), header + buttons);
        }
    }
}
