using OpenWestwood.Core.FileSystem;

namespace OpenWestwood.TiberianDawn.Data;

/// <summary>
/// A Tiberian Dawn scenario as stored on disc: SCxnnDV.INI (rules, objects, triggers, teams) and
/// SCxnnDV.BIN (the 64x64 map of template type and icon per cell, MapClass::Read_Binary). Object
/// lines are kept in the original's field order (IOOBJ.CPP and the Read_INI functions).
/// Red Alert's scenarios are read too: one INI, a 128x128 map, its cells in [MapPack] and its overlays in
/// [OverlayPack] (base64 text of LCW blocks, as CELL.CPP / MAP.CPP write them).
/// </summary>
public sealed class TdScenario
{
    public const int MapCellWidth = 64, MapCellHeight = 64;
    public const byte TemplateNone = 255;

    /// <summary>A Red Alert scenario (128x128 cells, 16-bit template numbers; 0xFFFF is no template).</summary>
    public bool IsRedAlert { get; }
    /// <summary>Cells across (and down) the whole map: 64 in TD, 128 in Red Alert.</summary>
    public int MapWidth { get; }

    /// <summary>Whether a cell has no template (it shows the clear ground).</summary>
    public bool IsEmpty(ushort template) => template == 0xFFFF || !IsRedAlert && template == TemplateNone;

    public string Name { get; }
    public TdIni Ini { get; }

    /// <summary>Template type and icon of every cell (row by row, cell = y * 64 + x).</summary>
    public (ushort Template, byte Icon)[] Cells { get; }

    public string Theater { get; }
    /// <summary>The playable part of the 64x64 map ([MAP] X, Y, Width, Height in cells).</summary>
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public sealed record PlacedObject(string Id, string House, string Type, int Strength, int Cell, int Facing,
        string Mission, string Trigger, int SubCell = -1);

    public List<PlacedObject> Structures { get; } = new();
    public List<PlacedObject> Units { get; } = new();
    public List<PlacedObject> Infantry { get; } = new();
    public List<PlacedObject> Aircraft { get; } = new();
    public List<(int Cell, string Type, string Trigger)> Terrain { get; } = new();
    public List<(int Cell, string Type)> Overlay { get; } = new();
    public List<(int Cell, string Type, int Data)> Smudges { get; } = new();
    public Dictionary<int, int> Waypoints { get; } = new();

    /// <param name="mapWidth">
    /// OpenWESTWOOD: a map wider than TD's 64 cells (one made for OpenRA's Tiberian Dawn, converted by TdMaps); its .bin
    /// then holds mapWidth x mapWidth cells.
    /// </param>
    public TdScenario(string name, string iniText, byte[]? bin, IReadOnlyList<string>? overlayNames = null, int mapWidth = 0)
    {
        Name = name;
        Ini = new TdIni(iniText);
        IsRedAlert = Ini.Section("MapPack").Any();
        MapWidth = mapWidth > 0 ? mapWidth : IsRedAlert ? 128 : MapCellWidth;
        Cells = new (ushort, byte)[MapWidth * MapWidth];
        Theater = Ini.Get("MAP", "Theater", "TEMPERATE").ToUpperInvariant();
        X = Ini.GetInt("MAP", "X");
        Y = Ini.GetInt("MAP", "Y");
        Width = Ini.GetInt("MAP", "Width", MapWidth);
        Height = Ini.GetInt("MAP", "Height", MapWidth);

        for (int i = 0; i < Cells.Length; i++)
            Cells[i] = (IsRedAlert ? (ushort)0xFFFF : TemplateNone, 0);
        if (IsRedAlert)
        {
            // [MapPack]: every cell's template (16 bits), then every cell's icon (8 bits).
            var pack = Unpack("MapPack", Cells.Length * 3);
            for (int i = 0; i < Cells.Length; i++)
                Cells[i] = ((ushort)(pack[i * 2] | pack[i * 2 + 1] << 8), pack[Cells.Length * 2 + i]);
            // [OverlayPack]: an overlay number per cell (0xFF: none), named by the overlay table's order.
            var overlays = Unpack("OverlayPack", Cells.Length);
            for (int i = 0; i < Cells.Length; i++)
                if (overlays[i] != 0xFF && overlayNames != null && overlays[i] < overlayNames.Count)
                    Overlay.Add((i, overlayNames[overlays[i]]));
        }
        else if (bin != null)
            for (int i = 0; i < Cells.Length && i * 2 + 1 < bin.Length; i++)
                Cells[i] = (bin[i * 2], bin[i * 2 + 1]);

        foreach (var (id, value) in Ini.Section("STRUCTURES"))
            if (Split(value) is { Length: >= 5 } f)
                Structures.Add(new(id, f[0], f[1], Int(f[2]), Int(f[3]), Int(f[4]), "", f.ElementAtOrDefault(5) ?? "None"));
        foreach (var (id, value) in Ini.Section("UNITS"))
            if (Split(value) is { Length: >= 6 } f)
                Units.Add(new(id, f[0], f[1], Int(f[2]), Int(f[3]), Int(f[4]), f[5], f.ElementAtOrDefault(6) ?? "None"));
        foreach (var (id, value) in Ini.Section("INFANTRY"))
            if (Split(value) is { Length: >= 7 } f)
                Infantry.Add(new(id, f[0], f[1], Int(f[2]), Int(f[3]), Int(f[6]), f[5], f.ElementAtOrDefault(7) ?? "None", Int(f[4])));
        foreach (var (id, value) in Ini.Section("AIRCRAFT"))
            if (Split(value) is { Length: >= 6 } f)
                Aircraft.Add(new(id, f[0], f[1], Int(f[2]), Int(f[3]), Int(f[4]), f[5], f.ElementAtOrDefault(6) ?? "None"));
        foreach (var (cell, value) in Ini.Section("TERRAIN"))
            if (int.TryParse(cell, out int c) && Split(value) is { Length: >= 1 } f)
                Terrain.Add((c, f[0], f.ElementAtOrDefault(1) ?? "None"));
        foreach (var (cell, value) in Ini.Section("OVERLAY"))
            if (int.TryParse(cell, out int c))
                Overlay.Add((c, value.Trim()));
        foreach (var (_, value) in Ini.Section("SMUDGE"))
            if (Split(value) is { Length: >= 2 } f)
                Smudges.Add((Int(f[1]), f[0], f.Length > 2 ? Int(f[2]) : 0));
        foreach (var (number, value) in Ini.Section("Waypoints"))
            if (int.TryParse(number, out int n) && int.TryParse(value, out int cell) && cell >= 0)
                Waypoints[n] = cell;
    }

