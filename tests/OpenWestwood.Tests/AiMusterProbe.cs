using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: AI_MUSTER=Faction plays its computer player 7 minutes against an idle one and reports where its army
/// stands (from its base centre and toward the enemy) and how far apart its units are.</summary>
public class AiMusterProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Watch()
    {
        if (Environment.GetEnvironmentVariable("AI_MUSTER") is not { } faction || !content.Available) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 5,
        [
            new PlayerSlot("Idle", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", faction, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        for (int f = 0; f < 7 * 60 * LogicTime.FramesPerSecond; f++)
        {
            game.IsGameOver = false;
            game.Update();
        }
        var mine = game.ObjectsOwnedBy(bob).Where(o => !o.IsEffectivelyDead).ToList();
        var buildings = mine.Where(o => o.IsKindOf(Kind.Structure)).ToList();
        var center = new Vector3(buildings.Average(o => o.Position.X), buildings.Average(o => o.Position.Y), 0);
        var toward = Vector3.Normalize((game.Players.Find("Idle")!.StartPosition!.Value - center) with { Z = 0 });
        var army = mine.Where(o => o.IsMobile && o.Template.Rules.TryGet("WeaponSet", out _) && !o.IsKindOf(Kind.Harvester)).ToList();
        foreach (var u in army)
        {
            var d = (u.Position - center) with { Z = 0 };
            float nearest = army.Where(o => o != u).Select(o => MathUtil.Distance2D(o.Position, u.Position)).DefaultIfEmpty(0).Min();
            output.WriteLine($"{u.Template.Name,-22} ahead {Vector3.Dot(d, toward),6:0} side {Vector3.Dot(d, new Vector3(-toward.Y, toward.X, 0)),6:0} nearest {nearest,5:0} idle {u.AI?.IsIdle}");
        }
        float minGap = buildings.SelectMany(a => buildings.Where(b => b != a).Select(b => MathUtil.Distance2D(a.Position, b.Position) - a.Radius - b.Radius)).DefaultIfEmpty(0).Min();
        output.WriteLine($"{buildings.Count} buildings, closest pair gap {minGap:0}");
    }
}
