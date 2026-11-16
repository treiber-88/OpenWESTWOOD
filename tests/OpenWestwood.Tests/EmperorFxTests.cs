using Xunit;

namespace OpenWestwood.Tests;

/// <summary>Emperor's particle effects: the effect block in its models, and the systems exported from it.</summary>
public class EmperorFxTests(BaseGameFixture content) : IClassFixture<BaseGameFixture>
{
    static OpenWestwood.Formats.Emperor.EmperorArtPackage? Art()
    {
        string data = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        return Directory.Exists(data) ? new OpenWestwood.Formats.Emperor.EmperorArtPackage(data) : null;
    }

    /// <summary>The shell hit: three particle systems, started and stopped on its nodes over 18 timed events.</summary>
    [Fact]
    public void ShellHitEffectBlockReads()
    {
        using var art = Art();
        if (art?.XbfPath("shellhit") is not { } path) return;
        var xbf = OpenWestwood.Formats.Emperor.XbfFile.Read(art.ReadAllBytes(path));
        var fx = OpenWestwood.Formats.Emperor.XbfFx.Read(xbf.FxData);
        Assert.Equal(3, fx.Systems.Count);
        Assert.Equal(18, fx.Events.Count);
        Assert.Contains(fx.Systems, s => s.Texture == "!%Bru" && s.TextureFrames == 21);
        var start = Assert.Single(fx.Events, e => e.Type == 3 && e.Strings[1] == "?#bigbing~~1");
        Assert.Equal(0, start.Frame);
        Assert.Contains(fx.Events, e => e.Type == 9 && e.Strings[0] == "ShellDetonation");
    }

    /// <summary>A shell's hit puts Emperor's own particle systems on the effect model's bones.</summary>
    [Fact]
    public void ShellHitHasItsParticleSystems()
    {
        if (!content.Available || content.Rules!.Get("Objects", "EM_FX_ShellHit") == null) return;
        Assert.NotNull(content.Rules.Get("ParticleSystems", "EMPS_shellhit_0"));
        string effects = File.ReadAllText(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "mods", "zh", "rules", "emperor", "effects.yaml"));
        var block = System.Text.RegularExpressions.Regex.Match(effects.Replace("\r", ""), @"\n  EM_FX_ShellHit:\n(?:    .*\n)+").Value;
        Assert.Contains("ParticleSysBone:", block);
        Assert.Contains("EMPS_shellhit_", block);
    }
}
