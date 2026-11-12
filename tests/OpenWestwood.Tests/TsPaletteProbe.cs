using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: TS_PAL="MMCH,CITY01,TREE01.TEM" TS_PAL_OUT=dir draws frame 0 of each shape with each TS palette (raw RGBA).</summary>
public class TsPaletteProbe(ITestOutputHelper output)
{
    [Fact]
    public void Draw()
    {
        if (Environment.GetEnvironmentVariable("TS_PAL") is not { } names) return;
        string outDir = Environment.GetEnvironmentVariable("TS_PAL_OUT")!;
        var art = OpenWestwood.Client.RedAlert2.Ra2Art.GetTiberianSun()!;
        foreach (string name in names.Split(','))
        {
            var shape = art.Shape(name);
            if (shape == null) { output.WriteLine($"{name}: none"); continue; }
            foreach (string pal in (Environment.GetEnvironmentVariable("TS_PAL_PALS") ?? "UNITTEM.PAL,ISOTEM.PAL,TEMPERAT.PAL,ANIM.PAL,UNITSNO.PAL").Split(','))
            {
                if (art.Palette(pal) is not { } palette) continue;
                var frames = (Environment.GetEnvironmentVariable("TS_PAL_FRAMES") ?? "0").Split(',').Select(int.Parse);
                foreach (int fr in frames)
                {
                    var img = art.Frame(shape, Math.Min(shape.FrameCount - 1, fr), palette, (40, 80, 230));
                    if (img == null) continue;
                    File.WriteAllBytes(Path.Combine(outDir, $"{name}_{pal}_f{fr}_{img.Width}x{img.Height}.rgba"), img.Pixels);
                }
            }
            output.WriteLine($"{name}: {shape.FrameCount} frames");
        }
    }
}

public class TsArtProbe(ITestOutputHelper output)
{
    [Fact]
    public void Sections()
    {
        if (Environment.GetEnvironmentVariable("TS_ART") is not { } names) return;
        var art = OpenWestwood.Client.RedAlert2.Ra2Art.GetTiberianSun()!;
        foreach (string name in names.Split(','))
        {
            output.WriteLine($"[{name}] art: " + string.Join("; ", art.Art.Section(name).Select(kv => $"{kv.Key}={kv.Value}")));
            output.WriteLine($"[{name}] rules: " + string.Join("; ", art.Rules.Section(name).Select(kv => $"{kv.Key}={kv.Value}")));
            if (art.Shape(name) is { } s) output.WriteLine($"  shape {s.Width}x{s.Height}, {s.FrameCount} frames");
        }
    }
}
