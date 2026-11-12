namespace OpenWestwood.Formats.Westwood;

/// <summary>An INI made of several files (later ones add to and override earlier ones); ";" comments.</summary>
public sealed class Ra2Ini
{
    readonly Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> order = new(StringComparer.OrdinalIgnoreCase);
    // Every value a section was given, in the order read (across files): a type list's entries by name.
    readonly Dictionary<string, List<string>> listed = new(StringComparer.OrdinalIgnoreCase);
    // (The same values as a set, to skip repeats quickly: searching the list made a map's big data sections slow.)
    readonly Dictionary<string, HashSet<string>> listedSet = new(StringComparer.OrdinalIgnoreCase);

    public void Read(string text)
    {
        Dictionary<string, string>? current = null;
        List<string>? keys = null, values = null;
        HashSet<string>? valueSet = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split(';')[0].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.Contains(']'))
            {
                string name = line[1..line.IndexOf(']')].Trim();
                if (!listed.TryGetValue(name, out values))
                    listed[name] = values = new();
                if (!listedSet.TryGetValue(name, out valueSet))
                    listedSet[name] = valueSet = new(StringComparer.OrdinalIgnoreCase);
                if (!sections.TryGetValue(name, out current))
                {
                    sections[name] = current = new(StringComparer.OrdinalIgnoreCase);
                    order[name] = keys = new();
                }
                else
                    keys = order[name];
                continue;
            }
            int eq = line.IndexOf('=');
            if (current != null && eq > 0)
            {
                string key = line[..eq].Trim();
                if (!current.ContainsKey(key))
                    keys!.Add(key);
                string value = line[(eq + 1)..].Trim();
                current[key] = value;
                if (value.Length > 0 && values != null && values.Count < 4096 && valueSet!.Add(value))
                    values.Add(value);
            }
        }
    }

    public bool Has(string section) => sections.ContainsKey(section);
    public IEnumerable<string> Sections => sections.Keys;
    public IReadOnlyDictionary<string, string> Section(string name) => sections.TryGetValue(name, out var s) ? s : new Dictionary<string, string>();
    /// <summary>A list section's values in order (e.g. [VehicleTypes] 0=MTNK, 1=HTNK...).</summary>
    public IEnumerable<string> Values(string name) => order.TryGetValue(name, out var keys) ? keys.Select(k => sections[name][k]) : [];
    /// <summary>
    /// A type list as the game builds it from several files: each file's entries added by name, so a later file's
    /// list (Firestorm's FIRESTRM.INI, numbered afresh) adds its new types after the earlier ones instead of
    /// overwriting them.
    /// </summary>
    public IEnumerable<string> TypeList(string name) => listed.TryGetValue(name, out var values) ? values : [];
    public string Get(string section, string key, string fallback = "") =>
        sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;
    public float Num(string section, string key, float fallback = 0) =>
        float.TryParse(Get(section, key).TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
    public bool Bool(string section, string key, bool fallback = false) =>
        Get(section, key) is { Length: > 0 } v ? v[0] is 'y' or 'Y' or 't' or 'T' or '1' : fallback;
    public string[] List(string section, string key) =>
        Get(section, key).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
