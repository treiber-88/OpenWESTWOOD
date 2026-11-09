using System.Numerics;
using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.TiberianDawn;
using OpenWestwood.TiberianDawn.Art;
using OpenWestwood.TiberianDawn.Data;
using OpenWestwood.TiberianDawn.Logic;
using OpenWestwood.TiberianDawn.Logic.Types;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Tiberian Dawn's multiplayer maps (SCMxxEA) as Zero Hour engine maps, named "td:SCM01EA" in the map list.
/// The map is made in memory from the original scenario:
/// <list type="bullet">
/// <item>flat ground, the size of the scenario's playable area, one TD cell = <see cref="TdScale.Cell"/> units;</item>
/// <item>the TD ground (templates, smudges, walls and civilian buildings) drawn into a picture laid over it;</item>
/// <item>rock (cliffs), water (rivers too), walls and buildings impassable (flagged as cliffs);</item>
/// <item>trees as TD_Terrain_* objects, each Tiberium cell as a TD_Tiberium supply pile (its density from its neighbours);</item>
/// <item>TD's multiplayer start waypoints 0-5 as Player_1_Start ... Player_6_Start.</item>
/// </list>
/// Red Alert's multiplayer maps come the same way, named "ra:SCM01EA": 128x128 cells, ore and gems for
/// Tiberium, start waypoints 0-7, and the land of each cell from its template's icon set.
/// </summary>
public static partial class TdMaps
{
    public const string Prefix = "td:";
    public const string RaPrefix = "ra:";

    public static bool IsTdMap(string map) => map.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || IsRaMap(map);

    public static bool IsRaMap(string map) => map.StartsWith(RaPrefix, StringComparison.OrdinalIgnoreCase);

    static List<string>? raNames;

