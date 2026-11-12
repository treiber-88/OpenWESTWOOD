using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: TD_FRAMES="LOGOS" TD_FRAMES_GAME=td|ra TD_FRAMES_OUT=dir writes every frame of a TD/RA shape (raw RGBA).</summary>
public class TdShapeFramesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("TD_FRAMES") is not { } names) return;
        string outDir = Environment.GetEnvironmentVariable("TD_FRAMES_OUT")!;
        var art = Environment.GetEnvironmentVariable("TD_FRAMES_GAME") == "ra" ? OpenWestwood.Client.RedAlertContent.Art! : OpenWestwood.Client.TiberianDawnContent.Art!;
        foreach (string name in names.Split(','))
        {
            if (art.Shape(name) is not { } shape) { output.WriteLine($"{name}: none"); continue; }
            output.WriteLine($"{name}: {shape.FrameCount} frames {shape.Width}x{shape.Height}");
            for (int f = 0; f < Math.Min(shape.FrameCount, 40); f++)
                if (art.Frame(shape, f, null) is { } img)
                    File.WriteAllBytes(Path.Combine(outDir, $"{name}_{f}_{img.Width}x{img.Height}.rgba"), img.Pixels);
        }
    }
}
