using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Emperor: Battle for Dune's houses (mods/zh/rules/emperor) play: the computer builds a base and an army.</summary>
public class EmperorFactionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    /// <summary>A building may stand up to MaxBuildingPlacementTileDist (6 tiles) from the base's, not further.</summary>
    [Fact]
    public void BuildingsMayStandSixTilesFromTheBase()
    {
        if (!content.Available || content.Rules!.Section("PlayerTemplates")["FactionEMAtreides"] == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "Maps/Tournament Desert/Tournament Desert.map");
        var game = GameSetup.FromMap(map, content.Rules!, 3,
        [
            new PlayerSlot("A", "FactionEMAtreides", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("B", "FactionEMHarkonnen", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var a = game.Players.Find("A")!;
        var mcv = game.ObjectsOwnedBy(a).First(o => o.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>() != null);
        Assert.True(mcv.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>()!.Deploy());
        game.Update();
        var yard = game.ObjectsOwnedBy(a).First(o => o.IsKindOf("COMMANDCENTER"));
        var windtrap = game.Templates.Find("EM_ATSmWindtrap")!;
        // The yard's footprint ends 32 units east of it; the windtrap's half width is 12.8; a tile is 12.8.
        System.Numerics.Vector3 East(float d) => (yard.Position + new System.Numerics.Vector3(d, 0, 0)) with { Z = game.Terrain.GroundHeight(yard.Position + new System.Numerics.Vector3(d, 0, 0)) };
        var near = game.Building.CheckLocation(windtrap, East(32 + 12.8f + 5 * 12.8f), 0, a);
        var far = game.Building.CheckLocation(windtrap, East(32 + 12.8f + 9 * 12.8f), 0, a);
        output.WriteLine($"5 tiles off: {near}, 9 tiles off: {far}");
        Assert.Equal(Logic.Construction.BuildLocation.Ok, near);
        Assert.NotEqual(Logic.Construction.BuildLocation.Ok, far);
    }

    [Theory]
    [InlineData("FactionEMAtreides", "FactionEMHarkonnen")]
    [InlineData("FactionEMOrdos", "FactionEMAtreides")]
    public void ComputerBuildsABaseAndAnArmy(string faction, string enemy)
    {
        faction = Environment.GetEnvironmentVariable("EM_FACTION") ?? faction;
        if (!content.Available || content.Rules!.Section("PlayerTemplates")[faction] == null) return;
        string mapName = Environment.GetEnvironmentVariable("EM_MAP") ?? "d2k:TournamentSpice-ver3";
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, mapName);
        var game = GameSetup.FromMap(map, content.Rules!, 11,
        [
            new PlayerSlot("A", faction, IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("B", enemy, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
            // (Someone must be the local human: an observer.)
            new PlayerSlot("Watcher", "FactionObserver", IsHuman: true, StartPosition: 0, Color: 0xFFFFFF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var a = game.Players.Find("A")!;
        foreach (var mcv in game.ObjectsOwnedBy(a).Where(o => o.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>() != null))
        {
            var yard = game.Templates.Find(mcv.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>()!.StructureName)!;
            output.WriteLine($"AI {a.AI?.GetType().Name} B {game.Players.Find("B")!.AI?.GetType().Name}; MCV at {mcv.Position}: deploy {game.Building.CheckLocation(yard, mcv.Position, 0, a, mcv)}; footprint {yard.Rules.GetString("TdFootprint")}");
        }
        int earned = 0;
        game.Events.OnMoneyEarned += (o, amount) => { if (o.Owner == a) earned += amount; };
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("EM_MINUTES"), out int m) ? m : 6;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (60 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: ${a.Money.Amount} (earned {earned}), " +
                    string.Join(" ", game.ObjectsOwnedBy(a).Where(o => o.IsKindOf("HARVESTER")).Select(h => $"[harv {h.AI?.State} {(h.AI as Logic.Modules.Supply.SupplyTruckAIUpdate)?.Gatherer.State} {(h.AI as Logic.Modules.Supply.SupplyTruckAIUpdate)?.Gatherer.Boxes} at {h.Position}]")) + " " +
                    string.Join(", ", game.ObjectsOwnedBy(a).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        }
        var mine = game.ObjectsOwnedBy(a).ToList();
        foreach (var o in mine.Where(o => o.IsKindOf(Kind.Structure)))
            output.WriteLine($"  {o.Template.Name} at {o.Position.X:F0},{o.Position.Y:F0} angle {o.Orientation:F2} footprint {o.Template.Rules.GetString("TdFootprint")}");
        int structures = mine.Count(o => o.IsKindOf(Kind.Structure));
        int army = mine.Count(o => !o.IsKindOf(Kind.Structure) && o.IsKindOf("CAN_ATTACK"));
        output.WriteLine($"end: {structures} structures, {army} armed units: " + string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        Assert.True(structures >= 5, $"{faction} has {structures} structures");
        Assert.True(army >= 3, $"{faction} has {army} armed units");
    }
}
