using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: EM_FILES=text lists Emperor's files (content/emperor/DATA) whose path contains the text.</summary>
public class EmFileListProbe(ITestOutputHelper output)
{
    [Fact]
    public void List()
    {
        if (Environment.GetEnvironmentVariable("EM_FILES") is not { } text) return;
        var p = new OpenWestwood.Formats.Emperor.EmperorArtPackage(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA"));
        foreach (string f in p.FilePaths.Where(f => text.Split('|').Any(t => f.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(5000))
            output.WriteLine($"EM {f}");
    }
}

/// <summary>Debug: EM_XBF=FRONTEND/SIDEBAR1.XBF prints an Emperor model's nodes (model-space bounds, faces) and textures.</summary>
public class EmXbfDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("EM_XBF") is not { } path) return;
        var p = new OpenWestwood.Formats.Emperor.EmperorArtPackage(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA"));
        using var s = p.Open(OpenWestwood.Formats.Emperor.EmperorArtPackage.Folder + path);
        var ms = new MemoryStream(); s.CopyTo(ms);
        var xbf = OpenWestwood.Formats.Emperor.XbfFile.Read(ms.ToArray());
        output.WriteLine("textures: " + string.Join(", ", xbf.Textures.Select((t, i) => $"{i}:{t}")));
        foreach (var n in xbf.AllNodes)
        {
            var pts = n.Positions.Select(v => System.Numerics.Vector3.Transform(v, n.ModelTransform)).ToArray();
            string box = pts.Length == 0 ? "-" : $"x {pts.Min(v => v.X):0.#}..{pts.Max(v => v.X):0.#} y {pts.Min(v => v.Y):0.#}..{pts.Max(v => v.Y):0.#} z {pts.Min(v => v.Z):0.#}..{pts.Max(v => v.Z):0.#}";
            output.WriteLine($"{n.Name,-32} faces {n.Faces.Length,4} tex [{string.Join(",", n.Faces.Select(f => f.Texture).Distinct())}] {box}");
        }
    }
}

/// <summary>Debug: EM_SAVE=a;b EM_SAVE_OUT=dir copies Emperor files out of the archives.</summary>
public class EmFileSaveProbe
{
    [Fact]
    public void Save()
    {
        if (Environment.GetEnvironmentVariable("EM_SAVE") is not { } names) return;
        string outDir = Environment.GetEnvironmentVariable("EM_SAVE_OUT") ?? Path.GetTempPath();
        var p = new OpenWestwood.Formats.Emperor.EmperorArtPackage(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA"));
        foreach (string n in names.Split(';'))
            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(n)), p.ReadAllBytes(OpenWestwood.Formats.Emperor.EmperorArtPackage.Folder + n));
    }
}

/// <summary>Debug: EM_XBFP=SIDEBAR/SIDEBAR1.XBF prints a model's animations, effect events and nodes (with key/vertex animation).</summary>
public class EmXbfPathProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("EM_XBFP") is not { } path) return;
        var p = new OpenWestwood.Formats.Emperor.EmperorArtPackage(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA"));
        var xbf = OpenWestwood.Formats.Emperor.XbfFile.Read(p.ReadAllBytes(OpenWestwood.Formats.Emperor.EmperorArtPackage.Folder + path));
        output.WriteLine("anims: " + string.Join("; ", xbf.Animations.Select(a => a.Key + "=" + string.Join(",", a.Value.Select(r => $"{r.Start}-{r.End}")))));
        if (Environment.GetEnvironmentVariable("EM_FX_OUT") is { } fxOut) File.WriteAllBytes(fxOut, xbf.FxData);
        var fx = OpenWestwood.Formats.Emperor.XbfFx.Read(xbf.FxData);
        output.WriteLine($"fx systems {fx.Systems.Count} events {fx.Events.Count} frames {fx.Frames}");
        foreach (var e in fx.Events) output.WriteLine($"E f{e.Frame} t{e.Type} c{e.Chance} {string.Join(" / ", e.Strings)}");
        foreach (var n in xbf.AllNodes)
        {
            if (Environment.GetEnvironmentVariable("EM_NODE") is { } want && want.Split(';').Contains(n.Name))
                output.WriteLine($"V {n.Name} T {n.Transform} M {n.ModelTransform} P {string.Join(" ", n.Positions.Select(v => $"({v.X:0.#},{v.Y:0.#},{v.Z:0.#})"))} UV {string.Join(" ", n.Faces.Select(f => $"{f.UvA} {f.UvB} {f.UvC} t{f.Texture}"))}");
            output.WriteLine($"N {n.Name} faces {n.Faces.Length} ka {n.KeyAnimation?.FrameCount} va {n.VertexAnimation?.FrameCount} parent {n.Parent?.Name}");
        }
    }
}

/// <summary>Debug: EM_UIP=SIDEBAR/SIDEBAR1.XBF EM_UIF=0,50 EM_UIN=Radar;Icon00 prints those meshes' screen boxes at those frames
/// (model x, y; the key animations' full matrices).</summary>
public class EmUiPoseProbe(ITestOutputHelper output)
{
    [Fact]
    public void Boxes()
    {
        if (Environment.GetEnvironmentVariable("EM_UIP") is not { } path) return;
        var p = new OpenWestwood.Formats.Emperor.EmperorArtPackage(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA"));
        var xbf = OpenWestwood.Formats.Emperor.XbfFile.Read(p.ReadAllBytes(OpenWestwood.Formats.Emperor.EmperorArtPackage.Folder + path));
        var names = (Environment.GetEnvironmentVariable("EM_UIN") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var fs in (Environment.GetEnvironmentVariable("EM_UIF") ?? "0").Split(','))
        {
            float frame = float.Parse(fs, System.Globalization.CultureInfo.InvariantCulture);
            System.Numerics.Matrix4x4 World(OpenWestwood.Formats.Emperor.XbfNode n)
            {
                var local = n.KeyAnimation is { } ka ? ka.LocalAt(frame, n.Transform) : n.Transform;
                return n.Parent == null ? local : local * World(n.Parent);
            }
            var all = new List<(string, float, float, float, float)>();
            foreach (var n in xbf.AllNodes.Where(n => n.Faces.Length > 0))
            {
                var w = World(n);
                var pts = n.Positions.Select(v => System.Numerics.Vector3.Transform(v, w)).ToArray();
                float x0 = pts.Min(v => v.X), x1 = pts.Max(v => v.X), y0 = pts.Min(v => v.Y), y1 = pts.Max(v => v.Y);
                all.Add((n.Name, x0, x1, y0, y1));
                if (names.Length == 0 || names.Contains(n.Name))
                    output.WriteLine($"F{frame} {n.Name}: x {x0:0}..{x1:0} y {y0:0}..{y1:0} z {pts.Min(v => v.Z):0}..{pts.Max(v => v.Z):0}");
            }
            var vis = all.Where(a => a.Item3 - a.Item2 > 1 && a.Item5 - a.Item4 > 1 && !a.Item1.StartsWith('#')).ToList();
            output.WriteLine($"F{frame} all visible: x {vis.Min(a => a.Item2):0}..{vis.Max(a => a.Item3):0} y {vis.Min(a => a.Item4):0}..{vis.Max(a => a.Item5):0} ({vis.Count} meshes)");
        }
    }
}
