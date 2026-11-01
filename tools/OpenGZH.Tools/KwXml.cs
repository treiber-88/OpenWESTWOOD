using System.Globalization;
using System.Xml.Linq;

namespace OpenGZH.Tools;

/// <summary>
/// Kane's Wrath's source data (EA's modding release, content/kaneswrath/sdk/Xml): every definition (GameObject,
/// WeaponTemplate, ArmorTemplate, LocomotorTemplate, LogicCommandSet, LogicCommand, UpgradeTemplate...) by type and id,
/// with "inheritFrom" resolved the way the asset builder did (attributes override; a module or element with an id
/// replaces the parent's of that id, or is added, or with xai:joinAction="Remove" removes it; other elements replace the
/// parent's of that name), "xi:include" fragments expanded and "=$NAME" defines substituted.
/// </summary>
public sealed class KwXml
{
    public static readonly XNamespace Ns = "uri:ea.com:eala:asset";
    static readonly XNamespace Xi = "http://www.w3.org/2001/XInclude";
    static readonly XNamespace Xai = "uri:ea.com:eala:asset:instance";

    readonly string root;
    readonly Dictionary<(string Type, string Id), XElement> raw = new(new KeyComparer());
    readonly Dictionary<(string Type, string Id), XElement> resolved = new(new KeyComparer());
    readonly Dictionary<string, string> defines = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<XElement, string> fileOf = new();

