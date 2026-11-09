using OpenWestwood.Formats.Maps;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Probe (GEN_FACTIONS=1): factions in the Generals and Zero Hour campaign maps that no PlayerTemplate defines.</summary>
public class GeneralsFactionProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Probe()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("GEN_FACTIONS") is not { Length: > 0 })
            return;
        var templates = content.Rules!.Section("PlayerTemplates");
        foreach (string path in content.FileSystem!.List("Maps", ".map", recursive: true).Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"(USA|GLA|CHI|MD_)\w*0\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
        {
            var map = MapFile.Read(content.FileSystem.ReadAllBytes(path));
            var missing = map.Sides.Sides.Select(s => (s.Properties.GetValueOrDefault("playerName")?.ToString(), s.Properties.GetValueOrDefault("playerFaction")?.ToString() ?? "", s.Properties.GetValueOrDefault("playerIsHuman")?.ToString()))
                .Where(s => s.Item2.Length > 0 && templates[s.Item2] == null).ToList();
            output.WriteLine($"{path}: " + string.Join(", ", missing.Select(m => $"{m.Item1}={m.Item2}{(m.Item3 == "True" ? " (HUMAN)" : "")}")));
        }
    }
}