    /// <summary>Red Alert's multiplayer scenarios (SCM..).</summary>
    public static IReadOnlyList<string> RaScenarios =>
        raNames ??= RedAlertContent.Get() is { } ra
            ? ra.Files.Packages.SelectMany(p => p.FilePaths).Select(Path.GetFileName).OfType<string>()
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^SCM[0-9]{2}E[A-Z]\.INI$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(n => Path.GetFileNameWithoutExtension(n).ToUpperInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Red Alert's overlay names in its OverlayType order (what [OverlayPack] numbers).</summary>
    static List<string> RaOverlayNames(TdContent ra) =>
        ra.Rules.Section("Overlays").Entries.OrderBy(e => Core.Rules.RuleValue.Int(e.Value.GetString("ID"))).Select(e => e.Value.GetString("IniName")).ToList();

    /// <summary>A map name's game files and scenario ("td:SCM01EA" or "ra:SCM01EA"); null if it isn't there.</summary>
    static (TdContent Content, TdScenario Scenario)? Scenario(string map)
    {
        if (IsRaMap(map))
            return RedAlertContent.Get() is { } ra && TdScenario.Load(ra.Files, map[RaPrefix.Length..].ToUpperInvariant(), RaOverlayNames(ra)) is { } rs ? (ra, rs) : null;
        if (TiberianDawnContent.Get() is not { } td)
            return null;
        if (OraScenario(map[Prefix.Length..]) is { } ora)
            return (td, ora);
        return TdScenario.Load(td.Files, map[Prefix.Length..].ToUpperInvariant()) is { } ts ? (td, ts) : null;
    }

    /// <summary>Maps made for OpenRA's Tiberian Dawn (content/tiberiandawn/oramaps/*.oramap), named "td:&lt;file name&gt;".</summary>
    static string OraFolder => Path.Combine(GameContent.FindRoot(), "content", "tiberiandawn", "oramaps");

    static readonly Dictionary<string, TdScenario?> oraScenarios = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An OpenRA Tiberian Dawn map as a TD scenario. OpenRA's TD tilesets keep TD's template numbers, so each cell's
    /// (template, icon) carries over (its "pick any" icon 255 picked by position, as OpenRA does); its Tiberium becomes
    /// TI1 overlay, its trees, rocks and blossom trees TERRAIN, its civilian buildings neutral STRUCTURES, its
    /// mpspawn actors the start waypoints. map.bin: format byte, uint16 width, height, uint32 offsets of the tiles
    /// (uint16 template, byte icon per cell, column by column) and the resources (byte type, byte density).
    /// </summary>
    static TdScenario? OraScenario(string name)
    {
        if (oraScenarios.TryGetValue(name, out var cached))
            return cached;
        string path = Path.Combine(OraFolder, name + ".oramap");
        TdScenario? result = null;
        if (File.Exists(path))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var yaml = Formats.OpenRA.MiniYaml.Parse(new StreamReader(zip.GetEntry("map.yaml")!.Open()).ReadToEnd());
            string Get(string key) => yaml.FirstOrDefault(n => n.Key == key)?.Value ?? "";
            using var ms = new MemoryStream();
            zip.GetEntry("map.bin")!.Open().CopyTo(ms);
            var bin = ms.ToArray();
            int width = BitConverter.ToUInt16(bin, 1), height = BitConverter.ToUInt16(bin, 3);
            int tilesAt = BitConverter.ToInt32(bin, 5), resourcesAt = BitConverter.ToInt32(bin, 13);
            int size = Math.Max(TdScenario.MapCellWidth, Math.Max(width, height));
            var cells = new byte[size * size * 2];
            for (int i = 0; i < size * size; i++)
                cells[i * 2] = TdScenario.TemplateNone;
            var ini = new System.Text.StringBuilder();
            var overlay = new System.Text.StringBuilder("[OVERLAY]\n");
            int p = tilesAt;
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++, p += 3)
                {
                    int template = BitConverter.ToUInt16(bin, p), icon = bin[p + 2];
                    int cell = y * size + x;
                    cells[cell * 2] = template >= 255 ? TdScenario.TemplateNone : (byte)template;
                    cells[cell * 2 + 1] = (byte)(icon == 255 ? x % 4 + y % 4 * 4 : icon);
                }
            if (resourcesAt > 0)
            {
                p = resourcesAt;
                for (int x = 0; x < width; x++)
                    for (int y = 0; y < height; y++, p += 2)
                        if (bin[p] != 0)
                            overlay.Append($"{y * size + x}=TI1\n");
            }
            var bounds = Get("Bounds").Split(',').Select(v => int.TryParse(v.Trim(), out int n) ? n : 0).ToArray();
            if (bounds.Length != 4)
                bounds = [0, 0, width, height];
            string theater = Get("Tileset").ToUpperInvariant() switch { "DESERT" => "DESERT", "WINTER" or "SNOW" => "WINTER", _ => "TEMPERATE" };
            ini.Append($"[Basic]\nName={Get("Title")}\n[MAP]\nTheater={theater}\nX={bounds[0]}\nY={bounds[1]}\nWidth={bounds[2]}\nHeight={bounds[3]}\n");
            var waypoints = new System.Text.StringBuilder("[Waypoints]\n");
            var terrain = new System.Text.StringBuilder("[TERRAIN]\n");
            var structures = new System.Text.StringBuilder("[STRUCTURES]\n");
            int spawns = 0, buildings = 0;
            foreach (var actor in yaml.FirstOrDefault(n => n.Key == "Actors")?.Children ?? [])
            {
                if (actor.Get("Location").Split(',') is not [var sx, var sy] || !int.TryParse(sx.Trim(), out int ax) || !int.TryParse(sy.Trim(), out int ay))
                    continue;
                int cell = ay * size + ax;
                string type = actor.Value.Trim().ToUpperInvariant();
                if (type == "MPSPAWN")
                    waypoints.Append($"{spawns++}={cell}\n");
                else if (type.StartsWith('T') && type.Length <= 4 || type.StartsWith("ROCK") || type.StartsWith("SPLIT"))
                    terrain.Append($"{cell}={type},None\n");
                else if (type.StartsWith('V') || type is "ARCO" or "WOOD")
                    structures.Append($"{buildings++}=Neutral,{type},256,{cell},0,None\n");
            }
            ini.Append(waypoints).Append(terrain).Append(structures).Append(overlay);
            result = new TdScenario(name.ToUpperInvariant(), ini.ToString(), cells, mapWidth: size);
        }
        return oraScenarios[name] = result;
    }

    static List<string>? names;

    /// <summary>The multiplayer scenarios in the TD files (SCM..).</summary>
    public static IReadOnlyList<string> Scenarios
    {
        get
        {
            if (names == null)
                names = TiberianDawnContent.Get() is { } content
                    ? TdScenario.List(content.Files).Where(n => n.StartsWith("SCM", StringComparison.OrdinalIgnoreCase)).ToList()
                    : [];
            return names;
        }
    }

    /// <summary>Adds the TD maps to the map list (what the menus know without building the map).</summary>
    public static void AddTo(MapCache cache)
    {
        var oraMaps = Directory.Exists(OraFolder) ? Directory.GetFiles(OraFolder, "*.oramap").Select(f => Prefix + Path.GetFileNameWithoutExtension(f)) : [];
        var maps = Scenarios.Select(n => Prefix + n).Concat(oraMaps).Concat(RaScenarios.Select(n => RaPrefix + n));
        foreach (string map in maps)
        {
            if (Scenario(map) is not var (_, scenario))
                continue;
            bool ra = scenario.IsRedAlert;
            string name = scenario.Name;
            float cell = TdScale.Cell;
            var meta = new MapMetaData
            {
                FileName = MapCache.Key(map),
                NameLookupTag = $"{(ra ? "RA" : "TD")}: {Title(scenario)}",
                IsOfficial = true,
                IsMultiplayer = true,
                ExtentMin = Vector3.Zero,
                ExtentMax = new Vector3(scenario.Width * cell, scenario.Height * cell, 0),
            };
            int players = 0;
            for (int i = 0; i < (ra ? 8 : 6); i++)
                if (scenario.Waypoints.TryGetValue(i, out int wcell) && wcell >= 0)
                {
                    meta.Waypoints[$"Player_{++players}_Start"] = CellCenter(scenario, wcell);
                }
            meta.NumPlayers = Math.Min(players, Logic.Multiplayer.GameInfo.MaxSlots);
            meta.InitialCameraPosition = meta.StartPosition(0) ?? meta.ExtentMax / 2;
            foreach (var (tcell, type) in scenario.Overlay)
                if (IsResource(type))
                    meta.SupplyPositions.Add(CellCenter(scenario, tcell));
            cache.Add(meta);
        }
    }

    /// <summary>"Name (SCM01EA)": the scenario's own name from its [Basic] section, if it has one.</summary>
    static string Title(TdScenario scenario)
    {
        string name = scenario.Ini.Get("Basic", "Name").Trim();
        return name.Length > 0 && !name.Equals(scenario.Name, StringComparison.OrdinalIgnoreCase) ? $"{name} ({scenario.Name})" : scenario.Name;
    }

    /// <summary>Tiberium (TD), ore or gems (RA).</summary>
    static bool IsResource(string overlay) =>
        overlay.StartsWith("TI", StringComparison.OrdinalIgnoreCase) || overlay.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase) || overlay.StartsWith("GEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>World position of the middle of a TD cell (TD rows run south; Zero Hour's Y runs north).</summary>
    static Vector3 CellCenter(TdScenario s, int cell)
    {
        float c = TdScale.Cell;
        int x = cell % s.MapWidth - s.X, y = cell / s.MapWidth - s.Y;
        return new Vector3((x + 0.5f) * c, (s.Height - y - 0.5f) * c, 0);
    }

    /// <summary>The map list's picture of a TD map: the playable area with its Tiberium and trees.</summary>
    public static Formats.Images.Image? Preview(string map) =>
        Scenario(map) is var (content, scenario) ? new TdMapRenderer(content).Render(scenario) : null;

    /// <summary>
    /// The camera's angle above the ground for the map last read, as its own game showed it (null: Zero Hour's own view).
    /// Tiberian Dawn, Red Alert and Dune 2000 looked straight down; Red Alert 2 and Tiberian Sun's maps are their isometric
    /// pictures stretched to twice the height, so a view squashing them back to half (sin 30 degrees) is the original 2:1 view.
    /// </summary>
    public static float? ViewPitch { get; private set; }
    public const float TopDownPitch = 1.55f;
    public static readonly float IsometricPitch = MathF.Asin(0.5f);

    /// <summary>Reads a map: a TD map is built from its scenario, any other is read from the game's files.</summary>
    public static MapFile Read(VirtualFileSystem files, string map)
    {
        TdSpriteRenderer.OnSageMap = !Dune2000.D2kMaps.IsD2kMap(map) && !RedAlert2.Ra2Maps.IsRa2Map(map) && !IsTdMap(map);
        ViewPitch = RedAlert2.Ra2Maps.IsRa2Map(map) ? IsometricPitch : Dune2000.D2kMaps.IsD2kMap(map) || IsTdMap(map) ? TopDownPitch : null;
        if (Dune2000.D2kMaps.IsD2kMap(map))
            return Dune2000.D2kMaps.Convert(map, ZeroHourLighting(files));
        if (RedAlert2.Ra2Maps.IsRa2Map(map))
            return RedAlert2.Ra2Maps.Convert(map, ZeroHourLighting(files));
        if (KanesWrath.KwMaps.IsKwMap(map))
            return KanesWrath.KwMaps.Read(map);
        if (!IsTdMap(map))
            return MapFile.Read(files.ReadAllBytes(map.Replace('\\', '/')));
        var (content, scenario) = Scenario(map) ?? throw new FileNotFoundException($"No such scenario (or its game's files aren't installed): {map}.");
        // Zero Hour's light for the units and buildings drawn in 3D (a desert map's); the TD ground keeps its own colours.
        MapLighting? lighting = null;
        const string LightingFrom = "Maps/Tournament Desert/Tournament Desert.map";
        if (files.Exists(LightingFrom))
            lighting = MapFile.Read(files.ReadAllBytes(LightingFrom)).Lighting;
        return Convert(content, scenario, lighting);
    }

    /// <summary>A Zero Hour desert map's light, for the 3D models on maps made from other games.</summary>
    public static MapLighting? ZeroHourLighting(VirtualFileSystem files)
    {
        const string LightingFrom = "Maps/Tournament Desert/Tournament Desert.map";
        return files.Exists(LightingFrom) ? MapFile.Read(files.ReadAllBytes(LightingFrom)).Lighting : null;
    }

    /// <summary>Builds the Zero Hour map for a TD scenario.</summary>
    public static MapFile Convert(TdContent content, TdScenario s, MapLighting? lighting = null)
    {
        var types = new TdTypes(content.Rules);
        float c = TdScale.Cell;
        float worldW = s.Width * c, worldH = s.Height * c;
        int cellsX = (int)MathF.Ceiling(worldW / MapFile.CellSize), cellsY = (int)MathF.Ceiling(worldH / MapFile.CellSize);

        var map = new MapFile();
        map.HeightMap.Width = cellsX + 1;
        map.HeightMap.Height = cellsY + 1;
        map.HeightMap.BorderSize = 0;
        map.HeightMap.Boundaries.Add((cellsX, cellsY));
        map.HeightMap.Heights = Enumerable.Repeat((byte)16, (cellsX + 1) * (cellsY + 1)).ToArray();
        bool mission = !s.IsRedAlert && IsMission(s.Name);
        map.GroundImage = new TdMapRenderer(content).Render(s, groundOnly: true, buildings: !mission);
        map.WorldInfo["mapName"] = s.Name;

        // Light: the TD ground shows its own colours; 3D models get Zero Hour's light.
        if (lighting != null)
            for (int t = 0; t < 4; t++)
                for (int i = 0; i < 3; i++)
                    map.Lighting.ObjectLights[t, i] = lighting.ObjectLights[t, i];
        for (int t = 0; t < 4; t++)
        {
            map.Lighting.TerrainLights[t, 0] = new MapLight { Ambient = Vector3.One, Diffuse = Vector3.Zero, Direction = -Vector3.UnitZ };
            if (lighting == null)
                map.Lighting.ObjectLights[t, 0] = new MapLight { Ambient = new Vector3(0.6f), Diffuse = new Vector3(0.6f), Direction = Vector3.Normalize(new Vector3(-1, 1, -2)) };
        }
        map.Lighting.CurrentTime = MapLighting.TimeOfDay.Afternoon;

        // Impassable TD cells: the ground's land type, walls, and the scenario's (civilian) buildings.
        int mapWidth = s.MapWidth;
        var blocked = new bool[s.Cells.Length];
        // Red Alert's open water is real water (ships sail it); its rivers stay impassable, as in RA.
        var water = new bool[s.Cells.Length];
        var templates = types.Templates.GroupBy(t => (int)t.Type).ToDictionary(g => g.Key, g => g.First());
        var renderer = new TdMapRenderer(content);
        string ext = TdScenario.TheaterFiles(s.Theater).Extension;
        for (int cell = 0; cell < s.Cells.Length; cell++)
        {
            var (template, icon) = s.Cells[cell];
            if (s.IsEmpty(template))
                continue;
            if (s.IsRedAlert)
            {
                // TemplateTypeClass::Land_Type: the icon set's control code for the cell: 8 rock, 10 water, 11 river.
                int control = content.TemplateById(template) is { } node ? renderer.Icons(node.GetString("IniName") + ext)?.Control(icon) ?? -1 : -1;
                if (control == 10)
                    water[cell] = true;
                else if (control is 8 or 11)
                    blocked[cell] = true;
                continue;
            }
            var land = templates.TryGetValue(template, out var t) ? t.LandOf(icon) : LandType.LAND_CLEAR;
            if (land is LandType.LAND_ROCK or LandType.LAND_WATER or LandType.LAND_WALL)
                blocked[cell] = true;
        }
        foreach (var (cell, type) in s.Overlay)
            if (content.Find("Overlays", type)?.GetString("IsWall") is "1" or "true")
                blocked[cell] = true;
        var buildings = types.Buildings.ToDictionary(b => b.IniName, StringComparer.OrdinalIgnoreCase);
        // (A mission's buildings are objects, which block the ground themselves.)
        foreach (var b in mission ? [] : s.Structures)
            if (buildings.TryGetValue(b.Type, out var type))
                foreach (short offset in type.Occupy_List(types, types.Special))
                {
                    if (offset == TdMath.RefreshEol)
                        break;
                    int cell = b.Cell + offset;
                    if (cell >= 0 && cell < blocked.Length)
                        blocked[cell] = true;
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
                // The TD cell under this Zero Hour cell's middle.
                float wx = (x + 0.5f) * MapFile.CellSize, wy = (y + 0.5f) * MapFile.CellSize;
                int tx = (int)(wx / c) + s.X, ty = s.Height - 1 - (int)(wy / c) + s.Y;
                if (tx < s.X || ty < s.Y || tx >= s.X + s.Width || ty >= s.Y + s.Height || blocked[ty * mapWidth + tx])
                    tiles.CliffStateBits[y * tiles.CliffBytesPerRow + (x >> 3)] |= (byte)(1 << (x & 7));
            }
        map.BlendTiles = tiles;

        // Water: the ground under it lowered, and one water area over the map at half the land's height.
        if (water.Any(w => w))
        {
            int RaCellAt(float wx, float wy)
            {
                int tx = Math.Clamp((int)(wx / c), 0, s.Width - 1) + s.X, ty = s.Height - 1 - Math.Clamp((int)(wy / c), 0, s.Height - 1) + s.Y;
                return ty * mapWidth + tx;
            }
            for (int y = 0; y <= cellsY; y++)
                for (int x = 0; x <= cellsX; x++)
                    if (water[RaCellAt(x * MapFile.CellSize, y * MapFile.CellSize)])
                        map.HeightMap.Heights[y * (cellsX + 1) + x] = 0;
            float level = 8 * MapFile.HeightScale;
            var sea = new PolygonTrigger { Name = "RedAlertWater", IsWater = true, Id = 1 };
            sea.Points.Add(new Vector3(0, 0, level));
            sea.Points.Add(new Vector3(worldW, 0, level));
            sea.Points.Add(new Vector3(worldW, worldH, level));
            sea.Points.Add(new Vector3(0, worldH, level));
            map.PolygonTriggers.Add(sea);
        }

        // Trees: placed by their cell's top-left corner, as TD draws them; the object stands at the art's middle.
        string prefix = s.IsRedAlert ? "RA_" : "TD_";
        var art = s.IsRedAlert ? RedAlertContent.Art : TiberianDawnContent.Art;
        foreach (var (cell, type, _) in s.Terrain)
        {
            var shape = art?.Shape(type);
            float w = (shape?.Width ?? 24) * TdScale.UnitsPerPixel, h = (shape?.Height ?? 24) * TdScale.UnitsPerPixel;
            var corner = CellCenter(s, cell) + new Vector3(-c / 2, c / 2, 0);
            map.Objects.Add(new MapObject { TemplateName = prefix + "Terrain_" + type.ToUpperInvariant(), Position = corner + new Vector3(w / 2, -h / 2, 0) });
        }

        // Tiberium: a supply pile per cell, as dense as TD makes it (its number of Tiberium neighbours).
        // (Red Alert: ore, and gems, as dense as their neighbours make them, CellClass::Overlay_Adjust.)
        var tiberium = s.Overlay.Where(o => IsResource(o.Type)).ToDictionary(o => o.Cell, o => o.Type);
        int[] adjacentTable = [0, 1, 3, 4, 6, 7, 8, 10, 11];
        foreach (var (cell, type) in tiberium)
        {
            int count = new[] { -mapWidth, -mapWidth + 1, 1, mapWidth + 1, mapWidth, mapWidth - 1, -1, -mapWidth - 1 }.Count(d => tiberium.ContainsKey(cell + d));
            string kind = !s.IsRedAlert ? "TD_Tiberium" : type.StartsWith("GEM", StringComparison.OrdinalIgnoreCase) ? "RA_Gem" : "RA_Ore";
            map.Objects.Add(new MapObject { TemplateName = $"{kind}{adjacentTable[count] + 1:00}", Position = CellCenter(s, cell) });
        }

        if (mission)
        {
            AddMission(map, content, s);
            return map;
        }

        // Start positions: TD's multiplayer waypoints 0-5, numbered in order.
        int player = 0;
        for (int i = 0; i < (s.IsRedAlert ? 8 : 6); i++)
            if (s.Waypoints.TryGetValue(i, out int wcell) && wcell >= 0)
            {
                player++;
                map.Objects.Add(new MapObject
                {
                    TemplateName = "*Waypoints/Waypoint",
                    Position = CellCenter(s, wcell),
                    Properties = new(StringComparer.OrdinalIgnoreCase) { ["waypointID"] = player, ["waypointName"] = $"Player_{player}_Start" },
                });
            }
        return map;
    }
}
