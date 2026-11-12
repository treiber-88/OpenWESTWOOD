using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: TD_PALTRY="LOGOS" TD_PALTRY_OUT=dir writes frame indices of a TD shape through every TD palette (raw RGBA).</summary>
public class TdPaletteTryProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("TD_PALTRY") is not { } name) return;
        string outDir = Environment.GetEnvironmentVariable("TD_PALTRY_OUT")!;
        var content = OpenWestwood.Client.TiberianDawnContent.Get()!;
        var shape = OpenWestwood.Client.TiberianDawnContent.Art!.Shape(name)!;
        foreach (string pal in new[] { "SCRSCN1.WSA", "S-GDIIN2.WSA", "SATSEL.PAL" })
        {
            if (!content.Files.Exists(pal)) { output.WriteLine($"{pal}: missing"); continue; }
            var p = content.Files.ReadAllBytes(pal);
            if (pal.EndsWith(".WSA"))
            {
                // TD's WSA: frames, x, y, w, h, delta size, flags (7 x 16 bits), then frames + 2 offsets, then the palette.
                int frames = BitConverter.ToUInt16(p, 0);
                int at = 14 + (frames + 2) * 4;
                output.WriteLine($"{pal}: {frames} frames, flags {BitConverter.ToUInt16(p, 12)}, palette at {at}");
                p = p[at..(at + 768)];
            }
            for (int f = 0; f < shape.FrameCount; f++)
            {
                var idx = shape.Frame(f);
                var rgba = new byte[idx.Length * 4];
                for (int i = 0; i < idx.Length; i++)
                {
                    int c = idx[i];
                    rgba[i * 4] = (byte)(p[c * 3] * 4); rgba[i * 4 + 1] = (byte)(p[c * 3 + 1] * 4); rgba[i * 4 + 2] = (byte)(p[c * 3 + 2] * 4); rgba[i * 4 + 3] = (byte)(c == 0 ? 0 : 255);
                }
                File.WriteAllBytes(Path.Combine(outDir, $"{pal}_{f}_{shape.Width}x{shape.Height}.rgba"), rgba);
            }
        }
        output.WriteLine("done");
    }
}
