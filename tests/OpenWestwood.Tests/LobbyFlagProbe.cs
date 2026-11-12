using OpenWestwood.Formats.Images;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Every faction's LobbyFlag picture can be read from the original files (and, with LOBBY_FLAGS_OUT=dir, each is
/// written out as raw RGBA to look at).</summary>
public class LobbyFlagProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    Image? Load(string name)
    {
        name = name.Split('#')[0];
        string arg = name[(name.IndexOf(':') + 1)..];
        switch (name[..name.IndexOf(':')].ToUpperInvariant())
        {
            case "RA2PCX":
                return OpenWestwood.Client.RedAlert2.Ra2Art.Get() is { } ra2 && ra2.Content.Files.Exists(arg + ".PCX") ? PcxDecoder.Decode(ra2.Content.Files.ReadAllBytes(arg + ".PCX"), (255, 0, 255)) : null;
            case "TDLOGO":
                return OpenWestwood.Client.UI.FactionEmblems.TdLogo(int.Parse(arg));
            case "TSEMBLEM":
                return OpenWestwood.Client.UI.FactionEmblems.TsEmblem(arg);
            case "RASHAPE":
                var parts = arg.Split(':');
                return OpenWestwood.Client.RedAlertContent.Art is { } ra && ra.Shape(parts[0]) is { } shape ? ra.Frame(shape, int.Parse(parts[1]), null) : null;
            case "D2KCHROME":
                var r = arg.Split(',').Select(int.Parse).ToArray();
                return OpenWestwood.Client.Dune2000.D2kArt.Get()?.Chrome(r[0], r[1], r[2], r[3]);
            case "EMCAMEO":
                var em = new OpenWestwood.Formats.Emperor.EmperorArtPackage(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA"));
                string path = OpenWestwood.Formats.Emperor.EmperorArtPackage.Folder + "Textures/" + arg;
                if (!em.Contains(path)) return null;
                using (var s = em.Open(path)) { var ms = new MemoryStream(); s.CopyTo(ms); return Image.Load(ms.ToArray(), path); }
            case "KWCAMEO":
                var kw = content.FileSystem!.Packages.OfType<OpenWestwood.Formats.Sage.SageArtPackage>().FirstOrDefault();
                if (kw?.PackedImage(arg) is not { } packed || !content.FileSystem.Exists(packed.Texture)) return null;
                return Image.Load(content.FileSystem.ReadAllBytes(packed.Texture), packed.Texture);
        }
        return null;
    }

    [Fact]
    public void EveryFlagLoads()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("LOBBY_FLAGS") != "1") return;
        string? outDir = Environment.GetEnvironmentVariable("LOBBY_FLAGS_OUT");
        var missing = new List<string>();
        foreach (var (faction, rules) in content.Rules!.Section("PlayerTemplates").Entries)
        {
            if (rules.GetString("LobbyFlag") is not { Length: > 0 } flag) continue;
            Image? image = null;
            try { image = Load(flag); } catch (Exception e) { output.WriteLine($"{faction}: {e.Message}"); }
            output.WriteLine($"{faction,-28} {flag,-28} {(image == null ? "MISSING" : $"{image.Width}x{image.Height}")}");
            if (image == null) missing.Add(faction);
            else if (outDir != null) File.WriteAllBytes(Path.Combine(outDir, $"{faction}_{image.Width}x{image.Height}.rgba"), image.Pixels);
        }
        Assert.Empty(missing);
    }
}

/// <summary>Debug: TS_RADAR=SIDEC02 TS_RADAR_OUT=dir writes frame 0 of that sidebar archive's RADAR.SHP (raw RGBA).</summary>
public class TsRadarProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("TS_RADAR") is not { } mix) return;
        var art = Environment.GetEnvironmentVariable("TS_RADAR_GAME") == "RA2" ? OpenWestwood.Client.RedAlert2.Ra2Art.Get()! : OpenWestwood.Client.RedAlert2.Ra2Art.GetTiberianSun()!;
        var package = art.Content.Files.Packages.First(p => p.Name.Contains(mix, StringComparison.OrdinalIgnoreCase) && p.Contains("RADAR.SHP"));
        byte[] Read(string name) { using var s = package.Open(name); var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        var shapeName = (Environment.GetEnvironmentVariable("TS_RADAR_SHAPE") ?? "RADAR") + ".SHP";
        var shape = new OpenWestwood.Formats.Westwood.TsShape(Read(shapeName));
        var img = art.Frame(shape, 0, new OpenWestwood.Formats.Westwood.WestwoodPalette(Read("SIDEBAR.PAL")), null)!;
        for (int f = 0; f < shape.FrameCount; f++)
            if (art.Frame(shape, f, new OpenWestwood.Formats.Westwood.WestwoodPalette(Read("SIDEBAR.PAL")), null) is { } fi)
                File.WriteAllBytes(Path.Combine(Environment.GetEnvironmentVariable("TS_RADAR_OUT")!, $"{mix}_{shapeName}_{f}_{fi.Width}x{fi.Height}.rgba"), fi.Pixels);
        output.WriteLine($"{shapeName}: {shape.FrameCount} frames {img.Width}x{img.Height}");
        output.WriteLine($"{package.Name}");
    }
}
