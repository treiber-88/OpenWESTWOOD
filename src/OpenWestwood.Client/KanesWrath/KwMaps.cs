using OpenWestwood.Core.Installs;
using System.Numerics;
using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic.Multiplayer;

namespace OpenWestwood.Client.KanesWrath;

/// <summary>
/// Kane's Wrath's multiplayer maps ("kw:map_mp_2_black1"), read from its maps.big (content/kaneswrath/Meta, the
/// patches' copies first). They are SAGE maps like Zero Hour's (MapFile reads their newer chunk versions). When one
/// loads:
/// <list type="bullet">
/// <item>a field (TiberiumField, TiberiumFieldBlue...) holds tibFieldStartFieldValue credits, and grows crystals on
/// a grid tibFieldResolution apart within tibFieldRadius; here crystals fill that grid from the middle outwards until
/// they hold the field's credits (a crystal, KW_TiberiumCrystal, is worth 350, a blue one 700);</item>
/// <item>a map's texture names are matched to Kane's Wrath's terrain pictures by mods/zh/rules/kw/terrain-types.yaml;</item>
/// <item>every other object is renamed KW_&lt;name&gt;: the maps' props (props.yaml, models from the editor stream) and
/// any faction objects.</item>
/// </list>
/// Not yet: roads, water areas, regrowing fields.
/// </summary>
public static class KwMaps
{
    public const string Prefix = "kw:";

    /// <summary>Credits in one crystal (TiberiumCrystal NumBoxes 50 x ValuePerBox 7, a blue one's x 14; as boxes in tiberium.yaml).</summary>
    const int CrystalValue = 350, BlueCrystalValue = 700;

    public static bool IsKwMap(string map) => map.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    static List<BigArchive>? archives;
    static Dictionary<string, string>? strings;
    static readonly object Lock = new();

