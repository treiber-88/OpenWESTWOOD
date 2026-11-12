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