    static string[] Split(string value) => value.Split(',').Select(v => v.Trim()).ToArray();

    /// <summary>
    /// A packed section: its numbered lines joined and base64-decoded, then blocks of (uint16 compressed size,
    /// uint16 size) and LCW data (LCWStraw), unpacked into <paramref name="size"/> bytes.
    /// </summary>
    byte[] Unpack(string section, int size)
    {
        var result = new byte[size];
        string text = string.Concat(Ini.Section(section).OrderBy(e => int.TryParse(e.Key, out int n) ? n : 0).Select(e => e.Value.Trim()));
        if (text.Length == 0)
            return result;
        byte[] data = System.Convert.FromBase64String(text);
        int pos = 0, outPos = 0;
        while (pos + 4 <= data.Length && outPos < size)
        {
            int compressed = data[pos] | data[pos + 1] << 8, length = data[pos + 2] | data[pos + 3] << 8;
            pos += 4;
            if (compressed == 0 || pos + compressed > data.Length)
                break;
            var block = new byte[length];
            Formats.Westwood.WestwoodCompression.LcwUncompress(data.AsSpan(pos, compressed), block);
            Array.Copy(block, 0, result, outPos, Math.Min(length, size - outPos));
            outPos += length;
            pos += compressed;
        }
        return result;
    }

    static int Int(string text) => int.TryParse(text, out int v) ? v : 0;

    /// <summary>Loads a scenario by its root name (e.g. "SCG01EA"); null if its .INI is missing.</summary>
    public static TdScenario? Load(VirtualFileSystem files, string root, IReadOnlyList<string>? overlayNames = null)
    {
        string ini = root + ".INI", bin = root + ".BIN";
        if (!files.Exists(ini))
            return null;
        return new TdScenario(root.ToUpperInvariant(), System.Text.Encoding.Latin1.GetString(files.ReadAllBytes(ini)),
            files.Exists(bin) ? files.ReadAllBytes(bin) : null, overlayNames);
    }

    /// <summary>Every scenario the file system holds (an .INI named SC + side + number + direction + variant).</summary>
    public static List<string> List(VirtualFileSystem files) =>
        files.List("", ".INI").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, "^SC[GBJM][0-9]{2}[EW][A-D]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(n => n.ToUpperInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>The theater's file extension and palette (TheaterTypeClass: TEMPERATE .TEM, DESERT .DES, WINTER .WIN).</summary>
    public static (string Extension, string Palette) TheaterFiles(string theater) => theater switch
    {
        "DESERT" => (".DES", "DESERT.PAL"),
        "WINTER" => (".WIN", "WINTER.PAL"),
        // Red Alert's theaters.
        "SNOW" => (".SNO", "SNOW.PAL"),
        "INTERIOR" => (".INT", "INTERIOR.PAL"),
        _ => (".TEM", "TEMPERAT.PAL"),
    };
}
