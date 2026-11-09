using System.Globalization;
using OpenWestwood.Formats.Images;
using OpenWestwood.Formats.OpenRA;
using OpenWestwood.Formats.Westwood;

namespace OpenWestwood.Client.Dune2000;

/// <summary>One animation of a Dune 2000 image (a "sequence"): where its frames are and how they turn and play.</summary>
public sealed class D2kSequence
{
    public required string File;
    public int Start;
    public int Length = 1;
    /// <summary>Facings (negative: stored clockwise).</summary>
    public int Facings = 1;
    public bool Transpose;
    public int[]? Frames;
    /// <summary>Pixels the frame is moved by (y down).</summary>
    public int OffsetX, OffsetY;
    /// <summary>Milliseconds a frame.</summary>
    public int Tick = 40;
    public float Alpha = 1;

    /// <summary>
    /// The frame for a facing and an animation step. <paramref name="ccwFromUp"/> is the direction it faces,
    /// in radians counter-clockwise from screen-up (the rules' facing 0 is north, counting counter-clockwise).
    /// </summary>
    public int FrameIndex(float ccwFromUp, int step)
    {
        int n = Math.Max(1, Math.Abs(Facings));
        int facing = 0;
        if (n > 1)
        {
            float turns = ccwFromUp / (2 * MathF.PI);
            facing = ((int)MathF.Round(turns * n) % n + n) % n;
            if (Facings < 0)
                facing = (n - facing) % n;
        }
        int length = Math.Max(1, Length);
        int frame = ((step % length) + length) % length;
        int index = Transpose ? frame * n + facing : facing * length + frame;
        if (Frames != null && Frames.Length > 0)
            return Frames[Math.Clamp(index, 0, Frames.Length - 1)];
        return Start + index;
    }
}

/// <summary>
/// Dune 2000's art for objects in Zero Hour engine matches: the sprite files in content/dune2000/data
/// (DATA.R16 and the tilesets) and the sequences in content/dune2000/mod/sequences, read once.
/// </summary>
public sealed class D2kArt
{
    readonly string dataFolder;
    readonly Dictionary<string, D2kSpriteFile?> files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<string, D2kSequence>> images = new(StringComparer.OrdinalIgnoreCase);

    D2kArt(string root)
    {
        dataFolder = Path.Combine(root, "content", "dune2000", "data");
        string sequences = Path.Combine(root, "content", "dune2000", "mod", "sequences");
        var parsed = MiniYaml.Resolve(MiniYaml.Combine(Directory.GetFiles(sequences, "*.yaml").Select(f => MiniYaml.Parse(File.ReadAllText(f)))));
        foreach (var (name, node) in parsed)
        {
            var defaults = node.Child("Defaults");
            var seqs = new Dictionary<string, D2kSequence>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in node.Children)
            {
                if (s.Key == "Defaults" || s.Key.StartsWith("Inherits"))
                    continue;
                string Get(string key) => s.Child(key)?.Value is { Length: > 0 } v ? v : defaults?.Get(key) ?? "";
                var seq = new D2kSequence { File = Get("Filename") is { Length: > 0 } f ? f : "DATA.R16" };
                seq.Start = Int(Get("Start"), 0);
                seq.Length = Get("Length") is "*" ? 1 : Int(Get("Length"), 1);
                seq.Facings = Int(Get("Facings"), 1);
                seq.Transpose = Get("Transpose") is "true" or "True";
                seq.Tick = Int(Get("Tick"), 40);
                seq.Alpha = float.TryParse(Get("Alpha"), NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ? a : 1;
                if (Get("Frames") is { Length: > 0 } frames)
                {
                    seq.Frames = frames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => Int(x, 0)).ToArray();
                    if (Get("Length").Length == 0)
                        seq.Length = Math.Max(1, seq.Frames.Length / Math.Max(1, Math.Abs(seq.Facings)));
                }
                if (Get("Offset").Split(',', StringSplitOptions.TrimEntries) is [var ox, var oy, ..])
                {
                    seq.OffsetX = Int(ox, 0);
                    seq.OffsetY = Int(oy, 0);
                }
                seqs[s.Key] = seq;
            }
            images[name] = seqs;
        }
    }

    static int Int(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    static D2kArt? instance;
    static bool tried;
    static readonly object LoadLock = new();

    /// <summary>Whether the Dune 2000 data is installed (content/dune2000).</summary>
    public static D2kArt? Get()
    {
        // Locked: the art may be read ahead on a loading thread while the game asks for it.
        lock (LoadLock)
        {
            if (!tried)
            {
                tried = true;
                string root = GameContent.FindRoot();
                if (File.Exists(Path.Combine(root, "content", "dune2000", "data", "DATA.R16")))
                    instance = new D2kArt(root);
            }
        }
        return instance;
    }

    public D2kSpriteFile? SpriteFile(string name)
    {
        // (Locked: the shell battle's next map may be read on a loading thread while this one draws.)
        lock (files)
            return SpriteFileLocked(name);
    }

    D2kSpriteFile? SpriteFileLocked(string name)
    {
        if (files.TryGetValue(name, out var file))
            return file;
        // The game's DATA folder, else the mod's bits (its extra tiles: customtiles.r16, used by some maps' ground).
        // (Names in any case: Linux and macOS file systems tell "data.r16" from "DATA.R16".)
        string? path = Core.Installs.GameInstalls.FindFile(dataFolder, name)
            ?? Core.Installs.GameInstalls.FindFile(Path.Combine(dataFolder, "..", "mod", "bits"), name);
        file = path != null ? new D2kSpriteFile(System.IO.File.ReadAllBytes(path)) : null;
        return files[name] = file;
    }

    public D2kSequence? Sequence(string image, string sequence) =>
        images.TryGetValue(image, out var seqs) && seqs.TryGetValue(sequence, out var seq) ? seq : null;

    public bool HasImage(string image) => images.ContainsKey(image);

    /// <summary>An image's cameo (its "icon" sequence) for buttons named "D2KCAMEO:image".</summary>
    Image? chrome;
    bool chromeTried;

    /// <summary>A region of the interface art (the d2k mod's uibits/chrome.png: sidebar, house insignia).</summary>
    public Image? Chrome(int x, int y, int w, int h)
    {
        if (!chromeTried)
        {
            chromeTried = true;
            string path = Path.Combine(dataFolder, "..", "mod", "uibits", "chrome.png");
            if (File.Exists(path))
                chrome = Formats.Images.PngDecoder.Decode(File.ReadAllBytes(path));
        }
        if (chrome == null || w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > chrome.Width || y + h > chrome.Height)
            return null;
        var region = new Image(w, h);
        for (int row = 0; row < h; row++)
            Array.Copy(chrome.Pixels, ((y + row) * chrome.Width + x) * 4, region.Pixels, row * w * 4, w * 4);
        return region;
    }

    public Image? Cameo(string image) =>
        (image.Split('/') is [var img, var seq] ? Sequence(img, seq) : Sequence(image, "icon")) is { } icon && SpriteFile(icon.File) is { } file
            ? file.ToImage(icon.FrameIndex(0, 0)) : null; // "image" (its icon sequence) or "image/sequence" (the support power icons)
}
