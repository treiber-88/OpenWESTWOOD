using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.Production;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>OpenWESTWOOD's production lines: an upgrade is researched beside a unit being built, and a sidebar yard takes
/// a queue of structures, built one after another, each placed in turn.</summary>
public class ProductionLaneTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    (GameLogic Game, Logic.Players.Player Me, Vector3 Middle) Setup(string faction)
    {
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", faction, IsHuman: true, StartPosition: 1, Color: 0xFF0000),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Scripts = null;
        var me = game.Players.Find("Alice")!;
        me.Money.Deposit(50000);
        var at = me.StartPosition!.Value + new Vector3(200, 200, 0);
        return (game, me, at with { Z = game.Terrain.GroundHeight(at) });
    }

    [Fact]
    public void UpgradeIsResearchedWhileAUnitIsBuilt()
    {
        if (!content.Available) return;
        var (game, me, at) = Setup("FactionAmerica");
        var factory = game.CreateObject("AmericaWarFactory", me, at)!;
        game.TreatAsBuilt(factory);
        var production = factory.FindModule<ProductionUpdate>()!;
        Assert.NotNull(production.QueueUnit(game.Templates.Find("AmericaTankCrusader")!));
        Assert.NotNull(production.QueueUpgrade("Upgrade_AmericaTOWMissile"));
        for (int i = 0; i < 2 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var unit = production.Queue.First(e => e.Unit != null);
        var upgrade = production.Queue.First(e => e.Upgrade != null);
        output.WriteLine($"after 2 s: tank {unit.Progress:P0}, upgrade {upgrade.Progress:P0}");
        Assert.True(unit.Progress > 0 && upgrade.Progress > 0, "both are under way");
    }

    [Fact]
    public void YardTakesAQueueOfStructures()
    {
        if (!content.Available || content.Rules!.Section("Objects")["TDGDI_FACT"] == null) return;
        var (game, me, at) = Setup("FactionTDGDI");
        var yard = game.CreateObject("TDGDI_FACT", me, at)!;
        game.TreatAsBuilt(yard);
        var production = yard.FindModule<ProductionUpdate>()!;
        var power = game.Templates.Find("TDGDI_NUKE")!;
        Assert.NotNull(production.QueueUnit(power));
        Assert.NotNull(production.QueueUnit(power));
        int frames = 0;
        while (production.ReadyStructure == null && frames++ < 120 * LogicTime.FramesPerSecond)
            game.Update();
        Assert.NotNull(production.ReadyStructure);
        for (int i = 0; i < 5 * LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.Equal(0f, production.Queue[1].Progress); // the second waits for the first to be placed
        output.WriteLine($"first ready after {frames / (float)LogicTime.FramesPerSecond:0.0}s; the second waits");
    }
}
