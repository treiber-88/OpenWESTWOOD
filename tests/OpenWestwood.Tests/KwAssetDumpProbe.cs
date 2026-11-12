using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: KW_ASSET="OnDemandTextureImage:GDILogo" prints the asset's data bytes and the readable text in them.</summary>
public class KwAssetDumpProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("KW_ASSET") is not { } spec || !content.Available) return;
        var (type, name) = (spec.Split(':')[0], spec.Split(':')[1]);
        foreach (var kw in content.FileSystem!.Packages.OfType<OpenWestwood.Formats.Sage.SageArtPackage>())
        {
            if (kw.Stream.Find(type, name) is not { } asset) continue;
            var d = kw.Stream.Data(asset).ToArray();
            output.WriteLine($"{d.Length} bytes: {Convert.ToHexString(d[..Math.Min(64, d.Length)])}");
            output.WriteLine(new string(d.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray()));
            for (int i = 0; i < 4; i++)
                try { output.WriteLine($"ref {i}: {kw.Stream.Reference(asset, i)?.TypeName}:{kw.Stream.Reference(asset, i)?.InstanceName}"); } catch (Exception e) { output.WriteLine($"ref {i}: {e.Message}"); }
        }
    }
}