    sealed class KeyComparer : IEqualityComparer<(string Type, string Id)>
    {
        public bool Equals((string Type, string Id) a, (string Type, string Id) b) =>
            string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Type, string Id) k) =>
            HashCode.Combine(k.Type.ToUpperInvariant(), k.Id.ToUpperInvariant());
    }

    /// <summary>Loads every .xml under the folder except maps, the UI and campaign-only data.</summary>
    public KwXml(string xmlRoot)
    {
        root = xmlRoot;
        foreach (string path in Directory.GetFiles(xmlRoot, "*.xml", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(xmlRoot, path).Replace('\\', '/');
            if (rel.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("AptUI/", StringComparison.OrdinalIgnoreCase))
                continue;
            XDocument doc;
            try
            {
                doc = XDocument.Load(path);
            }
            catch
            {
                continue;
            }
            if (doc.Root == null)
                continue;
            foreach (var define in doc.Root.Descendants(Ns + "Define"))
                if ((string?)define.Attribute("name") is { } n)
                    defines[n] = (string?)define.Attribute("value") ?? "";
            foreach (var e in doc.Root.Elements())
            {
                if (e.Name.LocalName is "Tags" or "Includes" or "Defines" || (string?)e.Attribute("id") is not { Length: > 0 } id)
                    continue;
                // (A later file's definition of the same id wins: subfaction folders override the base ones.)
                raw[(e.Name.LocalName, id)] = e;
                fileOf[e] = path;
            }
        }
    }

    public IEnumerable<string> Ids(string type) =>
        raw.Keys.Where(k => k.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).Select(k => k.Id);

    /// <summary>A definition with its inheritance resolved, or null.</summary>
    public XElement? Get(string type, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        if (resolved.TryGetValue((type, id), out var done))
            return done;
        if (!raw.TryGetValue((type, id), out var e))
            return null;
        resolved[(type, id)] = e; // (guards against inheritance loops)
        var result = Expand(new XElement(e), fileOf.GetValueOrDefault(e) ?? root);
        if ((string?)e.Attribute("inheritFrom") is { Length: > 0 } parentId && Get(type, parentId) is { } parent)
            result = Merge(parent, result);
        resolved[(type, id)] = result;
        return result;
    }

    /// <summary>Expands xi:include fragments (paths relative to the file, or DATA:...).</summary>
    XElement Expand(XElement e, string file)
    {
        foreach (var include in e.Descendants(Xi + "include").ToList())
        {
            string href = (string?)include.Attribute("href") ?? "";
            string path = href.StartsWith("DATA:", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, href[5..])
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? root, href));
            XElement? fragment = null;
            try
            {
                if (File.Exists(path))
                    fragment = XDocument.Load(path).Root;
            }
            catch
            {
            }
            if (fragment != null)
                include.ReplaceWith(Expand(new XElement(fragment), path));
            else
                include.Remove();
        }
        return e;
    }

    static readonly HashSet<string> ModuleLists = new(StringComparer.OrdinalIgnoreCase) { "Draws", "Behaviors", "AI", "Body", "ClientBehaviors", "Nuggets" };

    static string JoinAction(XElement e) => ((string?)e.Attribute(Xai + "joinAction") ?? "").ToLowerInvariant();

    static XElement Merge(XElement parent, XElement child)
    {
        var result = new XElement(parent);
        result.Attribute("inheritFrom")?.Remove();
        foreach (var a in child.Attributes())
            if (a.Name.LocalName != "inheritFrom")
                result.SetAttributeValue(a.Name, a.Value);
        var replacedNames = new HashSet<XName>();
        foreach (var c in child.Elements())
        {
            string action = JoinAction(c);
            if (ModuleLists.Contains(c.Name.LocalName) && result.Element(c.Name) is { } list)
            {
                // Modules merge by id.
                foreach (var m in c.Elements())
                {
                    string? id = (string?)m.Attribute("id");
                    var existing = id == null ? null : list.Elements().FirstOrDefault(x => (string?)x.Attribute("id") == id);
                    if (JoinAction(m) == "remove")
                        existing?.Remove();
                    else if (existing != null)
                        existing.ReplaceWith(new XElement(m));
                    else
                        list.Add(new XElement(m));
                }
                continue;
            }
            if ((string?)c.Attribute("id") is { Length: > 0 } cid && result.Elements(c.Name).FirstOrDefault(x => (string?)x.Attribute("id") == cid) is { } same)
            {
                if (action == "remove")
                    same.Remove();
                else
                    same.ReplaceWith(new XElement(c));
                continue;
            }
            if (action == "remove")
            {
                result.Elements(c.Name).Remove();
                continue;
            }
            if (action != "append" && replacedNames.Add(c.Name))
                result.Elements(c.Name).Remove();
            result.Add(new XElement(c));
        }
        return result;
    }

    /// <summary>An attribute of a definition as written (before inheritance), or null.</summary>
    public string? RawAttr(string type, string id, string name) =>
        raw.TryGetValue((type, id), out var e) ? (string?)e.Attribute(name) : null;

    /// <summary>An attribute's value with defines substituted ("" if missing).</summary>
    public string Attr(XElement? e, string name, string fallback = "")
    {
        string? v = (string?)e?.Attribute(name);
        if (v == null)
            return fallback;
        v = v.Trim();
        if (v.StartsWith("=$") && defines.TryGetValue(v[2..], out var d))
            v = d.Trim();
        return v;
    }

    public float Num(XElement? e, string name, float fallback = 0) => ParseNumber(Attr(e, name), fallback);

    /// <summary>"7s", "1.5s", "500ms", "30d", "12%", or a plain number.</summary>
    public static float ParseNumber(string v, float fallback = 0)
    {
        if (v.Length == 0)
            return fallback;
        float scale = 1;
        if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            v = v[..^2];
        else if (v.EndsWith('s') || v.EndsWith('S'))
        {
            v = v[..^1];
            scale = 1000; // seconds → ms
        }
        else if (v.EndsWith('d') || v.EndsWith('%'))
            v = v[..^1];
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f * scale : fallback;
    }

    /// <summary>A duration in milliseconds: "3s" = 3000, "1500ms" or "1500" = 1500.</summary>
    public float Ms(XElement? e, string name, float fallbackMs = 0) => ParseNumber(Attr(e, name), fallbackMs);

    public bool Bool(XElement? e, string name, bool fallback = false)
    {
        string v = Attr(e, name);
        return v.Length == 0 ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    public static IEnumerable<XElement> Children(XElement? e, string name) => e?.Elements(Ns + name) ?? [];

    public static XElement? Child(XElement? e, string name) => e?.Element(Ns + name);

    /// <summary>The modules in a module list (Draws, Behaviors, AI, Body...).</summary>
    public static IEnumerable<XElement> Modules(XElement? obj, string list) => Child(obj, list)?.Elements() ?? [];

    public static IEnumerable<XElement> AllModules(XElement? obj) =>
        ModuleLists.SelectMany(l => Modules(obj, l));
}
