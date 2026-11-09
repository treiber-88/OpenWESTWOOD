using System.Numerics;
using System.Text;
using OpenWestwood.Formats.Images;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Formats.Westwood;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Multiplayer;

namespace OpenWestwood.Client.RedAlert2;

/// <summary>
/// Red Alert 2 / Yuri's Revenge multiplayer maps (multi.mix, multimd.mix) as Zero Hour engine maps, named "ra2:NAME.MAP",
/// and Tiberian Sun / Firestorm's (MULTI.MIX), "ts:NAME.MAP" (the same map format with 48x24 cells):
/// <list type="bullet">
/// <item>the ground is RA2's own picture of the map (its isometric tiles, from the theater's .TEM/.SNO/... templates in
/// its iso palette) with the height doubled, so each diamond cell is a square seen from above: a screen pixel is
/// 0.85 world units across and 1.7 down, and the image keeps half the screen's columns (1.7 units a pixel);</item>
/// <item>the cells' levels are the ground's height; water tiles are water (lowered, under one water area); rock and
/// cliff tiles can't be crossed;</item>
/// <item>ore and gems are supply piles (RA2_Ore01-12, RA2_Gem01-12), trees and the neutral buildings (RA2CIV_) objects;
/// waypoints 0-7 are the start positions.</item>
/// </list>
/// </summary>
public static class Ra2Maps
{
    /// <summary>A game whose maps these are: its name prefix, its objects' prefix (RA2_, TS_), cell size and art.</summary>
    sealed record IsoGame(string Prefix, string Names, string Title, int TileW, int TileH, Func<Ra2Art?> Art, string MapPackages)
    {
        /// <summary>World units a screen pixel across (a cell is 36 units: 60 pixels in RA2, 48 in TS) and down (twice).</summary>
        public float UnitsPerScreenX => 0.85f * 60 / TileW;
        public float UnitsPerScreenY => 2 * UnitsPerScreenX;
    }

    static readonly IsoGame Ra2 = new("ra2:", "RA2", "Red Alert 2", 60, 30, Ra2Art.Get, "multi");
    static readonly IsoGame Ts = new("ts:", "TS", "Tiberian Sun", 48, 24, Ra2Art.GetTiberianSun, "MULTI");

    static IsoGame GameOf(string map) => map.StartsWith(Ts.Prefix, StringComparison.OrdinalIgnoreCase) ? Ts : Ra2;

    public const string Prefix = "ra2:";
    const float PixelsPerScreenX = 0.5f;
    const int HeightPerLevel = 15, BaseHeight = 16;

    public static bool IsRa2Map(string map) => map.StartsWith(Ra2.Prefix, StringComparison.OrdinalIgnoreCase) || map.StartsWith(Ts.Prefix, StringComparison.OrdinalIgnoreCase);

    static readonly Dictionary<string, IsoMap?> loaded = new(StringComparer.OrdinalIgnoreCase);

    static IsoMap? Load(IsoGame game, string name)
    {
        if (loaded.TryGetValue(game.Prefix + name, out var map))
            return map;
        try
        {
            map = game.Art() is { } art && art.Content.Files.Exists(name) ? new IsoMap(Encoding.Latin1.GetString(art.Content.Files.ReadAllBytes(name))) : null;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or FormatException)
        {
            map = null;
        }
        return loaded[game.Prefix + name] = map;
    }

    static string Title(IsoMap m, string name) => m.Ini.Get("Basic", "Name") is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(name);

    static List<(int X, int Y)> Starts(IsoMap m) =>
        Enumerable.Range(0, 8).Where(m.Waypoints.ContainsKey).Select(i => m.Waypoints[i]).ToList();

