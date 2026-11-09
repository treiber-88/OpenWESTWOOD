using System.IO.Compression;
using System.Numerics;
using OpenWestwood.Formats.Images;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Formats.OpenRA;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Multiplayer;

namespace OpenWestwood.Client.Dune2000;

/// <summary>
/// Dune 2000's skirmish maps (content/dune2000/mod/maps/*.oramap) as Zero Hour engine maps, named "d2k:name".
/// Each map is made in memory from its map.bin (a tile and a resource per cell) and map.yaml (start points):
/// <list type="bullet">
/// <item>flat ground the size of the map's Bounds, one cell = <see cref="TdScale.Cell"/> units (32 art pixels);</item>
/// <item>the ground drawn from the tileset (ARRAKIS: BLOX*.R16 frames), spice painted on;</item>
/// <item>cliffs impassable; spice as supply piles, one per 2x2 cells, as rich as the spice under it;</item>
/// <item>the "mpspawn" actors as Player_N_Start.</item>
/// </list>
/// </summary>
public static class D2kMaps
{
    public const string Prefix = "d2k:";

    public static bool IsD2kMap(string map) => map.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    static string MapsFolder => Path.Combine(GameContent.FindRoot(), "content", "dune2000", "mod", "maps");

    sealed record Template(string File, int[]? Frames, int Width, string[] Terrain, bool PickAny);

    static Dictionary<int, Template>? templates;

    /// <summary>Tiles the ground picture couldn't draw (diagnostics).</summary>
    public static readonly HashSet<string> MissingTiles = new();

    /// <summary>The ARRAKIS tileset: each template's tile file, frames and terrain types.</summary>
    static Dictionary<int, Template> Templates()
    {
        if (templates != null)
            return templates;
        templates = new();
        string file = Path.Combine(GameContent.FindRoot(), "content", "dune2000", "mod", "tilesets", "arrakis.yaml");
        var root = MiniYaml.Parse(File.ReadAllText(file)).FirstOrDefault(n => n.Key == "Templates");
        foreach (var t in root?.Children ?? [])
        {
            int id = int.TryParse(t.Get("Id"), out int i) ? i : int.TryParse(t.Key.Split('@').Last(), out int k) ? k : -1;
            if (id < 0)
                continue;
            var frames = t.Get("Frames") is { Length: > 0 } f ? f.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray() : null;
            var size = t.Get("Size").Split(',', StringSplitOptions.TrimEntries);
            int width = size.Length > 0 && int.TryParse(size[0], out int w) ? w : 1;
            var terrain = new List<string>();
            foreach (var tile in t.Child("Tiles")?.Children ?? [])
                if (int.TryParse(tile.Key, out int n))
                {
                    while (terrain.Count <= n)
                        terrain.Add("Clear");
                    terrain[n] = tile.Value;
                }
            templates[id] = new Template(t.Get("Images") is { Length: > 0 } img ? img : "BLOXBASE.R16", frames, width, terrain.ToArray(), t.Get("PickAny") is "True" or "true");
        }
        return templates;
    }

    sealed class OraMap
    {
        public required string Name, Title;
        public required int Width, Height;
        public required int BoundsX, BoundsY, BoundsW, BoundsH;
        public required (ushort Type, byte Index)[,] Tiles;
        public required (byte Type, byte Density)[,] Resources;
        public required List<(int X, int Y)> Spawns;
        /// <summary>Other actors placed on the map that the Zero Hour version keeps (worm spawners, spice bloom points).</summary>
        public required List<(string Type, int X, int Y)> Actors;
    }

