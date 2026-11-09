using System.Text;
using System.Xml.Linq;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic.Core;

namespace OpenWestwood.Tools;

public static partial class KwFactionExporter
{
    /// <summary>
    /// The objects Kane's Wrath's multiplayer maps place (EA's .map files in content/kaneswrath/sdk/Xml/Maps/official):
    /// every template name used, apart from waypoints and Tiberium fields (KwMaps spreads those into crystals).
    /// </summary>
    static HashSet<string> MapObjectNames(string sdk)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.Combine(sdk, "Maps", "official");
        if (!Directory.Exists(dir))
            return names;
        foreach (string path in Directory.GetFiles(dir, "MAP_MP_*.map", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var o in MapFile.Read(File.ReadAllBytes(path)).Objects)
                    if (!o.TemplateName.StartsWith('*') && !o.TemplateName.StartsWith("TiberiumField", StringComparison.OrdinalIgnoreCase))
                        names.Add(o.TemplateName);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{path}: {e.Message}");
            }
        }
        return names;
    }

    sealed partial class Exporter
    {
        readonly StringBuilder props = new("Objects:\n");
        public int PropCount { get; private set; }

        /// <summary>
        /// props.yaml: the maps' scenery (shrubs, trees, rocks, ruins, fences, civilian buildings...) as KW_&lt;id&gt;
        /// objects: their model (a PropDraw's Model, or the default model state's), kinds, shape, and health and armour
        /// if they have a body (those without one can't be hurt). Objects a faction already exported keep that version.
        /// Their models are in Kane's Wrath's editor stream (WBData.big), which OpenWESTWOOD mounts beside the game's art.
        /// </summary>
        public void ExportProps(IEnumerable<string> ids)
        {
            foreach (string id in ids.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (doneObjects.Contains(id) || x.Get("GameObject", id) is not { } o)
                    continue;
                string model = PropModel(o);
                if (model.Length == 0)
                    continue; // nothing to see (sound emitters, markers)
                doneObjects.Add(id);
                PropCount++;
                bool structure = Is(o, "STRUCTURE");
                var body = KwXml.Modules(o, "Body").LastOrDefault(b => x.Attr(b, "MaxHealth").Length > 0);
                var kinds = KindOf(o).Where(k => Flags.KindOf.IndexOf(k) >= 0 && k is not ("SELECTABLE" or "CAN_ATTACK")).ToList();
                kinds.Add("IMMOBILE");
                string display = x.Attr(o, "EditorName", id);
                props.Append($"  {P}{id}:\n    DisplayName: {Plain(display)}\n    EditorSorting: {x.Attr(o, "EditorSorting", "MISC_MAN_MADE")}\n");
                props.Append($"    KindOf: {string.Join(' ', kinds.Distinct())}\n");
                var shape = KwXml.Child(KwXml.Child(o, "Geometry"), "Shape");
                if (shape != null)
                {
                    float major = x.Num(shape, "MajorRadius", 10), minor = x.Num(shape, "MinorRadius", major);
                    props.Append($"    Geometry: {x.Attr(shape, "Type", "CYLINDER").ToUpperInvariant()}\n    GeometryMajorRadius: {F(major)}\n    GeometryMinorRadius: {F(minor)}\n");
                    props.Append($"    GeometryHeight: {F(x.Num(shape, "Height", 10))}\n    GeometryIsSmall: {(structure ? "No" : "Yes")}\n");
                }
                if (body != null)
                    props.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorSet(o, structure, false)}\n");
                props.Append("    Modules:\n");
                props.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        DefaultConditionState:\n          Model: {P}{model}\n");
                if (body != null)
                {
                    float hp = x.Num(body, "MaxHealth", 100);
                    props.Append($"      ModuleTag_02:\n        Module: Body\n        Class: {(structure ? "StructureBody" : "ActiveBody")}\n        MaxHealth: {F(hp)}\n        InitialHealth: {F(hp)}\n");
                    props.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                }
                else
                    props.Append("      ModuleTag_02:\n        Module: Body\n        Class: InactiveBody\n");
            }
        }

        /// <summary>A prop's model: its PropDraw's, or its draw's default model state's.</summary>
        string PropModel(XElement o)
        {
            foreach (var d in KwXml.Modules(o, "Draws"))
            {
                if (x.Attr(d, "Model") is { Length: > 0 } m)
                    return m;
                var def = KwXml.Children(d, "ModelConditionState").FirstOrDefault(s => x.Attr(s, "ParseCondStateType") == "PARSE_DEFAULT");
                if (x.Attr(KwXml.Child(def, "Model"), "Name") is { Length: > 0 } n)
                    return n;
            }
            return "";
        }

        public void WriteProps(string output, string header) =>
            File.WriteAllText(Path.Combine(output, "props.yaml"), header + props);
    }
}
