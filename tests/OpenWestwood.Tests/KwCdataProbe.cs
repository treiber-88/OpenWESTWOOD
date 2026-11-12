using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: KW_CDATA=IndividualImages_044 prints the start of an on-demand texture's data.</summary>
public class KwCdataProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("KW_CDATA") is not { } name || !content.Available) return;
        var kw = content.FileSystem!.Packages.OfType<OpenWestwood.Formats.Sage.SageArtPackage>().First();
        using var s = kw.Open(OpenWestwood.Formats.Sage.SageW3d.TextureFolder + name + ".dds");
        var ms = new MemoryStream(); s.CopyTo(ms); var d = ms.ToArray();
        output.WriteLine($"{d.Length} bytes");
        for (int i = 0; i < 128; i += 32) output.WriteLine(Convert.ToHexString(d[i..(i + 32)]));
        output.WriteLine(new string(d[..128].Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray()));
    }
}
