using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Tiberian Dawn's campaign missions in the Zero Hour engine: they load with their houses, units and triggers, and play.</summary>
public class TdMissionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    public static IEnumerable<object[]> Missions() =>
        Enumerable.Range(1, 15).Select(i => new object[] { $"SCG{i:00}EA" }).Append(["SCB01EA"]);

    [Fact]
    public void CampaignsAreListed()
    {
        if (!content.Available)
            return;
        var campaigns = new OpenWestwood.Client.Shell.CampaignManager(content.Rules!.Section("Campaigns"));
        var td = campaigns.OtherGames.Where(c => OpenWestwood.Client.Shell.CampaignManager.GameName(c) == "Tiberian Dawn").ToList();
        Assert.Equal(["TD_GDI", "TD_NOD"], td.Select(c => c.Name));
        int count = 0;
        for (var m = td[0].NextMission(null); m != null; m = td[0].NextMission(m))
            count++;
        Assert.Equal(15, count);
    }

    [Theory]
    [MemberData(nameof(Missions))]
    public void MissionPlays(string scenario)
    {
        if (!content.Available || OpenWestwood.Client.TiberianDawnContent.Get() == null)
            return;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MISSION_SECONDS"), out int s) ? s : 90;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "td:" + scenario);
        Assert.NotNull(map.TdMission);
        Logic.Missions.TdMission.Trace = Environment.GetEnvironmentVariable("TD_TRACE") == "1";
        var traced = new List<string>();
        var before = Log.Output;
        Log.Output = line => { if (line.Contains("[td]")) traced.Add(line); };
        var game = GameSetup.FromMap(map, content.Rules!, 1, difficulty: Difficulty.Normal, models: content.Models,
            readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        Log.Output = before;
        foreach (string line in traced)
            output.WriteLine(line);
        var mission = game.TdMission!;
        var oldOut = Log.Output;
        Log.Output = line => { if (line.Contains("[td]") || line.Contains("Unknown")) output.WriteLine(line); };
        try
        {
            string Census() => string.Join("; ", game.Players.All.Where(p => !p.IsNeutral).Select(p =>
                $"{p.Name}{(p.IsHuman ? "*" : "")} ${p.Money.Amount}: {game.ObjectsOwnedBy(p).Count(o => o.IsKindOf("TD_OBJECT"))}"));
            output.WriteLine($"{scenario} start: {Census()}; {mission.Triggers.Count} triggers, {mission.TeamTypes.Count} team types");
            Assert.Contains(game.Players.All, p => p.IsHuman);
            int frames = 0;
            for (; frames < seconds * LogicTime.FramesPerSecond && !game.IsGameOver && !mission.Ended; frames++)
                game.Update();
            // (Some missions start with nothing: the player's forces come as reinforcements.)
            Assert.Contains(game.Players.All, p => p.IsHuman && game.ObjectsOwnedBy(p).Any(o => o.IsKindOf("TD_OBJECT")));
            output.WriteLine($"{scenario} after {frames / LogicTime.FramesPerSecond}s: {Census()}; teams {string.Join(",", mission.Teams.Select(t => $"{t.Type}({t.Members.Count}{(t.Moving ? " moving" : "")})"))}; ended {mission.Ended} (win {mission.WinFrame}, lose {mission.LoseFrame})");
        }
        finally
        {
            Log.Output = oldOut;
            Logic.Missions.TdMission.Trace = false;
        }
    }
}
