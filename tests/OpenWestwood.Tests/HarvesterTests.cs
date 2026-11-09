using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.Supply;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>A player's harvesters keep going trip after trip, as long as there is something to fetch.</summary>
public class HarvesterTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    /// <summary>Two players and no computer opponent, so nothing interferes.</summary>
    GameLogic Quiet()
    {
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 1234,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Shroud.RevealMapForPlayerPermanently(game.Players.Find("Alice")!);
        return game;
    }

    (GameObject Dock, Vector3 Home) Site(GameLogic game, Logic.Players.Player player)
    {
        var commandCenter = game.ObjectsOwnedBy(player).First(o => o.IsKindOf("COMMANDCENTER"));
        var dock = game.Objects.Where(o => o.FindModule<SupplyWarehouseDockUpdate>() != null)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, commandCenter.Position)).First();
        var home = dock.Position + Vector3.Normalize(commandCenter.Position - dock.Position) * 140;
        return (dock, home with { Z = game.Terrain.GroundHeight(home) });
    }

    void KeepsGoing(GameLogic game, List<GameObject> gatherers, GameObject? dock, int minutes)
    {
        var player = gatherers[0].Owner;
        var lastDelivery = gatherers.ToDictionary(g => g.Id, _ => 0);
        var lastBoxes = gatherers.ToDictionary(g => g.Id, _ => 0);
        var warehouse = dock?.FindModule<SupplyWarehouseDockUpdate>();
        int Left() => warehouse?.Boxes ?? game.Objects.Sum(o => o.FindModule<SupplyWarehouseDockUpdate>()?.Boxes ?? 0);
        int frames = minutes * 60 * LogicTime.FramesPerSecond;
        for (int i = 0; i < frames; i++)
        {
            game.Update();
            foreach (var g in gatherers)
            {
                int boxes = SupplyGatherer.Of(g)!.Boxes;
                if (boxes < lastBoxes[g.Id])
                    lastDelivery[g.Id] = game.Frame;
                lastBoxes[g.Id] = boxes;
            }
            if (i % (10 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond,4}s ${player.Money.Amount,6} boxes left {Left()}: "
                    + string.Join("  ", gatherers.Select(g => $"{g.Id}:{SupplyGatherer.Of(g)!.State}/{SupplyGatherer.Of(g)!.Boxes}/{(g.AI!.IsIdle ? "idle" : g.AI.IsDocking ? "dock " + g.AI.DockStepName.Split(" goal")[0] + " @" + g.AI.DockTargetId : "busy " + g.AI.State)}{(SupplyGatherer.Of(g)!.State is SupplyGatherer.StateId.Wanting or SupplyGatherer.StateId.Regrouping ? " why: " + SupplyGatherer.Of(g)!.WhyNot(dock ?? g) : "")}")));
            if (Left() == 0 || dock is { IsEffectivelyDead: true })
                return;
        }
        if (Environment.GetEnvironmentVariable("HARVEST_TRACE") != null)
            for (int i = 0; i < 6; i++)
            {
                game.Update();
                foreach (var g in gatherers)
                    output.WriteLine($"  frame {game.Frame} {g.Id}: {SupplyGatherer.Of(g)!.State} ai {g.AI!.State} idle {g.AI.IsIdle} docking {g.AI.IsDocking} pos {g.Position} dest {g.AI.Destination} {g.AI.DockStepName}");
            }
        foreach (var g in gatherers)
            Assert.True(game.Frame - lastDelivery[g.Id] < 90 * LogicTime.FramesPerSecond,
                $"{g.Template.Name} {g.Id} delivered nothing for 90 seconds (state {SupplyGatherer.Of(g)!.State}, ai idle {g.AI!.IsIdle})");
    }

    [Fact]
    public void SupplyTrucksKeepHarvesting()
    {
        if (!content.Available) return;
        var game = Quiet();
        var alice = game.Players.Find("Alice")!;
        var (dock, home) = Site(game, alice);
        game.CreateObject("AmericaSupplyCenter", alice, home);
        var trucks = new List<GameObject>();
        for (int i = 0; i < 4; i++)
        {
            var truck = game.CreateObject("AmericaVehicleChinook", alice, home + new Vector3(80 + 30 * i, 60, 0))!;
            SupplyGatherer.Of(truck)!.ForceWanting = true;
            trucks.Add(truck);
        }
        KeepsGoing(game, trucks, dock, 6);
    }

    [Fact]
    public void ChinaTrucksKeepHarvesting()
    {
        if (!content.Available) return;
        var game = Quiet();
        var alice = game.Players.Find("Alice")!;
        var (dock, home) = Site(game, alice);
        game.CreateObject("ChinaSupplyCenter", alice, home);
        var trucks = new List<GameObject>();
        for (int i = 0; i < 4; i++)
        {
            var truck = game.CreateObject("ChinaVehicleSupplyTruck", alice, home + new Vector3(80 + 30 * i, 60, 0))!;
            SupplyGatherer.Of(truck)!.ForceWanting = true;
            trucks.Add(truck);
        }
        KeepsGoing(game, trucks, dock, 6);
    }

    /// <summary>Dune 2000: a player's harvesters, from the refineries they start with, on spice.</summary>
    [Fact]
    public void DuneHarvestersKeepHarvesting()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "d2k:red-chasm");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionD2KAtreides", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionD2KHarkonnen", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var mcv = game.ObjectsOwnedBy(alice).First();
        var field = game.Objects.Where(o => o.FindModule<SupplyWarehouseDockUpdate>() != null)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, mcv.Position)).First();
        var home = field.Position + Vector3.Normalize(mcv.Position - field.Position) * 140;
        home = Logic.Spatial.PositionFinder.FindPositionAround(game, home with { Z = game.Terrain.GroundHeight(home) }, 0, 200) ?? home;
        game.CreateObject("D2KATR_REFINERY", alice, home);
        var harvesters = new List<GameObject>();
        for (int i = 0; i < 3; i++)
        {
            var spot = Logic.Spatial.PositionFinder.FindPositionAround(game, home + new Vector3(60 + 30 * i, 60, 0), 0, 200) ?? home;
            var h = game.CreateObject("D2KATR_HARVESTER", alice, spot)!;
            SupplyGatherer.Of(h)!.ForceWanting = true;
            harvesters.Add(h);
        }
        KeepsGoing(game, harvesters, null, 6);
    }

    [Fact]
    public void WorkersKeepHarvesting()
    {
        if (!content.Available) return;
        var game = Quiet();
        var alice = game.Players.Find("Alice")!;
        var (dock, home) = Site(game, alice);
        game.CreateObject("GLASupplyStash", alice, home);
        var workers = new List<GameObject>();
        for (int i = 0; i < 5; i++)
            workers.Add(game.CreateObject("GLAInfantryWorker", alice, home + new Vector3(80 + 20 * i, 60, 0))!);
        new DockCommand(alice.Index, workers.Select(w => w.Id).ToArray(), dock.Id).Execute(game);
        KeepsGoing(game, workers, dock, 6);
    }
}
