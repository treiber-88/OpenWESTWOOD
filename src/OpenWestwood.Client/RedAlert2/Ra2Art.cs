using System.Numerics;
using OpenWestwood.Formats.Images;
using OpenWestwood.Formats.Westwood;
using OpenWestwood.TiberianDawn;

namespace OpenWestwood.Client.RedAlert2;

/// <summary>
/// Red Alert 2 / Yuri's Revenge art for its factions in Zero Hour engine matches: the TS-format shapes (buildings,
/// infantry, animations, cameos) with the theater's unit palette, and the voxel vehicles drawn into sprites for
/// each of 32 facings, lit from their shape and remapped to the owner's colour (palette 16-31), as the game
/// turns its voxels into screen images. Art settings come from ARTMD.INI over ART.INI.
/// </summary>
public sealed class Ra2Art
{
    public const int RemapFirst = 16, RemapLast = 31;

    readonly TdContent content;
    readonly Dictionary<string, TsShape?> shapes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (VoxelModel Model, HvaAnimation? Hva)?> voxels = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, int, uint), Image?> voxelImages = new();

    public Ra2Ini Art { get; }
    public Ra2Ini Rules { get; }
    /// <summary>The units' and buildings' palette: the theater's (UNITSNO.PAL on snow), else the temperate one.</summary>
    public WestwoodPalette UnitPalette => Palette($"UNIT{TheaterOf(Theater).Extension}.PAL") ?? unitTemperate;
    readonly WestwoodPalette unitTemperate;
    public WestwoodPalette AnimPalette { get; }
    public WestwoodPalette CameoPalette { get; }
    public Dictionary<string, string> Strings { get; }

    static Ra2Art? instance, tsInstance;
    static bool tried, tsTried;
    static readonly object LoadLock = new();

    /// <summary>Red Alert 2 / Yuri's Revenge's art (content/redalert2), or null.</summary>
    public static Ra2Art? Get()
    {
        // Locked: the art may be read ahead on a loading thread while the game asks for it.
        lock (LoadLock)
        {
            if (!tried)
            {
                tried = true;
                string root = GameContent.FindRoot();
                if (File.Exists(Path.Combine(root, "content", "redalert2", "ra2md.mix")) && File.Exists(Path.Combine(root, "mods", "redalert2", "mod.yaml")))
                    instance = new Ra2Art(TdContent.Load(root, "redalert2"), ["RULES.INI", "RULESMD.INI"], ["ART.INI", "ARTMD.INI"], "RA2MD.CSF", isTiberianSun: false);
            }
        }
        return instance;
    }

    /// <summary>Tiberian Sun / Firestorm's art (content/tiberiansun): the same formats, its own INIs and palettes.</summary>
    public static Ra2Art? GetTiberianSun()
    {
        // Locked: the art may be read ahead on a loading thread while the game asks for it.
        lock (LoadLock)
        {
            if (!tsTried)
            {
                tsTried = true;
                string root = GameContent.FindRoot();
                if (File.Exists(Path.Combine(root, "content", "tiberiansun", "TIBSUN.MIX")) && File.Exists(Path.Combine(root, "mods", "tiberiansun", "mod.yaml")))
                    tsInstance = new Ra2Art(TdContent.Load(root, "tiberiansun"), ["RULES.INI", "FIRESTRM.INI"], ["ART.INI", "ARTFS.INI"], "", isTiberianSun: true);
            }
        }
        return tsInstance;
    }

    /// <summary>The art of a draw module's game ("Game: TS" is Tiberian Sun's, else Red Alert 2's).</summary>
    public static Ra2Art? For(string game) => game.Equals("TS", StringComparison.OrdinalIgnoreCase) ? GetTiberianSun() : Get();

    /// <summary>Tiberian Sun's (48x24 cells, its own theaters' files) rather than Red Alert 2's.</summary>
    public bool IsTiberianSun { get; }

    Ra2Art(TdContent content, string[] rules, string[] art, string csf, bool isTiberianSun)
    {
        this.content = content;
        IsTiberianSun = isTiberianSun;
        Art = new Ra2Ini();
        Rules = new Ra2Ini();
        foreach (string f in art)
            if (content.Files.Exists(f))
                Art.Read(System.Text.Encoding.Latin1.GetString(content.Files.ReadAllBytes(f)));
        foreach (string f in rules)
            if (content.Files.Exists(f))
                Rules.Read(System.Text.Encoding.Latin1.GetString(content.Files.ReadAllBytes(f)));
        WestwoodPalette Pal(params string[] names) => names.Where(content.Files.Exists).Select(n => new WestwoodPalette(content.Files.ReadAllBytes(n))).First();
        unitTemperate = Pal("UNITTEM.PAL");
        AnimPalette = Pal("ANIM.PAL", "UNITTEM.PAL");
        CameoPalette = Pal("CAMEO.PAL", "UNITTEM.PAL");
        Strings = csf.Length > 0 && content.Files.Exists(csf) ? CsfFile.Read(content.Files.ReadAllBytes(csf)) : new();
    }

    public TdContent Content => content;

    /// <summary>A theater's files: its tile set INI, tile and overlay extension, iso (ground) and object palettes, building letter.</summary>
    public sealed record TheaterInfo(string Ini, string FallbackIni, string Extension, string IsoPalette, string Palette, char Letter);

    public static TheaterInfo TheaterOf(string theater) => theater.ToUpperInvariant() switch
    {
        "SNOW" => new("SNOWMD.INI", "SNOW.INI", "SNO", "ISOSNO.PAL", "SNOW.PAL", 'A'),
        "URBAN" => new("URBANMD.INI", "URBAN.INI", "URB", "ISOURB.PAL", "URBAN.PAL", 'U'),
        "NEWURBAN" => new("URBANNMD.INI", "URBANN.INI", "UBN", "ISOUBN.PAL", "URBANN.PAL", 'N'),
        "DESERT" => new("DESERTMD.INI", "DESERT.INI", "DES", "ISODES.PAL", "DESERT.PAL", 'D'),
        "LUNAR" => new("LUNARMD.INI", "LUNAR.INI", "LUN", "ISOLUN.PAL", "LUNAR.PAL", 'L'),
        _ => new("TEMPERATMD.INI", "TEMPERAT.INI", "TEM", "ISOTEM.PAL", "TEMPERAT.PAL", 'T'),
    };

    /// <summary>The theater of the map being played (set when a Red Alert 2 map is loaded; temperate otherwise).</summary>
    public static string Theater { get; set; } = "TEMPERATE";

    readonly Dictionary<string, WestwoodPalette?> palettes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A palette file (cached), or null.</summary>
    public WestwoodPalette? Palette(string file)
    {
        if (!palettes.TryGetValue(file, out var palette))
            palettes[file] = palette = content.Files.Exists(file) ? new WestwoodPalette(content.Files.ReadAllBytes(file)) : null;
        return palette;
    }

    /// <summary>The current theater's palette for its overlays and trees (TEMPERAT.PAL...).</summary>
    public WestwoodPalette TheaterPalette => Palette(TheaterOf(Theater).Palette) ?? UnitPalette;

    /// <summary>
    /// The theater's ground palette (ISOTEM.PAL...): Tiberian Sun's trees, and the buildings whose art says
    /// TerrainPalette=true (the city blocks), are drawn in it.
    /// </summary>
    public WestwoodPalette IsoPalette => Palette(TheaterOf(Theater).IsoPalette) ?? UnitPalette;

    /// <summary>A building's palette: the ground's for TerrainPalette=true, else the units'.</summary>
    public WestwoodPalette BuildingPalette(string image) =>
        Art.Get(image, "TerrainPalette").ToLowerInvariant() is "true" or "yes" or "1" ? IsoPalette : UnitPalette;

    /// <summary>A theater-specific shape (TREE01.TEM, TIB01.DES), else the plain one.</summary>
    public TsShape? TheaterShape(string name) => Shape($"{name}.{TheaterOf(Theater).Extension}") ?? Shape(name);

    public TsShape? Shape(string name)
    {
        if (shapes.TryGetValue(name, out var shape))
            return shape;
        string file = name.Contains('.') ? name : name + ".SHP";
        try
        {
            shape = content.Files.Exists(file) ? new TsShape(content.Files.ReadAllBytes(file)) : null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            shape = null;
        }
        return shapes[name] = shape;
    }

    /// <summary>
    /// A building's shape: NewTheater buildings take the theater's letter as their second (temperate T), else the
    /// generic G (GACNST is the snow art; GGCNST the generic one).
    /// </summary>
    public TsShape? BuildingShape(string image, char theater = '\0') =>
        BuildingShapeIn(image, theater == '\0' ? TheaterOf(Theater).Letter : theater);

    TsShape? BuildingShapeIn(string image, char theater)
    {
        // An art section's own Image= names the file (Tiberian Sun's Vulcan, RPG and SAM tower add-ons are drawn as the
        // component tower's GACTWR_B, _C and _D).
        string file = Art.Get(image, "Image") is { Length: > 0 } redirect ? redirect : image;
        return Art.Bool(image, "NewTheater") && file.Length > 2
            ? Shape(file[0] + theater.ToString() + file[2..]) ?? Shape(file[0] + "G" + file[2..]) ?? Shape(file)
            : Shape(file);
    }

    /// <summary>A shape frame as an image (its palette's colours; 0 clear), remapped to a team colour if given.</summary>
    public Image? Frame(TsShape shape, int frame, WestwoodPalette palette, (byte R, byte G, byte B)? team, bool shadow = false)
    {
        if (frame < 0 || frame >= shape.FrameCount)
            return null;
        var pixels = shape.Frame(frame);
        var image = new Image(shape.Width, shape.Height);
        for (int i = 0; i < pixels.Length; i++)
        {
            byte c = pixels[i];
            if (c == 0)
                continue;
            var (r, g, b, _) = palette.Color(c, zeroIsClear: false);
            if (shadow)
            {
                image.SetPixel(i % shape.Width, i / shape.Width, 0, 0, 0, 110);
                continue;
            }
            if (team is { } t && c >= RemapFirst && c <= RemapLast)
            {
                float k = 1 - (c - RemapFirst) / 18f;
                (r, g, b) = ((byte)(t.R * k), (byte)(t.G * k), (byte)(t.B * k));
            }
            image.SetPixel(i % shape.Width, i / shape.Width, r, g, b, 255);
        }
        return image;
    }

    /// <summary>The cameo (sidebar picture) of an object: its art's Cameo (NAMEICON.SHP), in the cameo palette.</summary>
    public Image? Cameo(string type)
    {
        string image = Rules.Get(type, "Image") is { Length: > 0 } i ? i : type;
        string cameo = Art.Get(image, "Cameo") is { Length: > 0 } c ? c : Art.Get(type, "Cameo");
        // A superweapon's SidebarImage names the picture itself (NUKEICON).
        if (cameo.Length == 0 && Shape(type) != null)
            cameo = type;
        return cameo.Length > 0 && Shape(cameo) is { } shape ? Frame(shape, 0, CameoPalette, null) : null;
    }

    (VoxelModel, HvaAnimation?)? Voxel(string name)
    {
        if (voxels.TryGetValue(name, out var v))
            return v;
        try
        {
            v = content.Files.Exists(name + ".VXL")
                ? (new VoxelModel(content.Files.ReadAllBytes(name + ".VXL")), content.Files.Exists(name + ".HVA") ? new HvaAnimation(content.Files.ReadAllBytes(name + ".HVA")) : null)
                : null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            v = null;
        }
        return voxels[name] = v;
    }

    /// <summary>Pixels a voxel is drawn with (an RA2 voxel is about this many screen pixels).</summary>
    public const float PixelsPerVoxel = 2.5f;
    /// <summary>The view's tilt (RA2's camera looks down at the battlefield at about this angle from level).</summary>
    const float Elevation = 0.85f; // (Zero Hour's camera looks down more steeply than RA2's)

    /// <summary>
    /// A voxel object (its body, and its turret and barrel when it has them) seen at a facing (0-31, counter-clockwise
    /// from north as the facings of the other games' art), lit from above and the south-west, remapped to a team colour.
    /// </summary>
    /// <param name="pitch">Nose up (+) or down (-) in steps of <see cref="PitchStep"/> (a V3 rocket climbing or diving).</param>
    /// <param name="parts">0: all of it; 1: the body alone; 2: the turret (and barrel) alone (a husk and its blown-off turret).</param>
    public Image? VoxelImage(string image, int facing, int turretFacing, uint team, int pitch = 0, int parts = 0)
    {
        var key = (image, facing | turretFacing << 8 | (pitch + 64) << 16 | parts << 24, team);
        if (voxelImages.TryGetValue(key, out var cached))
            return cached;
        var drawn = new List<(VoxelModel, HvaAnimation?, float Angle)>();
        if (parts != 2 && Voxel(image) is var (body, bodyHva))
            drawn.Add((body, bodyHva, facing));
        if (parts != 1)
        {
            if (Voxel(image + "TUR") is var (turret, turretHva))
                drawn.Add((turret, turretHva, turretFacing));
            if (Voxel(image + "BARL") is var (barrel, barrelHva))
                drawn.Add((barrel, barrelHva, turretFacing));
            // A building's voxel turret (TurretAnim=FLAKTUR) has its barrel beside it (FLAKBARL).
            else if (image.EndsWith("TUR", StringComparison.OrdinalIgnoreCase) && Voxel(image[..^3] + "BARL") is var (gunBarrel, gunBarrelHva))
                drawn.Add((gunBarrel, gunBarrelHva, turretFacing));
        }
        Image? result = drawn.Count == 0 ? null : Render(drawn, team == 0xFFFFFFFF ? null : ((byte)(team >> 16), (byte)(team >> 8), (byte)team), pitch * PitchStep);
        return voxelImages[key] = result;
    }

    /// <summary>The pitch a voxel picture is drawn at is a multiple of this (radians).</summary>
    public const float PitchStep = MathF.PI / 12;

    Image Render(List<(VoxelModel Model, HvaAnimation? Hva, float Facing)> parts, (byte R, byte G, byte B)? team, float pitch = 0)
    {
        const int Size = 160;
        var image = new Image(Size, Size);
        var depth = new float[Size * Size];
        Array.Fill(depth, float.MinValue);
        var light = Vector3.Normalize(new Vector3(-0.5f, -0.5f, 1f));
        float cosE = MathF.Cos(Elevation), sinE = MathF.Sin(Elevation);
        foreach (var (model, hva, facing) in parts)
        {
            // Facing 0 is north; 32 steps counter-clockwise. Models point along +X.
            float angle = MathF.PI / 2 + facing * MathF.Tau / 32;
            // (Pitching the nose, along +X, up first: a rotation about Y by minus the pitch.)
            var rotate = pitch != 0 ? Matrix4x4.CreateRotationY(-pitch) * Matrix4x4.CreateRotationZ(angle) : Matrix4x4.CreateRotationZ(angle);
            for (int li = 0; li < model.Limbs.Count; li++)
            {
                var limb = model.Limbs[li];
                var occupied = new HashSet<(int, int, int)>(limb.Voxels.Select(v => ((int)v.X, (int)v.Y, (int)v.Z)));
                var size = limb.MaxBounds - limb.MinBounds;
                var perVoxel = new Vector3(size.X / Math.Max(1, limb.SizeX), size.Y / Math.Max(1, limb.SizeY), size.Z / Math.Max(1, limb.SizeZ));
                // The limb's transform: the HVA's limb of the same name (its limbs needn't be in the model's order).
                int hvaLimb = hva == null ? li : Array.FindIndex(hva.Limbs, n => n.Equals(limb.Name, StringComparison.OrdinalIgnoreCase));
                var hvaTransform = hva?.Transform(0, hvaLimb >= 0 ? hvaLimb : li) ?? Matrix4x4.Identity;
                // HVA translations are in the model's scale.
                hvaTransform.Translation *= limb.Scale;
                foreach (var (x, y, z, color, _) in limb.Voxels)
                {
                    var local = limb.MinBounds + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * perVoxel;
                    var world = Vector3.Transform(Vector3.Transform(local, hvaTransform), rotate);
                    // A normal from the empty neighbours (the outward side).
                    var n = Vector3.Zero;
                    foreach (var (dx, dy, dz) in Neighbours)
                        if (!occupied.Contains((x + dx, y + dy, z + dz)))
                            n += new Vector3(dx, dy, dz);
                    n = n == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Vector3.TransformNormal(n, rotate));
                    float shade = 0.45f + 0.75f * Math.Max(0, Vector3.Dot(n, light));
                    // Project: screen x along the world's X, screen y down the tilted view.
                    float sx = Size / 2f + world.X * PixelsPerVoxel;
                    float sy = Size * 0.6f - (world.Y * sinE + world.Z * cosE) * PixelsPerVoxel;
                    float d = world.Z * sinE - world.Y * cosE;
                    var (r, g, b, _) = UnitPalette.Color(color, zeroIsClear: false);
                    if (team is { } t && color >= RemapFirst && color <= RemapLast)
                    {
                        float k = 1 - (color - RemapFirst) / 18f;
                        (r, g, b) = ((byte)(t.R * k), (byte)(t.G * k), (byte)(t.B * k));
                    }
                    byte R = (byte)Math.Clamp(r * shade, 0, 255), G = (byte)Math.Clamp(g * shade, 0, 255), B = (byte)Math.Clamp(b * shade, 0, 255);
                    for (int py = (int)sy - 1; py <= (int)sy + 1; py++)
                        for (int px = (int)sx - 1; px <= (int)sx + 1; px++)
                        {
                            if (px < 0 || py < 0 || px >= Size || py >= Size)
                                continue;
                            int i = py * Size + px;
                            if (d <= depth[i])
                                continue;
                            depth[i] = d;
                            image.SetPixel(px, py, R, G, B, 255);
                        }
                }
            }
        }
        return image;
    }

    static readonly (int, int, int)[] Neighbours = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
}
