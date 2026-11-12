using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: PCX_SCAN=redalert2 PCX_OUT=dir lists the PCX pictures in a Westwood game's archives (named or not), with
/// their sizes, and writes the small ones out as .pcx files to look at.</summary>
public class PcxScanProbe(ITestOutputHelper output)
{
    [Fact]
    public void Scan()
    {
        if (Environment.GetEnvironmentVariable("PCX_SCAN") is not { } mod) return;
        string outDir = Environment.GetEnvironmentVariable("PCX_OUT")!;
        using var westwood = OpenWestwood.TiberianDawn.TdContent.Load(OpenWestwood.Client.GameContent.FindRoot(), mod);
        foreach (var package in westwood.Files.Packages)
            foreach (string path in package.FilePaths)
            {
                byte[] head;
                try
                {
                    using var s = package.Open(path);
                    head = new byte[12];
                    if (s.Read(head, 0, 12) < 12) continue;
                }
                catch (Exception) { continue; }
                if (head[0] != 0x0A || head[1] > 5 || head[2] != 1) continue;
                int w = BitConverter.ToUInt16(head, 8) - BitConverter.ToUInt16(head, 4) + 1, h = BitConverter.ToUInt16(head, 10) - BitConverter.ToUInt16(head, 6) + 1;
                output.WriteLine($"{package.Name}: {path} {w}x{h}");
                if (w <= 160 && h <= 120)
                {
                    using var s = package.Open(path);
                    using var f = File.Create(Path.Combine(outDir, $"{Path.GetFileName(package.Name)}_{path}.pcx"));
                    s.CopyTo(f);
                }
            }
    }
}
