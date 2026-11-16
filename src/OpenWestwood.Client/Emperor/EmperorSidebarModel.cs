using System.Numerics;
using OpenWestwood.Client.Graphics;
using OpenWestwood.Client.UI;
using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Emperor;

namespace OpenWestwood.Client.Emperor;

/// <summary>
/// Emperor: Battle for Dune's in-game sidebar, as the game has it: a 3D model (SIDEBAR\SIDEBAR1.XBF, scripted by
/// SIDEBAR.TXT) laid out on the interface's plane (its "#screen" node: 600 x 450 units, x right, y up; nearer the viewer
/// is smaller z). Its named meshes are the controls (Radar, glow00..14 the icon slots, the tabs, the buttons, power00..15
/// the power arc, #number00..05 the credits); the key animations open it (frame 0 has it folded away; from frame 1 on it
/// is open, and frames 1..99 are its idle loop, "ActiveEvent Repeat 7").
/// The script's "Event N" plays the model's animation XbfFile.AnimationNames[N - 1] (its frame ranges).
/// </summary>
public sealed class EmperorSidebarModel
{
    public const string ModelPath = EmperorArtPackage.Folder + "SIDEBAR/SIDEBAR1.XBF";

    readonly XbfFile xbf;
    readonly TextureCache textures;
    readonly VirtualFileSystem fileSystem;
    readonly Dictionary<string, XbfNode> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<XbfNode, Matrix4x4> world = new();
    readonly Dictionary<int, Texture?> faceTextures = new();
    float posedFrame = float.NaN;

    EmperorSidebarModel(XbfFile xbf, TextureCache textures, VirtualFileSystem fileSystem)
    {
        this.xbf = xbf;
        this.textures = textures;
        this.fileSystem = fileSystem;
        foreach (var n in xbf.AllNodes)
            byName.TryAdd(n.Name, n);
    }

    public static EmperorSidebarModel? Load(VirtualFileSystem fileSystem, TextureCache textures)
    {
        if (!fileSystem.Exists(ModelPath))
            return null;
        try
        {
            return new EmperorSidebarModel(XbfFile.Read(fileSystem.ReadAllBytes(ModelPath)), textures, fileSystem);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not read Emperor's sidebar: {e.Message}");
            return null;
        }
    }

    public IEnumerable<string> NodeNames => byName.Keys;

    /// <summary>Puts every node where the key animations have it at this frame.</summary>
    public void Pose(float frame)
    {
        if (frame == posedFrame)
            return;
        posedFrame = frame;
        world.Clear();
        foreach (var n in xbf.AllNodes)
            World(n, frame);
    }

    Matrix4x4 World(XbfNode n, float frame)
    {
        if (world.TryGetValue(n, out var done))
            return done;
        var local = n.KeyAnimation is { } ka ? ka.LocalAt(frame, n.Transform) : n.Transform;
        return world[n] = n.Parent == null ? local : local * World(n.Parent, frame);
    }

    /// <summary>A mesh's extent on the interface's plane (x right, y up), at the posed frame; null if it has none.</summary>
    public (Vector2 Min, Vector2 Max)? Box(string name)
    {
        if (!byName.TryGetValue(name, out var n) || n.Positions.Length == 0 || !world.TryGetValue(n, out var m))
            return null;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in n.Positions)
        {
            var w = Vector3.Transform(p, m);
            min = Vector2.Min(min, new Vector2(w.X, w.Y));
            max = Vector2.Max(max, new Vector2(w.X, w.Y));
        }
        return (min, max);
    }

    /// <summary>A node that is only a marker or a hidden helper (Emperor's '#', '~', '?' ... names).</summary>
    public static bool IsHelper(string name) => name.Length == 0 || name[0] is '#' or '~' or '?' or '^' or ':' or '>' || name.Contains('#');

    Texture? FaceTexture(int index)
    {
        if (faceTextures.TryGetValue(index, out var cached))
            return cached;
        string name = index >= 0 && index < xbf.Textures.Count ? xbf.Textures[index] : "";
        return faceTextures[index] = name.Length > 0 && ResolveTexture(name) is { } path ? textures.Get(path, repeat: false) : null;
    }

    /// <summary>As EmperorArtPackage.ResolveTexture: the picture, or another size of it.</summary>
    string? ResolveTexture(string name)
    {
        string folder = EmperorArtPackage.Folder + "Textures/";
        if (fileSystem.Exists(folder + name))
            return folder + name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        var sized = System.Text.RegularExpressions.Regex.Match(stem, "^(.*)_(32|64|128|256)$");
        string bare = sized.Success ? sized.Groups[1].Value : stem;
        foreach (string candidate in new[] { bare, bare + "_256", bare + "_128", bare + "_64", bare + "_32" })
            if (fileSystem.Exists(folder + candidate + ext))
                return folder + candidate + ext;
        return null;
    }

    /// <summary>
    /// How a texture blends, by the marks before its name: '@' see-through (by its alpha), else '!' adds light; two digits
    /// among them are its opacity in tenths ("@!05testback": the sidebar's black backing at half strength, "!06%redglow").
    /// </summary>
    (bool Additive, float Opacity) Blend(int index)
    {
        string name = index >= 0 && index < xbf.Textures.Count ? xbf.Textures[index] : "";
        string marks = new(name.TakeWhile(c => !char.IsLetter(c)).ToArray());
        string digits = new(marks.Where(char.IsDigit).ToArray());
        float opacity = digits.Length >= 2 && int.TryParse(digits[..2], out int tenths) ? Math.Clamp(tenths / 10f, 0, 1) : 1;
        return (!marks.Contains('@') && marks.Contains('!'), opacity);
    }

    /// <summary>
    /// Draws the posed model: the meshes <paramref name="shown"/> lets through, farthest first, each face with its texture
    /// (<paramref name="textureFor"/> may put another in a mesh's place: the house's radar logo).
    /// </summary>
    public void Draw(SpriteBatch batch, Func<Vector3, Vector2> toScreen, Func<string, bool> shown, Func<string, Texture?>? textureFor = null, Func<string, Vector4>? tint = null)
    {
        var drawn = new List<(XbfNode Node, float Depth)>();
        foreach (var n in xbf.AllNodes)
        {
            if (n.Faces.Length == 0 || !world.TryGetValue(n, out var m) || !shown(n.Name))
                continue;
            float depth = 0;
            foreach (var p in n.Positions)
                depth += Vector3.Transform(p, m).Z;
            drawn.Add((n, depth / Math.Max(1, n.Positions.Length)));
        }
        bool additive = false;
        foreach (var (n, _) in drawn.OrderByDescending(d => d.Depth))
        {
            var m = world[n];
            var replaced = textureFor?.Invoke(n.Name);
            var color = tint?.Invoke(n.Name) ?? Vector4.One;
            foreach (var f in n.Faces)
            {
                if ((replaced ?? FaceTexture(f.Texture)) is not { } texture)
                    continue;
                var (add, opacity) = replaced == null ? Blend(f.Texture) : (false, 1f);
                if (add != additive)
                {
                    batch.SetAdditive(add);
                    additive = add;
                }
                Vector2 At(int i) => i >= 0 && i < n.Positions.Length ? toScreen(Vector3.Transform(n.Positions[i], m)) : Vector2.Zero;
                batch.DrawTexturedTriangle(texture, At(f.A), At(f.B), At(f.C), f.UvA, f.UvB, f.UvC, color with { W = color.W * opacity });
            }
        }
        if (additive)
            batch.SetAdditive(false);
    }
}
