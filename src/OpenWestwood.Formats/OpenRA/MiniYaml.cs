namespace OpenWestwood.Formats.OpenRA;

/// <summary>
/// The rule files of the Dune 2000 game data (OpenRA's "MiniYaml"): tab-indented "Key: Value" lines,
/// '#' comments. Top-level entries are combined across files (later ones override), and
/// <see cref="Resolve"/> applies inheritance: "Inherits: ^Base" (or "Inherits@x:") merges the parent's
/// nodes in first, "-Key:" removes an inherited node, and entries starting with '^' are abstract.
/// </summary>
public sealed class MiniYamlNode
{
    public string Key { get; set; }
    public string Value { get; set; }
    public List<MiniYamlNode> Children { get; } = new();

    public MiniYamlNode(string key, string value = "")
    {
        Key = key;
        Value = value;
    }

    public MiniYamlNode? Child(string key) => Children.LastOrDefault(c => c.Key == key);

    /// <summary>A child's value ("" if missing).</summary>
    public string Get(string key) => Child(key)?.Value ?? "";

    /// <summary>Children whose key is <paramref name="trait"/> or "trait@name".</summary>
    public IEnumerable<MiniYamlNode> All(string trait) =>
        Children.Where(c => c.Key == trait || c.Key.StartsWith(trait + "@", StringComparison.Ordinal));

    public MiniYamlNode Clone()
    {
        var copy = new MiniYamlNode(Key, Value);
        foreach (var child in Children)
            copy.Children.Add(child.Clone());
        return copy;
    }

    public override string ToString() => $"{Key}: {Value} ({Children.Count})";
}

public static class MiniYaml
{
    /// <summary>Parses one file into its top-level nodes.</summary>
    public static List<MiniYamlNode> Parse(string text)
    {
        var roots = new List<MiniYamlNode>();
        var stack = new List<(int Depth, MiniYamlNode Node)>();
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            if (line.Trim().Length == 0)
                continue;
            int depth = 0;
            int i = 0;
            while (i < line.Length && (line[i] == '\t' || line[i] == ' '))
            {
                // A tab is one level; four spaces count as one too.
                if (line[i] == '\t')
                {
                    depth++;
                    i++;
                }
                else
                {
                    int spaces = 0;
                    while (i < line.Length && line[i] == ' ' && spaces < 4)
                    {
                        spaces++;
                        i++;
                    }
                    depth++;
                }
            }
            string content = line[i..].TrimEnd();
            int colon = content.IndexOf(':');
            string key = colon >= 0 ? content[..colon].Trim() : content.Trim();
            string value = colon >= 0 ? content[(colon + 1)..].Trim() : "";
            var node = new MiniYamlNode(key, value);
            while (stack.Count > 0 && stack[^1].Depth >= depth)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0)
                roots.Add(node);
            else
                stack[^1].Node.Children.Add(node);
            stack.Add((depth, node));
        }
        return roots;
    }

    /// <summary>Combines files: a top-level key seen again is merged into the first.</summary>
    public static Dictionary<string, MiniYamlNode> Combine(IEnumerable<List<MiniYamlNode>> files)
    {
        var result = new Dictionary<string, MiniYamlNode>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var node in file)
            {
                if (result.TryGetValue(node.Key, out var existing))
                    Merge(existing, node);
                else
                    result[node.Key] = node.Clone();
            }
        return result;
    }

    /// <summary>Merges <paramref name="over"/> into <paramref name="into"/>: its value (if any) and its children, recursively.</summary>
    public static void Merge(MiniYamlNode into, MiniYamlNode over)
    {
        if (over.Value.Length > 0)
            into.Value = over.Value;
        foreach (var child in over.Children)
        {
            if (child.Key.StartsWith('-'))
            {
                into.Children.RemoveAll(c => c.Key == child.Key[1..]);
                continue;
            }
            var existing = into.Children.LastOrDefault(c => c.Key == child.Key);
            if (existing == null)
                into.Children.Add(child.Clone());
            else
                Merge(existing, child);
        }
    }

    /// <summary>Every entry with its inheritance applied (abstract '^' entries included, for lookups).</summary>
    public static Dictionary<string, MiniYamlNode> Resolve(Dictionary<string, MiniYamlNode> entries)
    {
        var resolved = new Dictionary<string, MiniYamlNode>(StringComparer.Ordinal);
        MiniYamlNode ResolveOne(string key, HashSet<string> visiting)
        {
            if (resolved.TryGetValue(key, out var done))
                return done;
            var source = entries[key];
            var result = new MiniYamlNode(source.Key, source.Value);
            if (visiting.Add(key))
            {
                foreach (var inherit in source.Children.Where(c => c.Key == "Inherits" || c.Key.StartsWith("Inherits@", StringComparison.Ordinal)))
                    if (entries.ContainsKey(inherit.Value))
                        foreach (var child in ResolveOne(inherit.Value, visiting).Children)
                            Merge(result, new MiniYamlNode(result.Key) { Children = { child } });
                visiting.Remove(key);
            }
            var own = new MiniYamlNode(source.Key, source.Value);
            foreach (var child in source.Children.Where(c => c.Key != "Inherits" && !c.Key.StartsWith("Inherits@", StringComparison.Ordinal)))
                own.Children.Add(child);
            Merge(result, own);
            resolved[key] = result;
            return result;
        }
        foreach (string key in entries.Keys)
            ResolveOne(key, new HashSet<string>());
        return resolved;
    }

    /// <summary>A distance ("5c512" = 5 cells and 512/1024 of one, or plain 1024ths) in cells.</summary>
    public static float Cells(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return 0;
        int c = value.IndexOf('c');
        if (c < 0)
            return int.TryParse(value, out int raw) ? raw / 1024f : 0;
        float cells = int.TryParse(value[..c], out int whole) ? whole : 0;
        if (c + 1 < value.Length && int.TryParse(value[(c + 1)..], out int part))
            cells += (cells < 0 ? -part : part) / 1024f;
        return cells;
    }
}
