using System.Globalization;
using System.Text;
using System.Xml.Linq;
using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Westwood;
using OpenWestwood.Logic.Core;

namespace OpenWestwood.Tools;

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
public static partial class KwFactionExporter
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
        if (Environment.GetEnvironmentVariable("KW_STRINGS") is { Length: > 0 } find)
        {
            foreach (var (k, v) in strings.Where(kv => kv.Key.Contains(find, StringComparison.OrdinalIgnoreCase)).Take(40))
                Console.WriteLine($"{k} = {v}");
            return 0;
        }
        if (Environment.GetEnvironmentVariable("KW_DEBUG") is { Length: > 0 } debug)
        {
            Console.WriteLine(x.Get("GameObject", debug)?.ToString() ?? "(none)");
            Console.WriteLine($"{strings.Count} strings; e.g. " + string.Join(" | ", strings.Keys.Where(k => k.Contains("Predator", StringComparison.OrdinalIgnoreCase)).Take(5)));
            return 0;
        }
        // Which sounds exist (Kane's Wrath's compiled audio, as OpenWESTWOOD serves it: kwsfx/<AudioFile>.wav or .mp3).
        HashSet<string> audio;
        using (var package = new OpenWestwood.Formats.Sage.SageAudioPackage(OpenWestwood.Formats.Sage.SageAudioPackage.ArchivesIn(Path.Combine(root, "content", "kaneswrath"))))
            audio = new HashSet<string>(package.FilePaths, StringComparer.OrdinalIgnoreCase);
        var e = new Exporter(x, strings, audio);
        e.ExportMusic();
        foreach (var faction in Factions)
            e.ExportFaction(faction);
        e.ExportProps(MapObjectNames(sdk));
        string output = Path.Combine(root, "mods", "zh", "rules", "kw");
        Directory.CreateDirectory(output);
        e.Write(output);
        KwTerrainExporter.Run(root, output);
        Console.WriteLine("Weapons without damage: " + string.Join(" ", e.FailedWeapons));
        Console.WriteLine($"Wrote Kane's Wrath factions to {output} ({e.ObjectCount} objects, {e.WeaponCount} weapons, {e.SoundCount} sounds, {e.MusicCount} music tracks, {e.FxCount} FX lists, {e.ParticleCount} particle systems, {e.UpgradeCount} upgrades, {e.PowerCount} powers, {e.PropCount} map props)");
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
                foreach (var (k, v) in CsfFile.Read(OpenWestwood.Formats.Sage.SageManifest.Unpack(ms.ToArray())))
                    result[k] = v;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
            }
        }
        return result;
    }

    sealed partial class Exporter(KwXml x, Dictionary<string, string> strings, HashSet<string> audio)
    {
        // ---- Sounds ----

        readonly StringBuilder sounds = new("AudioEvents:\n");
        readonly StringBuilder music = new("MusicTracks:\n");
        readonly HashSet<string> doneSounds = new(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> pendingSounds = new();
        readonly List<string> playlist = new();
        public int SoundCount => doneSounds.Count;
        public int MusicCount => playlist.Count;

        /// <summary>The yaml name of a Kane's Wrath sound event (AudioEvent or Multisound), queued for export; "" if there is none.</summary>
        string Snd(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "";
            id = id.Trim();
            if (x.Get("AudioEvent", id) == null && x.Get("Multisound", id) == null)
                return "";
            if (doneSounds.Add(id))
                pendingSounds.Enqueue(id);
            return "KwSnd_" + id;
        }

        /// <summary>An AudioFile as OpenWESTWOOD serves it, or null if it is not in the audio.</summary>
        string? SoundFile(string file)
        {
            foreach (string ext in new[] { ".wav", ".mp3" })
                if (audio.Contains(OpenWestwood.Formats.Sage.SageAudioPackage.Folder + file.Trim() + ext))
                    return "/" + OpenWestwood.Formats.Sage.SageAudioPackage.Folder + file.Trim() + ext;
            return null;
        }

        /// <summary>The fields a sound and a music track share: volume, priority, who hears it, pitch, delay, ranges.</summary>
        void SoundFields(StringBuilder sb, XElement e, bool isMusic)
        {
            sb.Append($"    Volume: {F(x.Num(e, "Volume", 100))}\n");
            if (x.Num(e, "MinVolume") is > 0 and var min)
                sb.Append($"    MinVolume: {F(min)}\n");
            if (x.Num(e, "VolumeShift") is not 0 and var shift)
                sb.Append($"    VolumeShift: {F(shift)}\n");
            string priority = x.Attr(e, "Priority", "NORMAL").ToUpperInvariant();
            sb.Append($"    Priority: {(priority == "HIGHEST" ? "high" : priority.ToLowerInvariant())}\n");
            if (isMusic)
                return;
            if (x.Num(e, "Limit") is > 0 and var limit)
                sb.Append($"    Limit: {F(limit)}\n");
            string type = x.Attr(e, "Type").ToLowerInvariant();
            sb.Append($"    Type: {(type.Length > 0 ? type : "world everyone")}\n");
            var control = x.Attr(e, "Control").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var flags = new List<string>();
            if (!control.Contains("SEQUENTIAL"))
                flags.Add("random");
            if (control.Contains("LOOP"))
                flags.Add("loop");
            if (control.Contains("INTERRUPT"))
                flags.Add("interrupt");
            sb.Append($"    Control: {string.Join(' ', flags)}\n");
            if (KwXml.Child(e, "PitchShift") is { } pitch)
                sb.Append($"    PitchShift: {F(x.Num(pitch, "Low"))} {F(x.Num(pitch, "High"))}\n");
            if (KwXml.Child(e, "Delay") is { } delay)
                sb.Append($"    Delay: {F(x.Num(delay, "Low"))} {F(x.Num(delay, "High"))}\n");
            sb.Append($"    MinRange: {F(x.Num(e, "MinRange", 200))}\n    MaxRange: {F(x.Num(e, "MaxRange", 800))}\n");
        }

        /// <summary>The engine's announcer events and Kane's Wrath's EVA line for each (Geva_ GDI, Neva_ Nod, Aeva_ Scrin).</summary>
        static readonly (string Event, string Kw)[] EvaLines =
        [
            ("LowPower", "LowPower"), ("InsufficientFunds", "InsufficFunds"), ("TdInsufficientFunds", "InsufficFunds"),
            ("BuildingLost", "BuildingLost"), ("BaseUnderAttack", "BaseUndAttack"), ("AllyUnderAttack", "AllyUndAttack"),
            ("BeaconDetected", "BeaconDetecte"), ("UnitLost", "UnitLost"), ("UpgradeComplete", "UpgradeComple"),
            ("UnitReady", "UnitReady"), ("BuildingComplete", "ConComplete"), ("TdConstructionComplete", "ConComplete"),
            ("TdSilosNeeded", "SilosNeeded"), ("TdBuildingCaptured", "BuildingCapt"), ("TdCannotDeployHere", "CantDeploHere"),
            ("GeneralLevelUp", "NewPowersAv"), ("CashStolen", "CreditsStolen"), ("BuildingStolen", "BuildingCapt"),
        ];

        readonly StringBuilder eva = new("SideEvaSounds:\n");

        void ExportEva()
        {
            foreach (var (evt, kw) in EvaLines)
            {
                var sides = new List<string>();
                foreach (var (side, prefix) in new[] { ("KWGDI", "Geva_"), ("KWNod", "Neva_"), ("KWScrin", "Aeva_") })
                    if (Snd(prefix + kw) is { Length: > 0 } line)
                        sides.Add($"    {side}: {line}\n");
                if (sides.Count > 0)
                    eva.Append($"  {evt}:\n").Append(string.Concat(sides));
            }
        }

        void WriteSounds()
        {
            ExportEva();
            while (pendingSounds.TryDequeue(out var id))
            {
                if (x.Get("Multisound", id) is { } multi)
                {
                    var subs = KwXml.Children(multi, "Subsound").Select(s => Snd(s.Value)).Where(s => s.Length > 0).ToList();
                    sounds.Append($"  KwSnd_{id}:\n    AlsoPlay: {string.Join(' ', subs)}\n");
                    if (x.Attr(multi, "Control").Contains("PLAY_ONE", StringComparison.OrdinalIgnoreCase))
                        sounds.Append("    AlsoPlayOne: Yes\n");
                    continue;
                }
                var e = x.Get("AudioEvent", id)!;
                string Files(string element) => string.Join(' ', KwXml.Children(e, element).Select(s => SoundFile(s.Value)).OfType<string>());
                sounds.Append($"  KwSnd_{id}:\n    Sounds: {Files("Sound")}\n");
                if (Files("Attack") is { Length: > 0 } attack)
                    sounds.Append($"    Attack: {attack}\n");
                if (Files("Decay") is { Length: > 0 } decay)
                    sounds.Append($"    Decay: {decay}\n");
                SoundFields(sounds, e, false);
            }
        }

        /// <summary>The skirmish soundtrack: Kane's Wrath's battle music (build-up, exploration and action tracks, stereo).</summary>
        public void ExportMusic()
        {
            foreach (string id in x.Ids("MusicTrack").OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            {
                bool battle = id.StartsWith("TM_CNC3", StringComparison.OrdinalIgnoreCase) || id.StartsWith("SJ_CNC3", StringComparison.OrdinalIgnoreCase);
                if (!battle || id.Contains("CREDITS", StringComparison.OrdinalIgnoreCase) || id.Contains("_moved", StringComparison.OrdinalIgnoreCase))
                    continue;
                var e = x.Get("MusicTrack", id)!;
                string file = KwXml.Child(e, "Filename")?.Value.Trim() ?? "";
                // (5.1 tracks are several interleaved MPEG streams: not converted.)
                if (file.Contains("5point1", StringComparison.OrdinalIgnoreCase) || SoundFile(file) is not { } path)
                    continue;
                music.Append($"  KwMusic_{id}:\n    Filename: {path}\n");
                SoundFields(music, e, true);
                playlist.Add("KwMusic_" + id);
            }
        }

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
            if (playlist.Count > 0)
                factions.Append($"    GameMusic: {string.Join(' ', playlist)}\n");
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
            var allWeapons = new List<(string Ordering, string Weapon, string Status)>();
            bool turret = false, turretPitch = false;
            float turretRate = 180;
            var weaponSetUpdate = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "WeaponSetUpdate");
            foreach (var slot in weaponSetUpdate?.Elements() ?? [])
            {
                if (!slot.Name.LocalName.StartsWith("WeaponSlot"))
                    continue;
                int before = allWeapons.Count;
                foreach (var w in KwXml.Children(slot, "Weapon"))
                {
                    string status = x.Attr(w, "ObjectStatus").ToUpperInvariant();
                    if (x.Attr(w, "Upgrade").Length > 0 || status.Length > 0 && !WeaponStatusFlags.ContainsKey(status))
                        continue;
                    string ordering = x.Attr(w, "Ordering", "PRIMARY_WEAPON").Replace("_WEAPON", "");
                    if (ordering is not ("PRIMARY" or "SECONDARY" or "TERTIARY") || allWeapons.Any(s => s.Ordering == ordering && s.Status == status))
                        continue;
                    if (Weapon(x.Attr(w, "Template"), o) is { } weapon)
                        allWeapons.Add((ordering, weapon, status));
                }
                if (slot.Name.LocalName == "WeaponSlotTurret" && allWeapons.Count > before)
                {
                    turret = true;
                    var settings = KwXml.Child(slot, "TurretSettings");
                    turretRate = x.Num(settings, "TurretTurnRate", 180);
                    turretPitch = x.Bool(settings, "AllowsPitch");
                }
            }
            var (weaponSetYaml, slots) = WeaponSets(o, allWeapons);
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
            string upgradedArmor = UpgradedArmorSet(o);
            string shieldArmor = ShieldArmorSet(o);
            if (upgradedArmor.Length > 0 || shieldArmor.Length > 0)
            {
                sb.Append($"    ArmorSet:\n    - Conditions: None\n      Armor: {armorName}\n{(structure ? "      DamageFX: StructureDamageFXNoShake\n" : "")}");
                if (upgradedArmor.Length > 0)
                    sb.Append($"    - Conditions: PLAYER_UPGRADE\n      Armor: {upgradedArmor}\n");
                if (shieldArmor.Length > 0)
                    sb.Append($"    - Conditions: SHIELDBODY_ENABLED\n      Armor: {shieldArmor}\n");
            }
            else
            {
                sb.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {armorName}\n");
                if (structure)
                    sb.Append("      DamageFX: StructureDamageFXNoShake\n");
            }
            sb.Append(weaponSetYaml);
            if (!structure)
            {
                // Aircraft that land on a pad taxi there (Kane's Wrath's TAXIING set: Zero Hour's helicopter taxi locomotor).
                bool taxis = aircraft && KwXml.Children(o, "LocomotorSet").Any(l => x.Attr(l, "Condition").Equals("TAXIING", StringComparison.OrdinalIgnoreCase));
                sb.Append(taxis ? $"    Locomotor: [SET_NORMAL {Locomotor(o, infantry, aircraft)}, SET_TAXIING BasicHelicopterTaxiLocomotor]\n"
                                : $"    Locomotor: SET_NORMAL {Locomotor(o, infantry, aircraft)}\n");
            }

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
            var research = structure ? UpgradeButtons(o) : [];
            set.AddRange(research);
            var (powerModules, powerButtons) = structure ? Powers(o) : ("", new List<string>());
            set.AddRange(powerButtons);
            if (structure)
                set.AddRange(factory && role != "STRUCT_CONST" ? ["Command_SetRallyPoint", "Command_KwRepair", "Command_Sell"] : ["Command_KwRepair", "Command_Sell"]);
            var (abilityModules, abilityButtons) = structure ? ("", new List<string>()) : UnitAbilities(o, squad);
            powerModules += abilityModules;
            string commandSet = structure || factory || research.Count > 0 || powerButtons.Count > 0 ? CommandSet($"{name}CommandSet", set)
                : abilityButtons.Count > 0 && passengers == 0 ? UnitCommandSet($"{name}CommandSet", abilityButtons)
                : passengers > 0 ? "KWTransportCommandSet" : "GenericCommandSet";
            sb.Append($"    CommandSet: {commandSet}\n");
            AddVoices(sb, o, squad);

            // ---- Modules ----
            sb.Append("    Modules:\n");
            sb.Append(Draw(o, squad));
            sb.Append(Tracers(o));
            // (The last body: an object's own replaces the base object's default.)
            var body = KwXml.Modules(o, "Body").LastOrDefault(b => x.Attr(b, "MaxHealth").Length > 0);
            float hp = x.Num(body, "MaxHealth", structure ? 1000 : 100);
            bool shielded = !structure && body?.Name.LocalName == "ShieldBody";
            sb.Append($"      ModuleTag_02:\n        Module: Body\n        Class: {(structure ? "StructureBody" : shielded ? "ShieldBody" : "ActiveBody")}\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
            if (shielded)
                sb.Append(ShieldSettings(body!));
            // Its death FX: an FXListBehavior's onDeath event, or its slow death's first FX.
            string deathFx = KwXml.AllModules(o).Where(m => m.Name.LocalName == "FXListBehavior")
                .SelectMany(m => KwXml.Children(m, "Event")).Where(e => x.Attr(e, "Index") == "onDeath").Select(e => Fx(x.Attr(e, "FX")))
                .Concat(KwXml.AllModules(o).Where(m => m.Name.LocalName == "SlowDeath").SelectMany(m => KwXml.Children(m, "FX")).Select(e => Fx(x.Attr(e, "List"))))
                .FirstOrDefault(s => s.Length > 0) ?? "";
            if (deathFx.Length > 0 && (structure || infantry))
                sb.Append($"      ModuleTag_12:\n        Module: Behavior\n        Class: FXListDie\n        DeathFX: {deathFx}\n");
            if (structure)
            {
                sb.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                sb.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                sb.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: TdStructureRepair\n        RepairStep: {F(Math.Max(hp / 20, 5))}\n");
                if (KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "StructureUnpackUpdate") is { } unpack)
                    sb.Append($"      ModuleTag_13:\n        Module: Behavior\n        Class: StructureUnpackUpdate\n        UnpackTime: {F(x.Ms(unpack, "UnpackTime", 1500))}\n");
                if (slots.Count > 0)
                {
                    sb.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (turret)
                        sb.Append($"        Turret:\n          TurretTurnRate: {F(turretRate)}\n          ControlledWeaponSlots: PRIMARY\n");
                }
                if (factory || research.Count > 0)
                {
                    sb.Append("      ModuleTag_07:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                    if (role == "STRUCT_CONST" || builds.Any(b => Is(Obj(b)!, "STRUCTURE")))
                        sb.Append("        PlacesStructures: Yes\n");
                    if (quantities.Count > 0)
                        sb.Append("        QuantityModifier:\n" + string.Concat(quantities.Distinct().Select(q => $"        - {q}\n")));
                    if (KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "ParkingPlaceBehavior") is { } parking)
                    {
                        // An air pad: the aircraft it builds park (and rearm) on its pads.
                        sb.Append($"      ModuleTag_08:\n        Module: Behavior\n        Class: ParkingPlaceBehavior\n        NumRows: {F(x.Num(parking, "NumRows", 2))}\n        NumCols: {F(x.Num(parking, "NumCols", 2))}\n");
                        sb.Append($"        HasRunways: No\n        ApproachHeight: {F(x.Num(parking, "ApproachHeight", 50))}\n        HealAmountPerSecond: {F(x.Num(parking, "HealAmount", 0))}\n");
                        sb.Append($"        ParkInHangars: {(x.Bool(parking, "ParkInHangars") ? "Yes" : "No")}\n");
                    }
                    else if (role != "STRUCT_CONST")
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
                    // Its SupplyTruckAIUpdate in Zero Hour's 75-credit boxes: a load is MaxBoxes (200) of a crystal's boxes
                    // (TiberiumCrystal ValuePerBox 7: 1400 credits); a crystal's NumBoxes (50) take HarvestActionTime.
                    var truck = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "SupplyTruckAIUpdate");
                    var crystal = KwXml.AllModules(x.Get("GameObject", "TiberiumCrystal")).FirstOrDefault(m => m.Name.LocalName == "TiberiumCrystal");
                    float perBox = x.Num(crystal, "ValuePerBox", 7), crystalBoxes = x.Num(crystal, "NumBoxes", 50);
                    int maxBoxes = Math.Max(1, (int)MathF.Round(x.Num(truck, "MaxBoxes", 200) * perBox / 75));
                    float boxMs = x.Ms(truck, "HarvestActionTime", 2200) * 75 / (crystalBoxes * perBox);
                    sb.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: {maxBoxes}\n");
                    sb.Append($"        SupplyCenterActionDelay: {F(x.Ms(truck, "SupplyCenterActionDelay", 100))}\n        SupplyWarehouseActionDelay: {F(boxMs)}\n");
                    sb.Append($"        SupplyWarehouseScanDistance: {F(x.Num(truck, "SupplyWarehouseScanDistance", 1500))}\n");
                }
                else if (KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "JetAIUpdate") is { } jet)
                {
                    // Aircraft that land at an air pad to rearm (Orcas, Firehawks, Venoms, Stormriders...).
                    sb.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: JetAIUpdate\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    sb.Append($"        NeedsRunway: {(x.Bool(jet, "NeedsRunway", false) ? "Yes" : "No")}\n        KeepsParkingSpaceWhenAirborne: {(x.Bool(jet, "KeepsParkingSpaceWhenAirborne", true) ? "Yes" : "No")}\n");
                    sb.Append($"        OutOfAmmoDamagePerSecond: {F(x.Num(jet, "OutOfAmmoDamagePerSecond", 0))}%\n        TakeoffPause: 500\n        ReturnToBaseIdleTime: 10000\n        MinHeight: 5\n        ParkingOffset: 0\n");
                    if (turret)
                        sb.Append($"        Turret:\n          TurretTurnRate: {F(turretRate)}\n          ControlledWeaponSlots: PRIMARY\n" + (turretPitch ? "          AllowsPitch: Yes\n" : ""));
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
                    sb.Append($"        SinkDelay: {F(x.Ms(death, "SinkDelay", 3000))}\n        SinkRate: {F(x.Num(death, "SinkRate", 1) * 0.5f)}\n        FX: INITIAL {(deathFx.Length > 0 ? deathFx : "FX_GenericTankDeathExplosion")}\n");
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
            sb.Append(UpgradeModules(o));
            sb.Append(powerModules);
            // Spawn (the Buzzer Hive's buzzers, the carrier's fighters) and, for a spawn, keeping with its master.
            if (role != "STRUCT_REFINERY" && KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "SpawnBehavior") is { } spawn
                && KwXml.Child(spawn, "SpawnTemplate")?.Value.Trim() is { Length: > 0 } slave && Obj(slave) != null)
            {
                sb.Append($"      ModuleTag_S1:\n        Module: Behavior\n        Class: SpawnBehavior\n        SpawnNumber: {F(x.Num(spawn, "SpawnNumberData", 1))}\n        InitialBurst: {F(x.Num(spawn, "InitialBurst", 0))}\n");
                sb.Append($"        SpawnReplaceDelay: {F(x.Ms(spawn, "SpawnReplaceDelayData", 10000))}\n        SpawnTemplateName: {P}{slave}\n        SpawnedRequireSpawner: {(x.Bool(spawn, "SpawnedRequireSpawner") ? "Yes" : "No")}\n        SlavesHaveFreeWill: No\n");
                exported.Add(slave);
                pendingFree.Add((slave, f, exported));
                // (Spawn leave through a production exit: buildings that build nothing, and units, get a plain one.)
                bool hasExit = structure && (factory || research.Count > 0) && role != "STRUCT_CONST";
                if (!hasExit)
                    sb.Append("      ModuleTag_S3:\n        Module: Behavior\n        Class: DefaultProductionExitUpdate\n        UnitCreatePoint: 'X: 0 Y: 0 Z:0.0'\n        NaturalRallyPoint: 'X: 0 Y: 0 Z:0.0'\n");
            }
            if (KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "SlavedUpdate") is { } slaved)
            {
                sb.Append("      ModuleTag_S2:\n        Module: Behavior\n        Class: SlavedUpdate\n");
                foreach (string key in new[] { "GuardMaxRange", "GuardWanderRange", "AttackRange", "AttackWanderRange", "ScoutRange", "ScoutWanderRange", "DistToTargetToGrantRangeBonus" })
                    if (x.Attr(slaved, key).Length > 0)
                        sb.Append($"        {key}: {F(x.Num(slaved, key))}\n");
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

        /// <summary>Voices (AudioArrayVoice), the death sound (SlowDeath's INITIAL sound) and the built sound.</summary>
        void AddVoices(StringBuilder sb, XElement o, XElement? squad)
        {
            var voices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { o, squad })
                foreach (var entry in KwXml.Children(source, "AudioArrayVoice").SelectMany(a => KwXml.Children(a, "AudioEntry"))
                             .Concat(KwXml.Children(source, "AudioArraySound").SelectMany(a => KwXml.Children(a, "AudioEntry"))))
                    voices.TryAdd(x.Attr(entry, "AudioType"), x.Attr(entry, "Sound"));
            foreach (var (kw, key) in new[] { ("voiceSelect", "VoiceSelect"), ("voiceMove", "VoiceMove"), ("voiceAttack", "VoiceAttack"), ("voiceCreated", "VoiceCreate"), ("voiceGuard", "VoiceGuard") })
                if (Snd(voices.GetValueOrDefault(kw)) is { Length: > 0 } s)
                    sb.Append($"    {key}: {s}\n");
            if (Snd(voices.GetValueOrDefault("voiceAttack")) is { Length: > 0 } attack && !voices.ContainsKey("voiceMove"))
                sb.Append($"    VoiceMove: {attack}\n");
            string die = KwXml.AllModules(o).Where(m => m.Name.LocalName == "SlowDeath")
                .SelectMany(m => KwXml.Children(m, "Sound")).Where(s => x.Attr(s, "Type", "INITIAL").Equals("INITIAL", StringComparison.OrdinalIgnoreCase))
                .Select(s => Snd(x.Attr(s, "List"))).FirstOrDefault(s => s.Length > 0) ?? "";
            if (die.Length > 0)
                sb.Append($"    SoundDie: {die}\n");
            if (Snd(voices.GetValueOrDefault("soundOnConstructionComplete") ?? voices.GetValueOrDefault("voiceFullyCreated")) is { Length: > 0 } built && Is(o, "STRUCTURE"))
                sb.Append($"    SoundOnConstructionComplete: {built}\n");
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

        /// <summary>A unit's command set: its ability buttons first, then the generic set's attack move, guard and stop.</summary>
        string UnitCommandSet(string name, List<string> abilities)
        {
            if (!doneSets.Add(name))
                return name;
            commandSets.Append($"  {name}:\n");
            for (int i = 0; i < abilities.Count; i++)
                commandSets.Append($"    {i + 1}: {abilities[i]}\n");
            commandSets.Append("    11: Command_AttackMove\n    13: Command_Guard\n    14: Command_Stop\n");
            return name;
        }

        // ---- Draw ----

        /// <summary>
        /// Its TracerModelDraws (rifles' and machine guns' tracers: streaks flying from the weapon's fire bone to the target
        /// while it fires) as KWTracerDraw modules, each with the object status it needs (upgraded ammo, veterancy).
        /// </summary>
        string Tracers(XElement o)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var t in KwXml.Modules(o, "Draws").Where(d => d.Name.LocalName == "TracerModelDraw"))
            {
                string texture = x.Attr(t, "Texture");
                if (texture.Length == 0)
                    continue;
                string Color(string child, float alpha)
                {
                    var c = KwXml.Child(t, child);
                    int C(string k, float fallback) => (int)Math.Clamp(MathF.Round(x.Num(c, k, fallback) * 255), 0, 255);
                    return $"R:{C("r", 1)} G:{C("g", 1)} B:{C("b", 1)} A:{C("a", alpha)}";
                }
                string slot = x.Attr(t, "WeaponSlotType", "PRIMARY_WEAPON").ToUpperInvariant().Replace("_WEAPON", "");
                sb.Append($"      ModuleTag_Tracer{++n}:\n        Module: Draw\n        Class: KWTracerDraw\n        WeaponSlot: {slot}\n");
                sb.Append($"        Texture: {OpenWestwood.Formats.Sage.SageW3d.TextureFolder}{texture}.dds\n        Additive: {(x.Bool(t, "UseAdditiveBlending") ? "Yes" : "No")}\n");
                sb.Append($"        MinLength: {F(x.Num(t, "MinLength", 10))}\n        MaxLength: {F(x.Num(t, "MaxLength", x.Num(t, "MinLength", 10)))}\n        Width: {F(x.Num(t, "Width", 1))}\n");
                sb.Append($"        MinSpeed: {F(x.Num(t, "MinSpeed", 10))}\n        MaxSpeed: {F(x.Num(t, "MaxSpeed", x.Num(t, "MinSpeed", 10)))}\n        SpreadAngle: {F(x.Num(t, "SpreadAngle"))}\n");
                sb.Append($"        MinTracersPerFrame: {F(x.Num(t, "MinTracersPerFrame"))}\n        MaxTracersPerFrame: {F(x.Num(t, "MaxTracersPerFrame", x.Num(t, "MinTracersPerFrame")))}\n");
                sb.Append($"        FrameLifeTime: {F(x.Num(t, "FrameLifeTime", 30))}\n        HeadColor: {Color("HeadColor", 1)}\n        TailColor: {Color("TailColor", 0)}\n");
                if (Fx(x.Attr(t, "TracerHitFx")) is { Length: > 0 } hitFx)
                    sb.Append($"        TracerHitFx: {hitFx}\n");
                if (KwXml.Child(t, "ObjectStatusValidation") is { } valid)
                {
                    if (x.Attr(valid, "RequiredStatus") is { Length: > 0 } required)
                        sb.Append($"        RequiredStatus: {required}\n");
                    if (x.Attr(valid, "ForbiddenStatus") is { Length: > 0 } forbidden)
                        sb.Append($"        ForbiddenStatus: {forbidden}\n");
                }
            }
            return sb.ToString();
        }

        static readonly System.Text.RegularExpressions.Regex SubObjectScript =new(@"CurDrawable(Show|Hide)SubObject\(""([^""]+)""\)");

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
            // (A building unfolding after it is put down: Zero Hour's UNPACKING, which StructureUnpackUpdate sets.)
            static string Key(string flags) => string.Join(' ', flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.ToUpperInvariant())
                .Select(f => f == "STRUCTURE_UNPACKING" ? "UNPACKING" : f));
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
            // The Forcefield Generator's bubble: a second draw with no model of its own except while the shield is up (USER_1).
            foreach (var d in KwXml.Modules(o, "Draws").Where(d => d != draw))
            {
                var shieldState = KwXml.Children(d, "ModelConditionState").FirstOrDefault(s => Key(x.Attr(s, "ConditionsYes")) == "USER_1");
                if (x.Attr(KwXml.Child(shieldState, "Model"), "Name") is not { Length: > 0 } shieldModel
                    || !KwXml.AllModules(o).Any(m => m.Name.LocalName == "ShieldBody"))
                    continue;
                sb.Append($"      ModuleTag_01S:\n        Module: Draw\n        Class: W3DModelDraw\n        OkToChangeModelColor: Yes\n");
                sb.Append($"        DefaultConditionState:\n          Model: None\n        ConditionState:\n          USER_1:\n            Model: {P}{shieldModel}\n");
                break;
            }
            return sb.ToString();
        }

        string StateBody(XElement? modelState, XElement? animState, string model, bool isDefault, string indent = "          ")
        {
            var sb = new StringBuilder($"{indent}Model: {P}{model}\n");
            if (modelState != null)
            {
                var trails = KwXml.Children(modelState, "ParticleSysBone")
                    .Select(b => (Bone: x.Attr(b, "BoneName", "NONE").ToUpperInvariant(), System: Ps(x.Attr(b, "FXParticleSystemTemplate"))))
                    .Where(b => b.System.Length > 0).ToList();
                if (trails.Count > 0)
                    sb.Append($"{indent}ParticleSysBone: [{string.Join(", ", trails.Select(t => $"{t.Bone} {t.System}"))}]\n");
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
            return ArmorFor(set) ?? (structure ? "StructureArmor" : infantry ? "HumanArmor" : "TankArmor");
        }

        /// <summary>An ArmorSet's ArmorTemplate, exported; null if it has none.</summary>
        string? ArmorFor(XElement? set)
        {
            string id = x.Attr(set, "Armor");
            var template = x.Get("ArmorTemplate", id);
            if (template == null)
                return null;
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
            // A weapon that only triggers a special power (the commando's satchel charge): the power's own weapon, fired when
            // in reach (its WeaponFireSpecialAbilityUpdate's SpecialWeapon).
            if (DamageOf(w) == null && KwXml.Child(w, "Nuggets")?.Elements().FirstOrDefault(n => n.Name.LocalName == "SpecialPowerNugget") is { } trigger
                && KwXml.AllModules(owner).FirstOrDefault(m => m.Name.LocalName == "WeaponFireSpecialAbilityUpdate"
                    && x.Attr(m, "SpecialPowerTemplate") == x.Attr(trigger, "SpecialPowerTemplate")) is { } ability
                && x.Attr(ability, "SpecialWeapon") is { Length: > 0 } special && special != id && Weapon(special, owner) is { } fired)
            {
                doneWeapons[id] = fired;
                return fired;
            }
            // Mind control (the Cultist): its power activates a TemporaryOwnerSpecialPower on what it targets.
            if (DamageOf(w) == null && KwXml.Child(w, "Nuggets")?.Elements().FirstOrDefault(n => n.Name.LocalName == "SpecialPowerNugget") is { } control
                && KwXml.AllModules(owner).FirstOrDefault(m => m.Name.LocalName == "ActivateModuleSpecialPower" && x.Attr(m, "SpecialPowerTemplate").Length > 0
                    // (or the power its "get in range" power leads to: SpecialPower_CultistMindControlGetInRange)
                    && x.Attr(control, "SpecialPowerTemplate").StartsWith(x.Attr(m, "SpecialPowerTemplate"), StringComparison.OrdinalIgnoreCase)) is { } activate
                && KwXml.AllModules(owner).Any(m => m.Name.LocalName == "TemporaryOwnerSpecialPower"))
            {
                string controlName = P + "Weapon_" + id;
                doneWeapons[id] = controlName;
                var power = x.Get("SpecialPowerTemplate", x.Attr(control, "SpecialPowerTemplate"));
                weapons.Append($"  {controlName}:\n    PrimaryDamage: 0\n    PrimaryDamageRadius: 0\n    AttackRange: {F(Math.Max(x.Num(activate, "StartAbilityRange", 250), 50))}\n");
                weapons.Append($"    DamageType: EXPLOSION\n    DeathType: NORMAL\n    DelayBetweenShots: {F(Math.Max(x.Ms(power, "ReloadTime", 30000), 1000))}\n    ClipSize: 0\n    ClipReloadTime: 0\n");
                weapons.Append("    MindControl: Yes\n    RadiusDamageAffects: ENEMIES\n    AntiGround: Yes\n    AntiAirborneVehicle: No\n    AntiAirborneInfantry: No\n");
                return controlName;
            }
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
            else if (KwXml.Child(w, "Nuggets")?.Elements().Any(n => n.Name.LocalName == "SlavesAttackNugget") == true)
            {
                // The Buzzer Hive, the carrier: firing sends its spawn at the target.
                string slaves = P + "Weapon_" + id;
                doneWeapons[id] = slaves;
                string antiMask = x.Attr(w, "AntiMask", "ANTI_GROUND");
                bool g = antiMask.Contains("ANTI_GROUND") || antiMask.Contains("ANTI_STRUCTURE"), a = antiMask.Contains("ANTI_AIRBORNE");
                weapons.Append($"  {slaves}:\n    PrimaryDamage: 0\n    PrimaryDamageRadius: 0\n    AttackRange: {F(Math.Max(x.Num(w, "AttackRange", 300), 10))}\n    DamageType: EXPLOSION\n    DeathType: NORMAL\n");
                weapons.Append($"    DelayBetweenShots: 1000\n    ClipSize: 0\n    ClipReloadTime: 0\n    OrderSlavesToAttack: Yes\n    AntiGround: {(g ? "Yes" : "No")}\n    AntiAirborneVehicle: {(a ? "Yes" : "No")}\n    AntiAirborneInfantry: {(a ? "Yes" : "No")}\n");
                return slaves;
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
            // (Aircraft weapons reload only at their air pad.)
            if (x.Attr(w, "AutoReloadsClip").ToUpperInvariant() is "RETURN_TO_BASE" or "NONE")
                weapons.Append($"    AutoReloadsClip: {x.Attr(w, "AutoReloadsClip").ToUpperInvariant()}\n");
            string anti = x.Attr(w, "AntiMask", "ANTI_GROUND");
            bool ground = anti.Contains("ANTI_GROUND") || anti.Contains("ANTI_STRUCTURE"), air = anti.Contains("ANTI_AIRBORNE");
            weapons.Append($"    AntiGround: {(ground ? "Yes" : "No")}\n    AntiAirborneVehicle: {(air ? "Yes" : "No")}\n    AntiAirborneInfantry: {(air ? "Yes" : "No")}\n");
            // Kane's Wrath's own fire FX and projectile (with its impact FX); Zero Hour's look for the kind of damage where it has none.
            var (fire, hit) = dt switch
            {
                "GUN" or "SNIPER" => ("WeaponFX_GenericMachineGunFire", ""),
                "CANNON" => ("WeaponFX_GenericTankGunNoTracer", "WeaponFX_GenericTankShellDetonation"),
                "GRENADE" or "ROCKET" => ("WeaponFX_GenericTankGunNoTracerSmall", "WeaponFX_JetMissileDetonation"),
                "FLAME" => ("WeaponFX_DragonTankFlameWeapon", ""),
                "LASER" or "MAGNETIC" or "SHOCKWAVE" or "SONIC" => ("WeaponFX_GenericTankGunNoTracerSmall", ""),
                _ => ("WeaponFX_GenericTankGunNoTracerSmall", ""),
            };
            // A laser (ActivateLaserNugget): its beam, as the firing unit's LaserDraw for that laser draws it.
            var laserNugget = KwXml.Child(w, "Nuggets")?.Elements().FirstOrDefault(n => n.Name.LocalName == "ActivateLaserNugget");
            var laserBeam = laserNugget != null ? Laser(owner, (int)x.Num(laserNugget, "LaserId", 1), x.Ms(laserNugget, "Lifetime", 750)) : null;
            if (laserBeam != null)
                (fire, hit) = ("", "");
            if (Fx(x.Attr(w, "FireFX")) is { Length: > 0 } kwFire)
                fire = kwFire;
            if (fire.Length > 0)
                weapons.Append($"    FireFX: {fire}\n");
            if (laserBeam is var (beamName, bone))
                weapons.Append($"    LaserName: {beamName}\n" + (bone.Length > 0 ? $"    LaserBoneName: {bone}\n" : ""));
            var projectileNugget = KwXml.Child(w, "Nuggets")?.Elements().FirstOrDefault(n => n.Name.LocalName == "ProjectileNugget");
            if (projectileNugget != null && Projectile(x.Attr(projectileNugget, "ProjectileTemplate")) is { } projectile)
            {
                weapons.Append($"    ProjectileObject: {projectile.Name}\n    WeaponSpeed: {F(Math.Max(x.Num(w, "WeaponSpeed", 600), 50))}\n");
                if (projectile.ImpactFx.Length > 0)
                    hit = projectile.ImpactFx;
            }
            if (Snd(x.Attr(w, "FireSound")) is { Length: > 0 } fireSound)
                weapons.Append($"    FireSound: {fireSound}\n");
            if (Snd(x.Attr(w, "FireSoundPerClip")) is { Length: > 0 } clipSound)
                weapons.Append($"    FireSoundPerClip: {clipSound}\n");
            if (hit.Length > 0)
                weapons.Append($"    ProjectileDetonationFX: {hit}\n");
            return name;
        }

        readonly StringBuilder lasers = new("Objects:\n");
        readonly Dictionary<(string, int), (string, string)?> doneLasers = new();

        /// <summary>
        /// A unit's laser beam (its LaserDraw for a LaserStateID) as a Zero Hour beam object (W3DLaserDraw, its first
        /// texture running along the beam as Kane's Wrath's Laser.fx draws it, LaserWidth wide, its V tiling and scroll),
        /// lasting the nugget's Lifetime; and the bone it comes from (the unit's weapon fire bone). Null if it has none.
        /// </summary>
        (string Name, string Bone)? Laser(XElement owner, int laserId, float lifetimeMs)
        {
            string ownerId = x.Attr(owner, "id");
            if (doneLasers.TryGetValue((ownerId, laserId), out var done))
                return done;
            var draw = KwXml.Modules(owner, "Draws").FirstOrDefault(d => d.Name.LocalName == "LaserDraw" && (int)x.Num(d, "LaserStateID", 1) == laserId
                && !x.Bool(d, "UseDistortionShader"));
            var texture = draw?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Texture" && x.Attr(e, "Name") == "Texture1")?.Elements().FirstOrDefault()?.Value.Trim();
            (string, string)? result = null;
            if (draw != null && !string.IsNullOrEmpty(texture))
            {
                string name = $"{P}Laser_{ownerId}_{laserId}";
                string bone = owner.Descendants().FirstOrDefault(e => e.Name.LocalName == "WeaponFireFXBone" && x.Attr(e, "BoneName").Length > 0) is { } b
                    ? x.Attr(b, "BoneName").ToUpperInvariant() : "";
                float width = x.Num(draw, "LaserWidth", 20);
                lasers.Append($"  {name}:\n    KindOf: IMMOBILE\n    Modules:\n      ModuleTag_01:\n        Module: Draw\n        Class: W3DLaserDraw\n");
                lasers.Append($"        Texture: {OpenWestwood.Formats.Sage.SageW3d.TextureFolder}{texture}.dds\n        TextureAlongV: Yes\n        NumBeams: 1\n");
                lasers.Append($"        InnerBeamWidth: {F(width)}\n        InnerColor: R:255 G:255 B:255 A:255\n        OuterBeamWidth: {F(width)}\n");
                lasers.Append($"        Tile: Yes\n        TilingScalar: {F(Math.Max(0.01f, x.Num(draw, "Texture1_VTile", 1)))}\n        ScrollRate: {F(x.Num(draw, "Texture1_VScrollRate", 0))}\n        Segments: 1\n");
                lasers.Append("      ModuleTag_02:\n        Module: ClientUpdate\n        Class: LaserUpdate\n");
                lasers.Append($"      ModuleTag_03:\n        Module: Behavior\n        Class: LifetimeUpdate\n        MinLifetime: {F(lifetimeMs)}\n        MaxLifetime: {F(lifetimeMs)}\n");
                result = (name, bone);
            }
            doneLasers[(ownerId, laserId)] = result;
            return result;
        }

        public void Write(string output)
        {
            const string header = "# GENERATED by 'OpenWestwood.Tools export-kw-factions' from Kane's Wrath's source XML (content/kaneswrath/sdk).\n" +
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
            WritePowers(output, header);
            WriteEffects(output, header);
            WriteSounds();
            File.WriteAllText(Path.Combine(output, "sound-effects.yaml"), header + sounds);
            File.WriteAllText(Path.Combine(output, "music.yaml"), header + music);
            File.WriteAllText(Path.Combine(output, "eva.yaml"), header + eva);
            File.WriteAllText(Path.Combine(output, "upgrades.yaml"), header + upgrades);
            WriteProps(output, header);
            File.WriteAllText(Path.Combine(output, "lasers.yaml"), header + lasers);
        }
    }
}