    /// <summary>
    /// The multiplayer maps in the game's multi archives (Tiberian Sun's mostly have no known names: any entry that is a
    /// map's INI, by its [IsoMapPack5]).
    /// </summary>
    static IEnumerable<string> MapNames(IsoGame game)
    {
        if (game.Art() is not { } art)
            yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in art.Content.Files.Packages)
            if (package.Name.StartsWith(game.MapPackages, StringComparison.Ordinal))
                foreach (string file in package.FilePaths)
                {
                    if (!seen.Add(file))
                        continue;
                    if (file.EndsWith(".MAP", StringComparison.OrdinalIgnoreCase))
                        yield return file;
                    else if (file.EndsWith(".unknown", StringComparison.OrdinalIgnoreCase)
                        && Encoding.Latin1.GetString(art.Content.Files.ReadAllBytes(file)).Contains("[IsoMapPack5]"))
                        yield return file;
                }
    }

    // ---- Geometry: RA2's cells (rx, ry) and screen; the world is the screen with its height doubled ----

    sealed class Layout(IsoMap m, IsoGame game)
    {
        public readonly IsoGame Game = game;
        public readonly int W = m.Width, TileW = game.TileW, TileH = game.TileH;
        public readonly float Left = m.Local.X * game.TileW, Top = m.Local.Y * game.TileH;
        public readonly int ImageW = Math.Max(1, (int)(m.Local.W * game.TileW * PixelsPerScreenX)), ImageH = Math.Max(1, m.Local.H * game.TileH);
        public float WorldW => ImageW / PixelsPerScreenX * Game.UnitsPerScreenX;
        public float WorldH => ImageH * Game.UnitsPerScreenY;

        /// <summary>The screen point of an iso position (a cell's top corner at whole numbers).</summary>
        public Vector2 Screen(float a, float b) => new(TileW / 2f * (a - b + W - 1) + TileW / 2f, TileH / 2f * (a + b - W - 1));

        public Vector3 World(float a, float b)
        {
            var s = Screen(a, b);
            return new Vector3((s.X - Left) * Game.UnitsPerScreenX, WorldH - (s.Y - Top) * Game.UnitsPerScreenY, 0);
        }

        /// <summary>The cell under a world point.</summary>
        public (int X, int Y) Cell(float wx, float wy)
        {
            float sx = Left + wx / Game.UnitsPerScreenX, sy = Top + (WorldH - wy) / Game.UnitsPerScreenY;
            // Inverse of Screen: a - b = (sx - TileW/2) / (TileW/2) - W + 1, a + b = sy / (TileH/2) + W + 1.
            float d = (sx - TileW / 2f) / (TileW / 2f) - W + 1, s = sy / (TileH / 2f) + W + 1;
            return ((int)MathF.Floor((s + d) / 2), (int)MathF.Floor((s - d) / 2));
        }
    }

    sealed class TileSet
    {
        public readonly List<(string File, int Index, string SetName)> Tiles = new();
        public readonly Dictionary<string, IsoTemplate?> Templates = new(StringComparer.OrdinalIgnoreCase);
    }

    static TileSet ReadTileSet(Ra2Art art, Ra2Art.TheaterInfo theater)
    {
        var ini = new Ra2Ini();
        string file = art.Content.Files.Exists(theater.Ini) ? theater.Ini : theater.FallbackIni;
        if (art.Content.Files.Exists(file))
            ini.Read(Encoding.Latin1.GetString(art.Content.Files.ReadAllBytes(file)));
        var set = new TileSet();
        for (int i = 0; ini.Has($"TileSet{i:0000}"); i++)
        {
            string section = $"TileSet{i:0000}";
            string name = ini.Get(section, "FileName"), setName = ini.Get(section, "SetName");
            int count = (int)ini.Num(section, "TilesInSet");
            for (int k = 1; k <= count; k++)
                set.Tiles.Add(($"{name}{k:00}.{theater.Extension}", k, setName));
        }
        return set;
    }

    static IsoTemplate.Tile? TileOf(Ra2Art art, TileSet set, int tile, int subTile)
    {
        if (tile < 0 || tile >= set.Tiles.Count)
            tile = 0;
        string file = set.Tiles[tile].File;
        if (!set.Templates.TryGetValue(file, out var template))
        {
            try { template = art.Content.Files.Exists(file) ? new IsoTemplate(art.Content.Files.ReadAllBytes(file)) : null; }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IndexOutOfRangeException) { template = null; }
            set.Templates[file] = template;
        }
        return template != null && subTile < template.Tiles.Length ? template.Tiles[subTile] : null;
    }

    /// <summary>TMP land types: 9 water; 7, 8 and 15 rock (cliffs); the rest can be crossed.</summary>
    static bool IsWater(IsoTemplate.Tile? t) => t?.TerrainType == 9;
    static bool IsRock(IsoTemplate.Tile? t) => t?.TerrainType is 7 or 8 or 15;

    // ---- The ground picture ----

    static Image Ground(IsoMap m, Ra2Art art, TileSet set, Layout layout, float scale = 1)
    {
        var theater = Ra2Art.TheaterOf(m.Theater);
        var palette = art.Palette(theater.IsoPalette) ?? art.UnitPalette;
        int w = Math.Max(1, (int)(layout.ImageW * scale)), h = Math.Max(1, (int)(layout.ImageH * scale));
        var image = new Image(w, h);
        void Plot(float sx, float sy, byte index)
        {
            int x = (int)((sx - layout.Left) * PixelsPerScreenX * scale), y = (int)((sy - layout.Top) * scale);
            if ((uint)x >= (uint)w || (uint)y >= (uint)h)
                return;
            var (r, g, b, _) = palette.Color(index, zeroIsClear: false);
            int o = (y * w + x) * 4;
            image.Pixels[o] = r;
            image.Pixels[o + 1] = g;
            image.Pixels[o + 2] = b;
            image.Pixels[o + 3] = 255;
        }
        // Under everything, the theater's plain ground: cliff and ramp tiles leave parts of their cell empty that RA2's
        // raised drawing covers with their neighbours.
        if (TileOf(art, set, 0, 0) is { } clear)
            foreach (var cell in m.Cells)
            {
                var corner = layout.Screen(cell.X, cell.Y) - new Vector2(layout.TileW / 2f, 0);
                for (int py = 0; py < layout.TileH; py++)
                    for (int px = 0; px < layout.TileW; px++)
                        if (py * layout.TileW + px < clear.Pixels.Length && clear.Pixels[py * layout.TileW + px] is var c && c != 0)
                            Plot(corner.X + px, corner.Y + py, c);
            }
        // Back to front, so what stands up from a tile (cliff faces) covers the ones behind it.
        foreach (var cell in m.Cells.OrderBy(c => c.X + c.Y))
        {
            if (TileOf(art, set, cell.Tile == 0xFFFF ? 0 : cell.Tile, cell.SubTile) is not { } tile)
                continue;
            var top = layout.Screen(cell.X, cell.Y) - new Vector2(layout.TileW / 2f, 0);
            int width = tile.Pixels.Length / layout.TileH;
            for (int py = 0; py < layout.TileH; py++)
                for (int px = 0; px < width; px++)
                    if (tile.Pixels[py * width + px] is var c && c != 0)
                        Plot(top.X + px, top.Y + py, c);
            if (tile.Extra is { } extra)
            {
                float ex = top.X + tile.ExtraX - tile.X, ey = top.Y + tile.ExtraY - tile.Y;
                for (int py = 0; py < tile.ExtraHeight; py++)
                    for (int px = 0; px < tile.ExtraWidth; px++)
                        if (extra[py * tile.ExtraWidth + px] is var c && c != 0)
                            Plot(ex + px, ey + py, c);
            }
        }
        // Overlays that are part of the ground (bridges, rails, tracks): drawn on it.
        var overlayTypes = (art.IsTiberianSun ? art.Rules.TypeList("OverlayTypes") : art.Rules.Values("OverlayTypes")).ToList();
        for (int i = 0; i < m.Overlay.Length; i++)
        {
            int type = m.Overlay[i];
            if (type == 0xFF || type >= overlayTypes.Count || IsResourceOverlay(overlayTypes[type]) || IsWallOverlay(art, overlayTypes[type]))
                continue;
            if (art.TheaterShape(overlayTypes[type]) is not { } shape)
                continue;
            int frame = Math.Min(m.OverlayData[i], shape.FrameCount - 1);
            if (art.Frame(shape, frame, art.TheaterPalette, null) is not { } picture)
                continue;
            int rx = i % 512, ry = i / 512;
            var center = layout.Screen(rx + 0.5f, ry + 0.5f);
            float x0 = center.X - picture.Width / 2f, y0 = center.Y - picture.Height / 2f;
            for (int py = 0; py < picture.Height; py++)
                for (int px = 0; px < picture.Width; px++)
                {
                    int so = (py * picture.Width + px) * 4;
                    if (picture.Pixels[so + 3] == 0)
                        continue;
                    int x = (int)((x0 + px - layout.Left) * PixelsPerScreenX * scale), y = (int)((y0 + py - layout.Top) * scale);
                    if ((uint)x >= (uint)w || (uint)y >= (uint)h)
                        continue;
                    int o = (y * w + x) * 4;
                    image.Pixels[o] = picture.Pixels[so];
                    image.Pixels[o + 1] = picture.Pixels[so + 1];
                    image.Pixels[o + 2] = picture.Pixels[so + 2];
                    image.Pixels[o + 3] = 255;
                }
        }
        return image;
    }

    static bool IsResourceOverlay(string name) =>
        name.StartsWith("TIB", StringComparison.OrdinalIgnoreCase) || name.StartsWith("GEM", StringComparison.OrdinalIgnoreCase);

    static bool IsWallOverlay(Ra2Art art, string name) => art.Rules.Bool(name, "Wall");

    static bool IsBridgeOverlay(string name) => name.Contains("BRDG", StringComparison.OrdinalIgnoreCase) || name.StartsWith("BRIDGE", StringComparison.OrdinalIgnoreCase);

    // ---- The map list ----

    public static void AddTo(MapCache cache)
    {
        foreach (var game in new[] { Ra2, Ts })
            AddTo(cache, game);
    }

    static void AddTo(MapCache cache, IsoGame game)
    {
        foreach (string name in MapNames(game))
        {
            if (Load(game, name) is not { } m || Starts(m) is not { Count: >= 2 } starts || (game == Ra2 && m.Ini.Get("Header", "NumberStartingPoints").Length == 0))
                continue;
            var layout = new Layout(m, game);
            var meta = new MapMetaData
            {
                FileName = MapCache.Key(game.Prefix + name),
                NameLookupTag = $"{game.Title}: {Title(m, name)}",
                IsOfficial = true,
                IsMultiplayer = true,
                NumPlayers = Math.Min(starts.Count, Logic.Multiplayer.GameInfo.MaxSlots),
                ExtentMax = new Vector3(layout.WorldW, layout.WorldH, 0),
            };
            for (int i = 0; i < starts.Count; i++)
                meta.Waypoints[$"Player_{i + 1}_Start"] = layout.World(starts[i].X + 0.5f, starts[i].Y + 0.5f);
            meta.InitialCameraPosition = meta.StartPosition(0) ?? meta.ExtentMax / 2;
            cache.Add(meta);
        }
    }

    /// <summary>The map list's picture of a Red Alert 2 map (its ground, small).</summary>
    public static Image? Preview(string map)
    {
        var game = GameOf(map);
        if (game.Art() is not { } art || Load(game, map[game.Prefix.Length..]) is not { } m)
            return null;
        var theater = Ra2Art.TheaterOf(m.Theater);
        var layout = new Layout(m, game);
        return Ground(m, art, ReadTileSet(art, theater), layout, scale: Math.Min(1f, 256f / Math.Max(layout.ImageW, layout.ImageH)));
    }

    // ---- The Zero Hour map ----

    public static MapFile Convert(string map, MapLighting? lighting)
    {
        var game = GameOf(map);
        var art = game.Art() ?? throw new InvalidOperationException($"The {game.Title} data is not installed.");
        string name = map[game.Prefix.Length..];
        var m = Load(game, name) ?? throw new FileNotFoundException($"No {game.Title} map {map}.");
        Ra2Art.Theater = m.Theater;
        var theater = Ra2Art.TheaterOf(m.Theater);
        var set = ReadTileSet(art, theater);
        var layout = new Layout(m, game);
        float worldW = layout.WorldW, worldH = layout.WorldH;
        int cellsX = (int)MathF.Ceiling(worldW / MapFile.CellSize), cellsY = (int)MathF.Ceiling(worldH / MapFile.CellSize);

        var file = new MapFile();
        file.HeightMap.Width = cellsX + 1;
        file.HeightMap.Height = cellsY + 1;
        file.HeightMap.BorderSize = 0;
        file.HeightMap.Boundaries.Add((cellsX, cellsY));
        file.HeightMap.Heights = new byte[(cellsX + 1) * (cellsY + 1)];
        file.GroundImage = Ground(m, art, set, layout);
        file.WorldInfo["mapName"] = Title(m, name);
        for (int t = 0; t < 4; t++)
        {
            for (int i = 0; i < 3; i++)
                if (lighting != null)
                    file.Lighting.ObjectLights[t, i] = lighting.ObjectLights[t, i];
            file.Lighting.TerrainLights[t, 0] = new MapLight { Ambient = Vector3.One, Diffuse = Vector3.Zero, Direction = -Vector3.UnitZ };
            if (lighting == null)
                file.Lighting.ObjectLights[t, 0] = new MapLight { Ambient = new Vector3(0.6f), Diffuse = new Vector3(0.6f), Direction = Vector3.Normalize(new Vector3(-1, 1, -2)) };
        }
        file.Lighting.CurrentTime = MapLighting.TimeOfDay.Afternoon;

        // The cells by position, and what lies on them.
        var cells = new Dictionary<(int, int), IsoMap.Cell>();
        foreach (var c in m.Cells)
            cells[(c.X, c.Y)] = c;
        var overlayTypes = (art.IsTiberianSun ? art.Rules.TypeList("OverlayTypes") : art.Rules.Values("OverlayTypes")).ToList();
        string OverlayAt(int x, int y) => (uint)x < 512 && (uint)y < 512 && m.Overlay[y * 512 + x] is var o && o != 0xFF && o < overlayTypes.Count ? overlayTypes[o] : "";
        IsoTemplate.Tile? TileAt(int x, int y) => cells.TryGetValue((x, y), out var c) ? TileOf(art, set, c.Tile == 0xFFFF ? 0 : c.Tile, c.SubTile) : null;
        bool Water(int x, int y) => IsWater(TileAt(x, y)) && !IsBridgeOverlay(OverlayAt(x, y));
        var structures = new HashSet<(int, int)>();
        foreach (var s in m.Structures)
        {
            string image = art.Rules.Get(s.Type, "Image") is { Length: > 0 } i ? i : s.Type;
            var f = art.Art.Get(image, "Foundation", "1x1").ToLowerInvariant().Split('x');
            int fw = int.TryParse(f[0], out int a) ? a : 1, fh = f.Length > 1 && int.TryParse(f[1], out int b) ? b : 1;
            for (int dy = 0; dy < fh; dy++)
                for (int dx = 0; dx < fw; dx++)
                    structures.Add((s.X + dx, s.Y + dy));
        }
        // (Ore lies on open ground: its cells stay open even where the art beside them is rock.)
        bool Blocked(int x, int y) => !cells.ContainsKey((x, y)) || !IsResourceOverlay(OverlayAt(x, y)) && (IsRock(TileAt(x, y)) || IsWallOverlay(art, OverlayAt(x, y)));

        // Heights: each cell's level; water lowered under one water area.
        bool anyWater = false;
        for (int y = 0; y <= cellsY; y++)
            for (int x = 0; x <= cellsX; x++)
            {
                var (cx, cy) = layout.Cell(x * MapFile.CellSize, y * MapFile.CellSize);
                int level = cells.TryGetValue((cx, cy), out var c) ? c.Level : 0;
                bool water = Water(cx, cy);
                anyWater |= water;
                file.HeightMap.Heights[y * (cellsX + 1) + x] = water ? (byte)0 : (byte)Math.Min(255, BaseHeight + level * HeightPerLevel);
            }
        if (anyWater)
        {
            float level = 8 * MapFile.HeightScale;
            var sea = new PolygonTrigger { Name = "RedAlert2Water", IsWater = true, Id = 1 };
            sea.Points.Add(new Vector3(0, 0, level));
            sea.Points.Add(new Vector3(worldW, 0, level));
            sea.Points.Add(new Vector3(worldW, worldH, level));
            sea.Points.Add(new Vector3(0, worldH, level));
            file.PolygonTriggers.Add(sea);
        }

        // What can't be crossed: rock and cliffs, walls, and outside the map.
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
                var (cx, cy) = layout.Cell((x + 0.5f) * MapFile.CellSize, (y + 0.5f) * MapFile.CellSize);
                if (Blocked(cx, cy))
                    tiles.CliffStateBits[y * tiles.CliffBytesPerRow + (x >> 3)] |= (byte)(1 << (x & 7));
            }
        file.BlendTiles = tiles;

        Vector3 At(float a, float b) => layout.World(a, b);

        // Ore and gems: a pile a cell, as rich as its overlay frame.
        for (int i = 0; i < m.Overlay.Length; i++)
        {
            if (m.Overlay[i] == 0xFF || m.Overlay[i] >= overlayTypes.Count)
                continue;
            string type = overlayTypes[m.Overlay[i]];
            if (!IsResourceOverlay(type))
                continue;
            string kind = type.StartsWith("GEM", StringComparison.OrdinalIgnoreCase) ? $"{game.Names}_Gem" : $"{game.Names}_Ore";
            int richness = Math.Clamp(m.OverlayData[i] + 1, 1, 12);
            file.Objects.Add(new MapObject { TemplateName = $"{kind}{richness:00}", Position = At(i % 512 + 0.5f, i / 512 + 0.5f) });
        }
        // Trees.
        foreach (var (x, y, type) in m.Terrain)
            file.Objects.Add(new MapObject { TemplateName = $"{game.Names}_Terrain_{type.ToUpperInvariant()}", Position = At(x + 0.5f, y + 0.5f) });
        // Neutral buildings: civilian houses and tech buildings, at their foundation's middle.
        foreach (var s in m.Structures)
        {
            string image = art.Rules.Get(s.Type, "Image") is { Length: > 0 } i ? i : s.Type;
            var f = art.Art.Get(image, "Foundation", "1x1").ToLowerInvariant().Split('x');
            int fw = int.TryParse(f[0], out int a) ? a : 1, fh = f.Length > 1 && int.TryParse(f[1], out int b) ? b : 1;
            var obj = new MapObject { TemplateName = $"{game.Names}CIV_{s.Type.ToUpperInvariant()}", Position = At(s.X + fw / 2f, s.Y + fh / 2f) };
            if (s.Health is > 0 and < 256)
                obj.Properties["objectInitialHealth"] = Math.Max(1, s.Health * 100 / 256);
            file.Objects.Add(obj);
        }
        // Start positions: waypoints 0-7, numbered in order.
        var starts = Starts(m);
        for (int i = 0; i < starts.Count; i++)
            file.Objects.Add(new MapObject
            {
                TemplateName = "*Waypoints/Waypoint",
                Position = At(starts[i].X + 0.5f, starts[i].Y + 0.5f),
                Properties = new(StringComparer.OrdinalIgnoreCase) { ["waypointID"] = i + 1, ["waypointName"] = $"Player_{i + 1}_Start" },
            });
        return file;
    }
}