    /// <summary>maps.big and its patches, newest first.</summary>
    static List<BigArchive> Archives
    {
        get
        {
            lock (Lock)
            {
                if (archives != null)
                    return archives;
                archives = new List<BigArchive>();
                // Only from the player's Kane's Wrath install.
                if (!GameInstalls.Has(OriginalGame.KanesWrath) || GameInstalls.KanesWrathFolder is not { } kw)
                    return archives;
                foreach (string big in new[] { "Meta/1.2/patch2.big", "Meta/1.1/patch1.big", "Meta/1.0/maps.big" })
                {
                    if (GameInstalls.FindFile(kw, big) is { } path)
                        try { archives.Add(new BigArchive(path)); }
                        catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); }
                }
                return archives;
            }
        }
    }

    static byte[]? ReadFile(string path)
    {
        foreach (var archive in Archives)
            if (archive.Contains(path))
            {
                using var s = archive.Open(path);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return OpenWestwood.Formats.Sage.SageManifest.Unpack(ms.ToArray());
            }
        return null;
    }

    static string MapPath(string name) => $"data/maps/official/{name}/{name}.map";

    /// <summary>The multiplayer maps' folder names (map_mp_2_black1...).</summary>
    public static IReadOnlyList<string> Names =>
        Archives.SelectMany(a => a.FilePaths).Select(p => p.Replace('\\', '/'))
            .Where(p => p.StartsWith("data/maps/official/map_mp_", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant()).Distinct().Order().ToList();

    /// <summary>The map's name from Kane's Wrath's strings ("MAP:MAP_MP_2_BLACK1" = Tournament Desert Redux).</summary>
    /// <summary>A text of Kane's Wrath's string table (cnc3.csf) by its label, or null.</summary>
    public static string? Text(string label)
    {
        lock (Lock)
            strings ??= LoadStrings();
        return strings.TryGetValue(label, out var text) ? text : null;
    }

    static string Title(string name)
    {
        lock (Lock)
            strings ??= LoadStrings();
        return strings.TryGetValue("MAP:" + name, out var title) ? title : name;
    }

    static Dictionary<string, string> LoadStrings()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GameInstalls.KanesWrathFolder is not { } kw)
            return result;
        foreach (string big in new[] { "Lang-english/1.0/English.big", "Lang-english/1.1/patch1.big", "Lang-english/1.2/patch2.big" })
        {
            if (GameInstalls.FindFile(kw, big) is not { } path)
                continue;
            try
            {
                using var archive = new BigArchive(path);
                if (archive.FilePaths.FirstOrDefault(f => f.EndsWith(".csf", StringComparison.OrdinalIgnoreCase)) is not { } csf)
                    continue;
                using var s = archive.Open(csf);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                foreach (var (k, v) in OpenWestwood.Formats.Westwood.CsfFile.Read(OpenWestwood.Formats.Sage.SageManifest.Unpack(ms.ToArray())))
                    result[k] = v;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{path}: {e.Message}");
            }
        }
        return result;
    }

    static MapFile? Load(string map)
    {
        string name = map.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? map[Prefix.Length..] : map;
        return ReadFile(MapPath(name)) is { } bytes ? MapFile.Read(bytes) : null;
    }

    /// <summary>Adds the Kane's Wrath maps to the map list.</summary>
    public static void AddTo(MapCache cache)
    {
        foreach (string name in Names)
        {
            MapFile file;
            try
            {
                if (Load(name) is not { } loaded)
                    continue;
                file = loaded;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Kane's Wrath map {name}: {e.Message}");
                continue;
            }
            var height = file.HeightMap;
            var (w, h) = height.Boundaries.Count > 0 ? height.Boundaries[0] : (height.Width - 2 * height.BorderSize, height.Height - 2 * height.BorderSize);
            var meta = new MapMetaData
            {
                FileName = MapCache.Key(Prefix + name),
                NameLookupTag = "KW: " + Title(name),
                IsOfficial = true,
                IsMultiplayer = true,
                ExtentMin = Vector3.Zero,
                ExtentMax = new Vector3(w * MapFile.CellSize, h * MapFile.CellSize, 0),
            };
            foreach (var o in file.Objects)
                if (o.Get("waypointName") is { } waypoint && waypoint.StartsWith("Player_", StringComparison.OrdinalIgnoreCase) && waypoint.EndsWith("_Start", StringComparison.OrdinalIgnoreCase))
                    meta.Waypoints[waypoint] = o.Position;
                else if (IsField(o))
                    meta.SupplyPositions.Add(o.Position);
            meta.NumPlayers = Math.Min(meta.Waypoints.Count, GameInfo.MaxSlots);
            meta.InitialCameraPosition = meta.StartPosition(0) ?? meta.ExtentMax / 2;
            if (meta.NumPlayers >= 2)
                cache.Add(meta);
        }
    }

    static bool IsField(MapObject o) => o.TemplateName.StartsWith("TiberiumField", StringComparison.OrdinalIgnoreCase);

    /// <summary>The map list's picture: the map's own (&lt;name&gt;_art.tga beside it).</summary>
    public static Formats.Images.Image? Preview(string map)
    {
        string name = map[Prefix.Length..];
        try
        {
            return ReadFile($"data/maps/official/{name}/{name}_art.tga") is { } tga ? Formats.Images.Image.Load(tga, name + "_art.tga") : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads a Kane's Wrath map, its Tiberium fields spread into crystals.</summary>
    public static MapFile Read(string map)
    {
        var file = Load(map) ?? throw new FileNotFoundException($"No such Kane's Wrath map (or its files aren't installed): {map}.");
        // Kane's Wrath's water is in its own chunks (StandingWaterAreas, RiverAreas, not read yet): its polygon
        // triggers are trigger areas, never Zero Hour water (a misread flag drew a flat plane over the battlefield).
        file.PolygonTriggers.RemoveAll(p => p.IsWater || p.IsRiver);
        // Everything else the map places is Kane's Wrath's (KW_<name>: props.yaml, or a faction's object).
        foreach (var o in file.Objects)
            if (!o.TemplateName.StartsWith('*') && !IsField(o))
                o.TemplateName = "KW_" + o.TemplateName;
        var fields = file.Objects.Where(IsField).ToList();
        foreach (var field in fields)
        {
            file.Objects.Remove(field);
            float radius = Number(field, "tibFieldRadius", 200), spacing = Math.Max(10, Number(field, "tibFieldResolution", 40));
            bool blue = field.TemplateName.Contains("Blue", StringComparison.OrdinalIgnoreCase);
            int crystals = (int)(Number(field, "tibFieldStartFieldValue", 24000) / (blue ? BlueCrystalValue : CrystalValue));
            string crystal = blue ? "KW_TiberiumCrystalBlue" : "KW_TiberiumCrystal";
            int steps = (int)(radius / spacing);
            var points = new List<Vector2>();
            for (int y = -steps; y <= steps; y++)
                for (int x = -steps; x <= steps; x++)
                    if (new Vector2(x, y).Length() * spacing <= radius)
                        points.Add(new Vector2(x, y) * spacing);
            foreach (var p in points.OrderBy(p => p.LengthSquared()).ThenBy(p => MathF.Atan2(p.Y, p.X)).Take(crystals))
            {
                var at = new Vector3(field.Position.X + p.X, field.Position.Y + p.Y, 0);
                file.Objects.Add(new MapObject { TemplateName = crystal, Position = at, Angle = (p.X * 7 + p.Y * 13) % 6.283f });
            }
        }
        return file;
    }

    static float Number(MapObject o, string key, float fallback) =>
        o.Properties.TryGetValue(key, out var v) && float.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
}
