using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Units getting out of buildings and vehicles, and the Stinger Site's soldiers in their pits.</summary>
public class ContainerExitTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    static Vector3 Ground(GameLogic game, Vector3 p) => p with { Z = game.Terrain.GroundHeight(p) };

    Vector3 OpenGround(GameLogic game)
    {
        var alice = game.Players.Find("Alice")!;
        var cc = game.ObjectsOwnedBy(alice).First(o => o.IsKindOf("COMMANDCENTER"));
        return Ground(game, cc.Position + new Vector3(0, 250, 0));
    }

    [Fact]
    public void StingerSoldiersStandInTheirPits()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var site = game.CreateObject("GLAStingerSite", alice, OpenGround(game))!;
        game.TreatAsBuilt(site);
        for (int i = 0; i < 5 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var soldiers = game.ObjectsOwnedBy(alice).Where(o => o.Template.Name == "GLAInfantryStingerSoldier").ToList();
        foreach (var s in soldiers)
            output.WriteLine($"soldier at {MathUtil.Distance2D(s.Position, site.Position):0} from the site's centre (site radius {site.BoundingCircleRadius:0}), held {s.IsDisabledBy("DISABLED_HELD")}");
        Assert.Equal(3, soldiers.Count);
        Assert.All(soldiers, s => Assert.True(MathUtil.Distance2D(s.Position, site.Position) < site.BoundingCircleRadius, "inside the site"));
        Assert.All(soldiers, s => Assert.True(s.IsDisabledBy("DISABLED_HELD")));
        Assert.Equal(3, soldiers.Select(s => (int)s.Position.X * 10000 + (int)s.Position.Y).Distinct().Count());

        // They stay put.
        var before = soldiers.Select(s => s.Position).ToList();
        for (int i = 0; i < 5 * LogicTime.FramesPerSecond; i++)
            game.Update();
        for (int i = 0; i < soldiers.Count; i++)
            Assert.True(MathUtil.Distance2D(before[i], soldiers[i].Position) < 1);
    }

    void SpreadOut(GameLogic game, List<GameObject> units)
    {
        for (int i = 0; i < 8 * LogicTime.FramesPerSecond; i++)
            game.Update();
        float closest = float.MaxValue;
        for (int a = 0; a < units.Count; a++)
            for (int b = a + 1; b < units.Count; b++)
                closest = Math.Min(closest, MathUtil.Distance2D(units[a].Position, units[b].Position));
        output.WriteLine($"out: {units.Count(u => !u.IsContained)}/{units.Count}; closest two {closest:0.0} apart; " +
            string.Join(" ", units.Select(u => $"({u.Position.X:0},{u.Position.Y:0})")));
        Assert.All(units, u => Assert.False(u.IsContained));
        Assert.True(closest >= 6, "they spread out instead of piling onto one spot");
    }

    [Fact]
    public void AGarrisonGetsOutAndSpreadsOut()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var at = OpenGround(game);
        var house = game.CreateObject("GermanTownHouse1", game.Players.All.First(p => p.IsNeutral), at)!;
        var rangers = Enumerable.Range(0, 5).Select(i => game.CreateObject("AmericaInfantryRanger", alice, at + new Vector3(80, i * 12, 0))!).ToList();
        new EnterCommand(alice.Index, rangers.Select(r => r.Id).ToArray(), house.Id).Execute(game);
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond && rangers.Any(r => !r.IsContained); i++)
            game.Update();
        Assert.All(rangers, r => Assert.True(r.IsContained));
        Assert.Equal(alice, house.Owner); // so selecting it shows its exit buttons
        new EvacuateCommand(alice.Index, [house.Id]).Execute(game);
        SpreadOut(game, rangers);
    }

    [Fact]
    public void OnePassengerGetsOutOfAVehicle()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var at = OpenGround(game);
        var humvee = game.CreateObject("AmericaVehicleHumvee", alice, at)!;
        var rangers = Enumerable.Range(0, 4).Select(i => game.CreateObject("AmericaInfantryRanger", alice, at + new Vector3(60, i * 12, 0))!).ToList();
        new EnterCommand(alice.Index, rangers.Select(r => r.Id).ToArray(), humvee.Id).Execute(game);
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond && rangers.Any(r => !r.IsContained); i++)
            game.Update();
        Assert.All(rangers, r => Assert.True(r.IsContained));

        // Clicking one portrait lets out only that one.
        new ExitContainerCommand(alice.Index, rangers[2].Id).Execute(game);
        for (int i = 0; i < 3 * LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.False(rangers[2].IsContained);
        Assert.Equal(3, rangers.Count(r => r.IsContained));

        new EvacuateCommand(alice.Index, [humvee.Id]).Execute(game);
        SpreadOut(game, rangers);
    }
}
