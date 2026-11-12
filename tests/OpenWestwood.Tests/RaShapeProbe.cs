using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: RA_SHAPE="FTUR,WEAP" RA_SHAPE_OUT=dir writes frame 0 of each Red Alert shape (raw RGBA) and its size.</summary>
public class RaShapeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Draw()
    {
        if (Environment.GetEnvironmentVariable("RA_SHAPE") is not { } names) return;
        string outDir = Environment.GetEnvironmentVariable("RA_SHAPE_OUT")!;
        var art = OpenWestwood.Client.RedAlertContent.Art!;
        output.WriteLine($"theater extension {art.TheaterExtension}");
        foreach (string name in names.Split(','))
        {
            var shape = art.Shape(name);
            if (shape == null) { output.WriteLine($"{name}: none"); continue; }
            var img = art.Frame(shape, 0, (40, 80, 230));
            output.WriteLine($"{name}: {shape.FrameCount} frames {shape.Width}x{shape.Height}");
            if (img != null)
                File.WriteAllBytes(Path.Combine(outDir, $"{name}_{img.Width}x{img.Height}.rgba"), img.Pixels);
        }
    }
}
