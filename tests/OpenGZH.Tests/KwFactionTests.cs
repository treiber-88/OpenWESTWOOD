using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.AI;
using OpenGZH.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Kane's Wrath's factions (mods/zh/rules/kw, from export-kw-factions) on the Zero Hour engine.</summary>
public class KwFactionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    bool Ready => content.Available && content.Rules!.Section("Objects")["KW_GDIPredator"] != null;

    GameLogic NewMatch(string alice, string bob, bool aliceHuman = true)
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", alice, IsHuman: aliceHuman, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", bob, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    [Fact]
    public void KwFactionsLoadWithTheirModels()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWGDI", "FactionKWNod");
        var predator = game.Templates.Find("KW_GDIPredator")!;
        Assert.Equal("Predator Tank", predator.DisplayName);
        Assert.NotNull(content.Models!.GetFile("KW_GUPredTank_SKN"));
        foreach (string f in new[] { "FactionKWGDI", "FactionKWSteelTalons", "FactionKWZOCOM", "FactionKWNod", "FactionKWBlackHand", "FactionKWMarkedOfKane", "FactionKWScrin", "FactionKWReaper17", "FactionKWTraveler59" })
            Assert.NotNull(content.Rules!.Section("PlayerTemplates")[f]);
        // Every exported model and animation should exist in Kane's Wrath's art.
        string yaml = File.ReadAllText(Path.Combine(OpenGZH.Client.GameContent.FindRoot(), "mods", "zh", "rules", "kw", "objects.yaml"));
        var names = System.Text.RegularExpressions.Regex.Matches(yaml, @"(?:Model|Animation): (KW_\w+)").Select(m => m.Groups[1].Value)
            .Concat(System.Text.RegularExpressions.Regex.Matches(yaml, @"IdleAnimation: \[([^\]]+)\]").SelectMany(m => m.Groups[1].Value.Split(", ")))
            .Distinct().ToList();
        var missing = names.Where(n => content.Models.GetFile(n) == null).ToList();
        output.WriteLine($"{names.Count} models and animations, {missing.Count} missing: {string.Join(", ", missing.Take(30))}");
        Assert.True(missing.Count < names.Count / 20, "nearly every model and animation is found");
    }

    [Fact]
    public void PredatorsAndScorpionsFightItOut()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWGDI", "FactionKWNod");
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var mine = new List<OpenGZH.Logic.Objects.GameObject>();
        var theirs = new List<OpenGZH.Logic.Objects.GameObject>();
        for (int i = 0; i < 3; i++)
        {
            var a = mid + new System.Numerics.Vector3(-150, (i - 1) * 50, 0);
            var b = mid + new System.Numerics.Vector3(150, (i - 1) * 50, 0);
            mine.Add(game.CreateObject("KW_GDIPredator", game.Players.Find("Alice")!, a with { Z = game.Terrain.GroundHeight(a) })!);
            theirs.Add(game.CreateObject("KW_NODScorpionBuggy", game.Players.Find("Bob")!, b with { Z = game.Terrain.GroundHeight(b) })!);
        }
        foreach (var t in theirs)
            t.AI!.AttackMoveTo(mid + new System.Numerics.Vector3(-150, 0, 0), Logic.Commands.CommandSource.AI);
        int frames = 0;
        for (; frames < 90 * LogicTime.FramesPerSecond && mine.Any(o => !o.IsEffectivelyDead) && theirs.Any(o => !o.IsEffectivelyDead); frames++)
            game.Update();
        output.WriteLine($"after {frames / LogicTime.FramesPerSecond}s: Predators left {mine.Count(o => !o.IsEffectivelyDead)}, Scorpions {theirs.Count(o => !o.IsEffectivelyDead)}");
        Assert.True(mine.All(o => o.IsEffectivelyDead) || theirs.All(o => o.IsEffectivelyDead));
    }

    [Theory]
    [InlineData("FactionKWGDI")]
    [InlineData("FactionKWNod")]
    [InlineData("FactionKWScrin")]
    public void KwComputerPlayerBuildsABaseAndAnArmy(string faction)
    {
        if (!Ready) return;
        var game = NewMatch("FactionTDGDI", faction);
        var bob = game.Players.Find("Bob")!;
        Assert.IsType<TdFactionAI>(bob.AI);
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("OPENGZH_AI_MINUTES"), out int m) ? m : 6;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (60 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: Bob ${bob.Money.Amount}, "
                    + string.Join(", ", game.ObjectsOwnedBy(bob).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        }
        var mine = game.ObjectsOwnedBy(bob).ToList();
        output.WriteLine("end: " + string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        Assert.Contains(mine, o => o.IsKindOf("COMMANDCENTER"));
        Assert.True(mine.Count(o => o.IsKindOf(Kind.Structure)) >= 4, "the computer player builds a base");
        Assert.Contains(mine, o => TdFactionAI.TdTypeOf(o.Template) == "STRUCT_REFINERY");
        Assert.True(bob.Score.TotalMoneyEarned > 0, "harvesters bring in money");
        Assert.True(mine.Count(o => o.IsMobile && TdFactionAI.TdTypeOf(o.Template) != "UNIT_HARVESTER") >= 3, "and trains units");
    }
}
