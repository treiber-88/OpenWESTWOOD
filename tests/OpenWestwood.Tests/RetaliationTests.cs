using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Formats.Maps;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>A computer player's units shoot back at whoever is shooting them.</summary>
public class RetaliationTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic Game(string aiFaction)
    {
        var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(mapFile, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", aiFaction, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    [Theory]
    [InlineData("FactionGLA", "GLATankScorpion", "AmericaTankCrusader")]
    [InlineData("FactionGLA", "GLAInfantryRebel", "AmericaInfantryRanger")]
    [InlineData("FactionGLA", "GLAInfantryTunnelDefender", "AmericaTankCrusader")]
    [InlineData("FactionChina", "ChinaTankBattleMaster", "AmericaTankCrusader")]
    [InlineData("FactionAmerica", "AmericaTankCrusader", "AmericaTankCrusader")]
    public void TheVictimShootsBack(string faction, string victimType, string attackerType)
    {
        if (!content.Available) return;
        var game = Game(faction);
        game.Scripts = null;
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        // Somewhere in the middle, away from both bases.
        var center = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        center.Z = game.Terrain.GroundHeight(center);
        var victim = game.CreateObject(victimType, bob, center)!;
        for (int i = 0; i < 2 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var attacker = game.CreateObject(attackerType, alice, center)!;
        float range = attacker.Weapons.All.Max(w => w.Template.AttackRange);
        float victimRange = victim.Weapons.All.Select(w => w.Template.AttackRange).DefaultIfEmpty(0).Max();
        // Just inside the attacker's range, and outside the victim's sight if possible (it has to answer the shots).
        var at = center + new Vector3(range - 5, 0, 0);
        attacker.AI!.Teleport(at with { Z = game.Terrain.GroundHeight(at) });
        new AttackObjectCommand(alice.Index, [attacker.Id], victim.Id).Execute(game);
        float victimHealth = victim.Health, attackerHealth = attacker.Health;
        int firstHit = -1, answered = -1;
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond && !victim.IsEffectivelyDead && !attacker.IsEffectivelyDead; i++)
        {
            game.Update();
            if (firstHit < 0 && victim.Health < victimHealth)
                firstHit = game.Frame;
            if (answered < 0 && (attacker.Health < attackerHealth || victim.AI?.CurrentVictim == attacker))
                answered = game.Frame;
            if (answered >= 0 && attacker.Health < attackerHealth)
                break;
        }
        output.WriteLine($"{victimType} (range {victimRange:0}, AI {bob.IsComputer}) vs {attackerType} at {range - 5:0}: first hit {firstHit}, answered {answered}, " +
            $"victim state {victim.AI?.State} target {victim.AI?.CurrentVictim?.Template.Name} health {victim.Health:0}/{victim.MaxHealth:0}, attacker health {attacker.Health:0}/{attacker.MaxHealth:0}");
        Assert.True(answered >= 0, "it shot back (or at least went for the attacker)");
    }
}

/// <summary>A computer player built up for a while, its base attacked: do its units fight the attackers?</summary>
public class AiDefenceTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionGLA")]
    [InlineData("FactionChina")]
    public void TheComputerDefendsItsBase(string faction)
    {
        if (!content.Available || Environment.GetEnvironmentVariable("AI_DEFENCE") == null) return;
        var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", faction, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("AI_DEFENCE"), out int m) ? m : 5;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"after {minutes} min: frame {game.Frame}, game over {game.IsGameOver}, {faction} owns {game.ObjectsOwnedBy(bob).Count()}, Alice owns {game.ObjectsOwnedBy(alice).Count()}");
        var army = game.ObjectsOwnedBy(bob).Where(o => o.IsMobile && o.Weapons.HasAnyWeapon && o.AI != null && !o.IsKindOf("HARVESTER") && !o.IsKindOf("DOZER")).ToList();
        var cc = game.ObjectsOwnedBy(bob).FirstOrDefault(o => o.IsKindOf("COMMANDCENTER")) ?? game.ObjectsOwnedBy(bob).First(o => o.IsKindOf("STRUCTURE"));
        // Attack a building at the edge of the base nearest Alice.
        var aliceCc = game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.IsKindOf("COMMANDCENTER")) ?? game.ObjectsOwnedBy(alice).First();
        var target = game.ObjectsOwnedBy(bob).Where(o => o.IsKindOf("STRUCTURE") && !o.IsKindOf("COMMANDCENTER") && !o.Template.Name.StartsWith("Tech")
            && MathUtil.Distance2D(o.Position, cc.Position) < 500).MinBy(o => MathUtil.Distance2D(o.Position, aliceCc.Position)) ?? cc;
        var dir = Vector3.Normalize((aliceCc.Position - target.Position) with { Z = 0 });
        var tanks = new List<GameObject>();
        for (int i = 0; i < 6; i++)
        {
            var p = target.Position + dir * 200 + new Vector3(-dir.Y, dir.X, 0) * (i - 2.5f) * 25;
            tanks.Add(game.CreateObject("AmericaTankCrusader", alice, p with { Z = game.Terrain.GroundHeight(p) })!);
        }
        new AttackObjectCommand(alice.Index, tanks.Select(t => t.Id).ToArray(), target.Id).Execute(game);
        output.WriteLine($"{faction}: army {army.Count} ({string.Join(", ", army.GroupBy(a => a.Template.Name).Select(g => $"{g.Count()} {g.Key}"))}); attacking {target.Template.Name} {MathUtil.Distance2D(target.Position, cc.Position):0} from their CC");
        var engaged = new HashSet<uint>();
        for (int i = 0; i < 40 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            foreach (var o in game.ObjectsOwnedBy(bob))
                if (o.AI?.CurrentVictim is { } v && v.Owner == alice)
                    engaged.Add(o.Id);
            if (i % (5 * LogicTime.FramesPerSecond) == 0 && Environment.GetEnvironmentVariable("AI_DEFENCE_LOG") != null)
            {
                var near = game.ObjectsOwnedBy(bob).Where(o => o.IsMobile && o.Weapons.HasAnyWeapon && MathUtil.Distance2D(o.Position, target.Position) < 400).ToList();
                output.WriteLine($"  {i / LogicTime.FramesPerSecond}s: tanks alive {tanks.Count(t => !t.IsEffectivelyDead)}, target {target.Health:0}/{target.MaxHealth:0}, {faction} units near {near.Count}, engaged so far {engaged.Count}: " +
                    string.Join(" ", near.Take(12).Select(o => $"{o.Template.Name}[{o.AI?.State}{(o.Team?.Prototype.Name is { } t ? " " + t : "")}]")));
            }
        }
        var stingers = game.ObjectsOwnedBy(bob).Where(o => o.Template.Name.Contains("StingerSoldier")).ToList();
        output.WriteLine($"engaged {engaged.Count}; stinger soldiers {stingers.Count}, of them engaged {stingers.Count(s => engaged.Contains(s.Id))}; tanks alive {tanks.Count(t => !t.IsEffectivelyDead)}");
        Assert.True(engaged.Count > 0, "someone fought back");
    }
}
