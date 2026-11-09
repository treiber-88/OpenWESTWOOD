using OpenWestwood.Client.Shell;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>The original Generals campaigns (MORE CAMPAIGNS): complete, with their speech, and every mission plays.</summary>
public class GeneralsCampaignTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void CampaignsAreCompleteWithTheirSpeech()
    {
        if (!content.Available)
            return;
        var campaigns = new CampaignManager(content.Rules!.Section("Campaigns"));
        var generals = campaigns.OtherGames.Where(c => CampaignManager.GameName(c) == "Generals").ToList();
        Assert.Equal(["GENERALS_USA", "GENERALS_GLA", "GENERALS_CHINA"], generals.Select(c => c.Name));
        // Zero Hour's own campaigns are still Zero Hour's.
        campaigns.SetCampaign("USA");
        Assert.Equal("Maps/MD_USA01/MD_USA01.map", campaigns.CurrentMap);
        var speech = content.Rules.Section("DialogEvents");
        foreach (var campaign in generals)
        {
            int count = 0;
            for (var mission = campaign.NextMission(null); mission != null; mission = campaign.NextMission(mission), count++)
            {
                Assert.True(content.FileSystem!.Exists(mission.Map), mission.Map);
                Assert.True(speech[mission.BriefingVoice] != null, $"{campaign.Name} {mission.Name}: no {mission.BriefingVoice}");
                string file = speech[mission.BriefingVoice]!.GetString("Filename");
                Assert.True(content.FileSystem.Exists($"Data/Audio/Speech/English/{file}"), file);
            }
            output.WriteLine($"{campaign.Name}: {count} missions");
            Assert.Equal(7, count);
        }
    }

    public static IEnumerable<object[]> Missions() =>
        new[] { "USA01", "USA02", "USA03", "USA04", "USA06", "USA07", "USA08",
                "GLA01", "GLA02", "GLA04", "GLA05", "GLA06", "GLA07", "GLA08",
                "CHI01", "CHI02", "CHI03", "CHI04", "CHI05", "CHI06", "CHI07" }.Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Missions))]
    public void MissionPlays(string name)
    {
        string map = $"Maps/{name}/{name}.map";
        if (!content.Available || !content.FileSystem!.Exists(map))
            return;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MISSION_SECONDS"), out int s) ? s : 60;
        var game = GameSetup.FromMap(MapFile.Read(content.FileSystem.ReadAllBytes(map)), content.Rules!, 1, difficulty: Difficulty.Normal,
            models: content.Models, readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var spoken = new List<string>();
        game.Events.OnAudioCommand += a => { if (a.Kind is OpenWestwood.Logic.AudioCommandKind.Speech) spoken.Add(a.Name); };
        for (int i = 0; i < seconds * LogicTime.FramesPerSecond && !game.IsGameOver; i++)
            game.Update();
        output.WriteLine($"{name}: {game.Objects.Count()} objects after {seconds}s; spoke {string.Join(", ", spoken.Distinct())}");
        var unknown = spoken.Where(n => content.Rules!.Section("DialogEvents")[n] == null && content.Rules.Section("AudioEvents")[n] == null).Distinct().ToList();
        Assert.True(unknown.Count == 0, "unknown sounds: " + string.Join(", ", unknown));
        Assert.False(game.IsGameOver, "the mission ended by itself");
    }
}
