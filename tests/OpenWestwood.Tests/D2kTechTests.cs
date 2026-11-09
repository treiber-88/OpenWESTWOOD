using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Modules.Production;
using OpenWestwood.Logic.Templates;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Dune 2000's high-tech units become buildable once their buildings and upgrades are there.</summary>
public class D2kTechTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionD2KAtreides", "D2KATR", "D2KATR_MISSILE_TANK")]
    [InlineData("FactionD2KHarkonnen", "D2KHAR", "D2KHAR_MISSILE_TANK")]
    public void TheMissileTankUnlocks(string faction, string prefix, string tank)
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", faction, IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        alice.Money.Deposit(100000);
        var start = alice.StartPosition!.Value;
        int n = 0;
        GameObject? Make(string name)
        {
            var p = start + new Vector3(-200 + 130 * (n % 4), 200 + 130 * (n++ / 4), 0);
            var o = game.CreateObject(name, alice, p with { Z = game.Terrain.GroundHeight(p) });
            game.TreatAsBuilt(o);
            return o;
        }
        foreach (string b in new[] { "WIND_TRAP", "WIND_TRAP", "WIND_TRAP", "WIND_TRAP", "REFINERY", "BARRACKS", "LIGHT_FACTORY", "OUTPOST" })
            Make($"{prefix}_{b}");
        var heavy = Make($"{prefix}_HEAVY_FACTORY")!;
        var template = game.Templates.Find(tank)!;
        string Why() => $"canBuild {alice.CanBuild(template)} prereqs {Prerequisites.AreMet(game, alice, template.Rules)} upgrade {alice.HasUpgrade("Upgrade_D2K_heavy")}";
        output.WriteLine("heavy factory only: " + Why());
        Make($"{prefix}_RESEARCH_CENTRE");
        output.WriteLine("+ research centre: " + Why());
        new ProduceUpgradeCommand(alice.Index, heavy.Id, "Upgrade_D2K_heavy").Execute(game);
        for (int i = 0; i < 60 * LogicTime.FramesPerSecond && !alice.HasUpgrade("Upgrade_D2K_heavy"); i++)
            game.Update();
        output.WriteLine("+ upgrade: " + Why() + $" (queue {string.Join(",", heavy.FindModule<ProductionUpdate>()!.Queue.Select(e => e.Upgrade ?? e.Unit?.Name))})");
        Assert.True(alice.CanBuild(template) && Prerequisites.AreMet(game, alice, template.Rules));
        Assert.Contains(tank, CommandSets.BuildableObjects(game.Catalog, heavy.CommandSetName, "UNIT_BUILD"));
    }
}

/// <summary>Probe: every faction's tech tree played out from its start; what can never be built?</summary>
public class TechTreeProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionD2KAtreides")]
    [InlineData("FactionD2KHarkonnen")]
    [InlineData("FactionD2KOrdos")]
    [InlineData("FactionEMAtreides")]
    [InlineData("FactionEMHarkonnen")]
    [InlineData("FactionEMOrdos")]
    [InlineData("FactionTDGDI")]
    [InlineData("FactionTDNod")]
    public void EverythingIsReachable(string faction)
    {
        if (!content.Available) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", faction, IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        foreach (var mcv in game.ObjectsOwnedBy(alice).ToList())
            mcv.FindModule<Logic.Modules.Behaviors.TdDeployToStructure>()?.Deploy();
        game.Update();
        var start = alice.StartPosition!.Value;
        int n = 0;
        var built = new HashSet<string>();
        var everListed = new HashSet<string>();
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var obj in game.ObjectsOwnedBy(alice).ToList())
            {
                foreach (var (_, button) in CommandSets.Buttons(game.Catalog, obj.CommandSetName))
                {
                    if (button.CommandType is "UNIT_BUILD" or "DOZER_CONSTRUCT")
                        everListed.Add(button.Object);
                    if (button.CommandType == "PLAYER_UPGRADE" && !alice.HasUpgrade(button.Upgrade) && game.Catalog.Upgrade(button.Upgrade) is { } u && Prerequisites.AreMet(game, alice, u))
                    {
                        alice.CompleteUpgrade(button.Upgrade);
                        progress = true;
                    }
                    if (button.CommandType is not ("UNIT_BUILD" or "DOZER_CONSTRUCT") || built.Contains(button.Object))
                        continue;
                    if (game.Templates.Find(button.Object) is not { } t || !alice.CanBuild(t) || !Prerequisites.AreMet(game, alice, t.Rules))
                        continue;
                    built.Add(button.Object);
                    progress = true;
                    if (t.Is("STRUCTURE"))
                    {
                        var p = start + new Vector3(-600 + 140 * (n % 9), 300 + 140 * (n++ / 9), 0);
                        if (game.CreateObject(t, alice, p with { Z = game.Terrain.GroundHeight(p) }) is { } made)
                            game.TreatAsBuilt(made);
                    }
                }
            }
            game.Update();
        }
        var never = everListed.Where(o => !built.Contains(o)).ToList();
        output.WriteLine($"{faction}: built {built.Count}, never buildable: {string.Join(", ", never.Select(o => $"{o} [{Why(o)}]"))}");
        string Why(string o)
        {
            if (game.Templates.Find(o) is not { } t) return "no template";
            var pre = t.Rules.TryGet("Prerequisites", out var node) ? string.Join(" ", node.Entries.Select(e => e.Key + ":" + string.Join("/", e.Value.AsList().Select(v => v.Value)))) : "";
            return $"canBuild {alice.CanBuild(t)} {pre}";
        }
    }
}