    static OraMap? Load(string name)
    {
        string path = Path.Combine(MapsFolder, name + ".oramap");
        if (!File.Exists(path))
            return null;
        using var zip = ZipFile.OpenRead(path);
        string yamlText = new StreamReader(zip.GetEntry("map.yaml")!.Open()).ReadToEnd();
        var yaml = MiniYaml.Parse(yamlText);
        string Get(string key) => yaml.FirstOrDefault(n => n.Key == key)?.Value ?? "";
        using var binStream = new MemoryStream();
        zip.GetEntry("map.bin")!.Open().CopyTo(binStream);
        var bin = binStream.ToArray();
        int width = BitConverter.ToUInt16(bin, 1), height = BitConverter.ToUInt16(bin, 3);
        int tilesAt = BitConverter.ToInt32(bin, 5), resourcesAt = BitConverter.ToInt32(bin, 13);
        var tiles = new (ushort, byte)[width, height];
        var resources = new (byte, byte)[width, height];
        // The cells are stored column by column; an index of 255 means "pick by position" (PickAny templates).
        int p = tilesAt;
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++, p += 3)
            {
                byte index = bin[p + 2];
                tiles[x, y] = (BitConverter.ToUInt16(bin, p), index == 255 ? (byte)(x % 4 + (y % 4) * 4) : index);
            }
        if (resourcesAt > 0)
        {
            p = resourcesAt;
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++, p += 2)
                    resources[x, y] = (bin[p], bin[p + 1]);
        }
        var bounds = Get("Bounds").Split(',', StringSplitOptions.TrimEntries).Select(v => int.TryParse(v, out int n) ? n : 0).ToArray();
        if (bounds.Length != 4)
            bounds = [0, 0, width, height];
        var spawns = new List<(int, int)>();
        var others = new List<(string, int, int)>();
        foreach (var actor in yaml.FirstOrDefault(n => n.Key == "Actors")?.Children ?? [])
        {
            if (actor.Get("Location").Split(',') is not [var sx, var sy])
                continue;
            int ax = int.Parse(sx.Trim()), ay = int.Parse(sy.Trim());
            if (actor.Value == "mpspawn")
                spawns.Add((ax, ay));
            else if (actor.Value is "wormspawner" or "spicebloom.spawnpoint")
                others.Add((actor.Value, ax, ay));
        }
        return new OraMap
        {
            Name = name, Title = Get("Title") is { Length: > 0 } t ? t : name, Width = width, Height = height,
            BoundsX = bounds[0], BoundsY = bounds[1], BoundsW = bounds[2], BoundsH = bounds[3],
            Tiles = tiles, Resources = resources, Spawns = spawns, Actors = others,
        };
    }

    /// <summary>World position of a map cell's middle (the map's rows run south; Zero Hour's Y runs north).</summary>
    static Vector3 CellCenter(OraMap m, float x, float y)
    {
        float c = TdScale.Cell;
        return new Vector3((x - m.BoundsX + 0.5f) * c, (m.BoundsH - (y - m.BoundsY) - 0.5f) * c, 0);
    }

    /// <summary>Adds the Dune 2000 skirmish maps to the map list.</summary>
    public static void AddTo(MapCache cache)
    {
        if (!Directory.Exists(MapsFolder))
            return;
        foreach (string file in Directory.GetFiles(MapsFolder, "*.oramap"))
        {
            OraMap? m;
            try { m = Load(Path.GetFileNameWithoutExtension(file)); }
            catch (Exception e) when (e is InvalidDataException or IOException or IndexOutOfRangeException or FormatException) { continue; }
            if (m == null || m.Spawns.Count < 2)
                continue;
            var meta = new MapMetaData
            {
                FileName = MapCache.Key(Prefix + m.Name),
                NameLookupTag = $"Dune 2000: {m.Title}",
                IsOfficial = true,
                IsMultiplayer = true,
                NumPlayers = Math.Min(m.Spawns.Count, Logic.Multiplayer.GameInfo.MaxSlots),
                ExtentMax = new Vector3(m.BoundsW * TdScale.Cell, m.BoundsH * TdScale.Cell, 0),
            };
            for (int i = 0; i < m.Spawns.Count; i++)
                meta.Waypoints[$"Player_{i + 1}_Start"] = CellCenter(m, m.Spawns[i].X, m.Spawns[i].Y);
            meta.InitialCameraPosition = meta.StartPosition(0) ?? meta.ExtentMax / 2;
            cache.Add(meta);
        }
    }

    /// <summary>The ground picture: the map's tiles with the spice on them.</summary>
    static Image Ground(OraMap m, D2kArt art, int pixels = 32, bool withSpice = false)
    {
        var image = new Image(m.BoundsW * pixels, m.BoundsH * pixels);
        var templates = Templates();
        var spice = new[] { 300, 301, 320, 321 }; // the spice sequences' full-cell frames (BLOXBASE)
        bool Stamp(Formats.Westwood.D2kSpriteFile file, int frameIndex, int cx, int cy)
        {
            if (file.ToImage(frameIndex) is not { } tile)
                return false;
            for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    int sx = x * tile.Width / pixels, sy = y * tile.Height / pixels;
                    int so = (sy * tile.Width + sx) * 4;
                    if (tile.Pixels[so + 3] == 0)
                        continue;
                    int o = (((cy * pixels) + y) * image.Width + cx * pixels + x) * 4;
                    image.Pixels[o] = tile.Pixels[so];
                    image.Pixels[o + 1] = tile.Pixels[so + 1];
                    image.Pixels[o + 2] = tile.Pixels[so + 2];
                    image.Pixels[o + 3] = 255;
                }
            return true;
        }
        var blox = art.SpriteFile("BLOXBASE.R16");
        for (int y = 0; y < m.BoundsH; y++)
            for (int x = 0; x < m.BoundsW; x++)
            {
                var (type, index) = m.Tiles[m.BoundsX + x, m.BoundsY + y];
                if (templates.TryGetValue(type, out var t) && art.SpriteFile(t.File) is { } file)
                {
                    int frame = t.Frames is { Length: > 0 } frames ? frames[(t.PickAny ? index : Math.Min(index, frames.Length - 1)) % frames.Length] : index;
                    if (!Stamp(file, frame, x, y))
                        MissingTiles.Add($"{type}:{index}->frame {frame} of {t.File}");
                }
                else
                    MissingTiles.Add($"{type}:{index} (no template)");
                if (withSpice && m.Resources[m.BoundsX + x, m.BoundsY + y].Type != 0 && blox != null)
                    Stamp(blox, spice[(x + y * 3) % spice.Length], x, y);
            }
        // A tile's see-through pixels (some custom tiles have them): the ground beside them, never an empty hole.
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int o = (y * image.Width + x) * 4;
                if (image.Pixels[o + 3] != 0)
                    continue;
                int from = x > 0 ? o - 4 : y > 0 ? o - image.Width * 4 : -1;
                if (from < 0 || image.Pixels[from + 3] == 0)
                    from = -1;
                image.Pixels[o] = from >= 0 ? image.Pixels[from] : (byte)180;
                image.Pixels[o + 1] = from >= 0 ? image.Pixels[from + 1] : (byte)140;
                image.Pixels[o + 2] = from >= 0 ? image.Pixels[from + 2] : (byte)90;
                image.Pixels[o + 3] = 255;
            }
        return image;
    }

    /// <summary>The map list's picture of a Dune 2000 map (its ground, small).</summary>
    public static Image? Preview(string map)
    {
        if (D2kArt.Get() is not { } art || Load(map[Prefix.Length..]) is not { } m)
            return null;
        return Ground(m, art, pixels: 4, withSpice: true);
    }

    /// <summary>Builds the Zero Hour map for a Dune 2000 map.</summary>
    public static MapFile Convert(string map, MapLighting? lighting)
    {
        var art = D2kArt.Get() ?? throw new InvalidOperationException("The Dune 2000 data is not installed (content/dune2000).");
        var m = Load(map[Prefix.Length..]) ?? throw new FileNotFoundException($"No Dune 2000 map {map}.");
        float c = TdScale.Cell;
        int cellsX = (int)MathF.Ceiling(m.BoundsW * c / MapFile.CellSize), cellsY = (int)MathF.Ceiling(m.BoundsH * c / MapFile.CellSize);
        var file = new MapFile();
        file.HeightMap.Width = cellsX + 1;
        file.HeightMap.Height = cellsY + 1;
        file.HeightMap.BorderSize = 0;
        file.HeightMap.Boundaries.Add((cellsX, cellsY));
        file.HeightMap.Heights = Enumerable.Repeat((byte)16, (cellsX + 1) * (cellsY + 1)).ToArray();
        file.GroundImage = Ground(m, art);
        file.WorldInfo["mapName"] = m.Title;
        for (int t = 0; t < 4; t++)
        {
            for (int i = 0; i < 3; i++)
                if (lighting != null)
                    file.Lighting.ObjectLights[t, i] = lighting.ObjectLights[t, i];
            file.Lighting.TerrainLights[t, 0] = new MapLight { Ambient = Vector3.One, Diffuse = Vector3.Zero, Direction = -Vector3.UnitZ };
            if (lighting == null)
                file.Lighting.ObjectLights[t, 0] = new MapLight { Ambient = new Vector3(0.6f), Diffuse = new Vector3(0.6f), Direction = Vector3.Normalize(new Vector3(-1, 1, -2)) };
        }

        // Cliffs can't be crossed; sand is where the worms go.
        var templates = Templates();
        string TerrainAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= m.BoundsW || y >= m.BoundsH)
                return "Cliff";
            var (type, index) = m.Tiles[m.BoundsX + x, m.BoundsY + y];
            if (!templates.TryGetValue(type, out var t) || t.Terrain.Length == 0)
                return "Sand";
            return t.Terrain[t.PickAny ? 0 : Math.Min(index, t.Terrain.Length - 1)];
        }
        bool Blocked(int x, int y)
        {
            if (x < 0 || y < 0 || x >= m.BoundsW || y >= m.BoundsH)
                return true;
            var (type, index) = m.Tiles[m.BoundsX + x, m.BoundsY + y];
            if (!templates.TryGetValue(type, out var t) || t.Terrain.Length == 0)
                return false;
            string terrain = t.Terrain[t.PickAny ? 0 : Math.Min(index, t.Terrain.Length - 1)];
            return terrain is "Cliff";
        }
        var tiles = new BlendTileData
        {
            TileIndices = new short[(cellsX + 1) * (cellsY + 1)],
            BlendIndices = new short[(cellsX + 1) * (cellsY + 1)],
            ExtraBlendIndices = new short[(cellsX + 1) * (cellsY + 1)],
            CliffInfoIndices = new short[(cellsX + 1) * (cellsY + 1)],
            CliffBytesPerRow = (cellsX + 1 + 7) / 8,
        };
        tiles.CliffStateBits = new byte[tiles.CliffBytesPerRow * (cellsY + 1)];
        for (int y = 0; y < cellsY; y++)
            for (int x = 0; x < cellsX; x++)
            {
                float wx = (x + 0.5f) * MapFile.CellSize, wy = (y + 0.5f) * MapFile.CellSize;
                if (Blocked((int)(wx / c), m.BoundsH - 1 - (int)(wy / c)))
                    tiles.CliffStateBits[y * tiles.CliffBytesPerRow + (x >> 3)] |= (byte)(1 << (x & 7));
            }
        file.BlendTiles = tiles;
        file.SandCells = new bool[cellsX * cellsY];
        for (int y = 0; y < cellsY; y++)
            for (int x = 0; x < cellsX; x++)
            {
                float wx = (x + 0.5f) * MapFile.CellSize, wy = (y + 0.5f) * MapFile.CellSize;
                file.SandCells[y * cellsX + x] = TerrainAt((int)(wx / c), m.BoundsH - 1 - (int)(wy / c)) is "Sand" or "Dune" or "Spice" or "SpiceSand" or "SpiceBlobs";
            }
        foreach (var (type, ax, ay) in m.Actors)
            file.Objects.Add(new MapObject { TemplateName = type == "wormspawner" ? "D2K_WormSpawner" : "D2K_SpiceBloomPoint", Position = CellCenter(m, ax, ay) });

        // Spice: a pile for each 2x2 cells that hold any, as rich as their density (up to 20 a cell).
        for (int by = 0; by < m.BoundsH; by += 2)
            for (int bx = 0; bx < m.BoundsW; bx += 2)
            {
                int total = 0, cells = 0;
                float sx = 0, sy = 0;
                var cellList = new List<string>();
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = bx + dx, y = by + dy;
                        if (x >= m.BoundsW || y >= m.BoundsH)
                            continue;
                        var r = m.Resources[m.BoundsX + x, m.BoundsY + y];
                        if (r.Type == 0)
                            continue;
                        total += Math.Max(r.Density, (byte)1);
                        cells++;
                        // Rich when over half of D2K's 20 a cell (D2kResourceRenderer's thick frame).
                        cellList.Add($"{x},{m.BoundsH - 1 - y},{(r.Density > 10 ? 1 : 0)}");
                        sx += m.BoundsX + x;
                        sy += m.BoundsY + y;
                    }
                if (cells == 0)
                    continue;
                int boxes = Math.Clamp((int)MathF.Round(total / 6f), 1, 12);
                var pile = new MapObject { TemplateName = $"D2K_Spice{boxes:00}", Position = CellCenter(m, sx / cells, sy / cells) };
                pile.Properties["objectMapData"] = string.Join(';', cellList);
                file.Objects.Add(pile);
            }

        for (int i = 0; i < m.Spawns.Count; i++)
            file.Objects.Add(new MapObject
            {
                TemplateName = "*Waypoints/Waypoint",
                Position = CellCenter(m, m.Spawns[i].X, m.Spawns[i].Y),
                Properties = new(StringComparer.OrdinalIgnoreCase) { ["waypointID"] = i + 1, ["waypointName"] = $"Player_{i + 1}_Start" },
            });
        return file;
    }
}
