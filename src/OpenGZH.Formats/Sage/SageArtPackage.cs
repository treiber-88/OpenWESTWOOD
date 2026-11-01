using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.W3d;

namespace OpenGZH.Formats.Sage;

/// <summary>
/// Kane's Wrath's compiled art as a package: its textures as "kw/&lt;name&gt;.dds" files, and its W3D containers as
/// models named "KW_&lt;container&gt;". The stream's data (hundreds of megabytes) load the first time they are needed.
/// </summary>
public sealed class SageArtPackage : IPackage, IW3dProvider
{
    readonly BigArchive archive;
    readonly string streamName;
    readonly Lazy<SageStream> stream;
    readonly HashSet<string> paths = new(PathUtil.Comparer);
    readonly Dictionary<string, string> textureOf = new(PathUtil.Comparer);

    public string Name { get; }

    public SageArtPackage(string bigPath, string streamName = "static_common")
    {
        Name = $"{bigPath}>{streamName}";
        archive = new BigArchive(bigPath);
        this.streamName = streamName;
        var manifest = new SageManifest(Read($"data/{streamName}.manifest"));
        foreach (var a in manifest.Assets.Where(a => a.TypeName == "Texture"))
        {
            string path = PathUtil.Normalize(SageW3d.TextureFolder + a.InstanceName + ".dds");
            paths.Add(path);
            textureOf[path] = a.InstanceName;
        }
        stream = new Lazy<SageStream>(() => SageStream.Load(Read, streamName));
    }

    byte[] Read(string name)
    {
        using var s = archive.Open(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public SageStream Stream => stream.Value;

    public IEnumerable<string> FilePaths => paths;

    public bool Contains(string path) => paths.Contains(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (textureOf.TryGetValue(PathUtil.Normalize(path), out var name) && Stream.Find("Texture", name) is { } asset
            && SageW3d.TextureDds(Stream, asset) is { } dds)
            return new MemoryStream(dds, writable: false);
        throw new FileNotFoundException($"{path} is not in {Name}.");
    }

    readonly Dictionary<string, W3dFile?> models = new(StringComparer.OrdinalIgnoreCase);

    public W3dFile? GetW3d(string name)
    {
        if (!name.StartsWith(SageW3d.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        lock (models)
        {
            if (!models.TryGetValue(name, out var file))
                models[name] = file = SageW3d.Model(Stream, name[SageW3d.ModelPrefix.Length..]);
            return file;
        }
    }

    /// <summary>
    /// A packed interface image (a unit's portrait "Portrait_GDIPredatorTank"...): its texture page file and its rectangle
    /// on it. Layout: +4 the page (import), +8 the page's width and height, +16 left, top, right, bottom.
    /// </summary>
    public (string Texture, int Left, int Top, int Right, int Bottom, int Width, int Height)? PackedImage(string name)
    {
        if (Stream.Find("PackedTextureImage", name) is not { } asset)
            return null;
        var d = Stream.Data(asset).ToArray();
        if (d.Length < 32 || Stream.Reference(asset, (int)BitConverter.ToUInt32(d, 4)) is not { TypeName: "Texture" } page)
            return null;
        int I(int at) => BitConverter.ToInt32(d, at);
        return (SageW3d.TextureFolder + page.InstanceName + ".dds", I(16), I(20), I(24), I(28), Math.Max(1, I(8)), Math.Max(1, I(12)));
    }

    public void Dispose() => archive.Dispose();

    /// <summary>
    /// Mounts Kane's Wrath's art (content/kaneswrath/Core/1.0/StaticStream.big) after everything else, if it is there
    /// (its names are prefixed, so nothing clashes).
    /// </summary>
    public static void MountIfPresent(VirtualFileSystem fileSystem, string gameRoot)
    {
        string kw = Path.Combine(gameRoot, "content", "kaneswrath", "Core", "1.0", "StaticStream.big");
        if (!File.Exists(kw))
            return;
        try
        {
            fileSystem.Mount(new SageArtPackage(kw));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not mount {kw}: {e.Message}");
        }
    }
}
