using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: KW_APT=filter lists Kane's Wrath's Apt.big files (its Flash-like interface) whose names contain it.</summary>
public class KwAptProbe(ITestOutputHelper output)
{
    [Fact]
    public void List()
    {
        if (Environment.GetEnvironmentVariable("KW_APT") is not { } filter) return;
        string path = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath", "Core", "1.0", "Apt.big");
        using var big = new OpenWestwood.Core.FileSystem.BigArchive(path);
        foreach (var f in big.FilePaths.Where(f => filter == "*" || filter.Split('|').Any(t => f.Contains(t, StringComparison.OrdinalIgnoreCase))))
            output.WriteLine("APT " + f);
    }
}

/// <summary>Debug: KW_APTS=tacticalhud lists an Apt.big stream's assets (type:name, data size, references).</summary>
public class KwAptStreamProbe(ITestOutputHelper output)
{
    [Fact]
    public void Assets()
    {
        if (Environment.GetEnvironmentVariable("KW_APTS") is not { } name) return;
        string path = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath", "Core", "1.0", "Apt.big");
        using var big = new OpenWestwood.Core.FileSystem.BigArchive(path);
        byte[] Read(string p) { using var s = big.Open(p); var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        var stream = OpenWestwood.Formats.Sage.SageStream.Load(Read, "aptui/" + name);
        foreach (var a in stream.Manifest.Assets)
        {
            output.WriteLine($"A {a.Name} {a.InstanceDataSize} bytes, refs {a.References.Length}, src {a.SourceFile}");
            if (Environment.GetEnvironmentVariable("KW_APTS_OUT") is { } outDir && a.InstanceDataSize < 500000)
                File.WriteAllBytes(Path.Combine(outDir, a.Name.Replace(':', '_') + ".bin"), stream.Data(a).ToArray());
        }
    }
}

/// <summary>Debug: KW_APTM=tacticalhud reads a movie (AptMovie) and prints its size, characters and frame 0's display list.</summary>
public class KwAptMovieProbe(ITestOutputHelper output)
{
    [Fact]
    public void Movie()
    {
        if (Environment.GetEnvironmentVariable("KW_APTM") is not { } names) return;
        string path = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath", "Core", "1.0", "Apt.big");
        using var big = new OpenWestwood.Core.FileSystem.BigArchive(path);
        byte[] Read(string p) { using var s = big.Open(p); var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        foreach (string name in names.Split(','))
        {
            var apt = OpenWestwood.Formats.Sage.AptMovie.Read(OpenWestwood.Formats.Sage.SageStream.Load(Read, "aptui/" + name));
            var m = apt.Movie;
            output.WriteLine($"M {apt.Name} {m.Width}x{m.Height} {m.MillisecondsPerFrame}ms frames {m.Frames.Count} chars {m.Characters.Count} [{string.Join(",", m.Characters.Select(c => c?.Type.ToString() ?? "-").GroupBy(t => t).Select(g => g.Key + "=" + g.Count()))}] textures {string.Join(",", apt.Textures.Keys)} images {string.Join(",", apt.ImageTextures)} geoms {apt.Geometries.Count}");
            output.WriteLine($"  imports {string.Join("; ", m.Imports.Select(i => $"{i.Character}={i.Movie}:{i.Name}"))}");
            output.WriteLine($"  exports {string.Join("; ", m.Exports.Select(e => $"{e.Name}={e.Character}"))}");
            if (Environment.GetEnvironmentVariable("KW_APTC") is { } chars)
                foreach (var ci in chars.Split(';').Select(int.Parse))
                    if (m.Characters[ci] is OpenWestwood.Formats.Sage.AptMovie.Playable pl)
                        for (int f = 0; f < pl.Frames.Count; f++)
                        {
                            output.WriteLine($"  C{ci} F{f} labels [{string.Join(",", pl.Frames[f].Labels)}] removes [{string.Join(",", pl.Frames[f].Removes)}]");
                            foreach (var p in pl.Frames[f].Places)
                                output.WriteLine($"    d{p.Depth} c{p.Character} {(p.Character >= 0 && p.Character < m.Characters.Count ? m.Characters[p.Character]?.Type.ToString() : "?")} '{p.Name}' move {p.IsMove} t {p.Matrix?.M31:0.#},{p.Matrix?.M32:0.#} s {p.Matrix?.M11:0.##},{p.Matrix?.M22:0.##} tint {p.Tint} add {p.Add}");
                        }
            for (int f = 0; f < Math.Min(m.Frames.Count, 3); f++)
                foreach (var p in m.Frames[f].Places)
                    output.WriteLine($"  F{f} d{p.Depth} c{p.Character} {(p.Character < m.Characters.Count ? m.Characters[p.Character]?.Type.ToString() : "?")} '{p.Name}' m {p.Matrix} tint {p.Tint}");
        }
    }
}
