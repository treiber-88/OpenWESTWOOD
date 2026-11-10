using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Red Alert 2's attack dogs run down infantry (and only infantry); terror drones jump into vehicles.</summary>
public class Ra2ParasiteTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    (GameLogic Game, Logic.Players.Player Me, Logic.Players.Player Them, Vector3 Middle) Setup()
    {
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionRA2Russians", IsHuman: true, StartPosition: 1, Color: 0xFF0000),
            new PlayerSlot("Bob", "FactionRA2Americans", IsHuman: true, StartPosition: 2, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Scripts = null;
        game.Shroud.RevealMapForPlayerPermanently(game.Players.Find("Alice")!);
        var middle = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        return (game, game.Players.Find("Alice")!, game.Players.Find("Bob")!, middle with { Z = game.Terrain.GroundHeight(middle) });
    }

    GameObject Make(GameLogic game, string name, Logic.Players.Player owner, Vector3 at) =>
        game.CreateObject(name, owner, at with { Z = game.Terrain.GroundHeight(at) })!;

    [Fact]
    public void DogsRunDownInfantryOnly()
    {
        if (!content.Available || content.Rules!.Section("Objects")["RA2SOV_DOG"] == null) return;
        var (game, me, them, middle) = Setup();
        var dog = Make(game, "RA2SOV_DOG", me, middle);
        var tank = Make(game, "RA2ALL_MTNK", them, middle + new Vector3(60, 0, 0));
        var gis = Enumerable.Range(0, 3).Select(i => Make(game, "RA2ALL_E1", them, middle + new Vector3(250, -40 + 40 * i, 0))).ToList();
        // (The GIs are stunned, so they can't shoot the dog down on its way.)
        foreach (var g in gis)
            g.SetDisabled("DISABLED_PARALYZED");
        tank.SetDisabled("DISABLED_EMP");
        Assert.False(dog.Weapons.All.Any(w => w.Template.CanTarget(tank)), "a dog can't go for a tank");
        Assert.True(dog.Weapons.All.Any(w => w.Template.CanTarget(gis[0])));
        Assert.DoesNotContain(dog.Weapons.All, w => w.Template.Name.Contains("Scanner"));

        new AttackObjectCommand(me.Index, [dog.Id], gis[0].Id).Execute(game);
        float start = MathUtil.Distance2D(dog.Position, gis[0].Position);
        game.Update();
        output.WriteLine($"after the order: state {dog.AI?.State}, target {dog.AI?.CurrentVictim?.Template.Name}, weapons {string.Join(",", dog.Weapons.All.Select(w => w.Template.Name + " range " + w.AttackRange))}, speed {dog.AI?.CurrentLocomotor?.MaxSpeedFor(dog)}, disabled {dog.IsDisabled}");
        bool leapt = false;
        int frame = 0;
        for (; frame < 30 * LogicTime.FramesPerSecond && gis.Any(g => !g.IsEffectivelyDead); frame++)
        {
            game.Update();
            leapt |= game.Parasites.IsBusy(dog);
        }
        output.WriteLine($"dog from {start:0} away: leapt {leapt}; GIs dead {gis.Count(g => g.IsEffectivelyDead)}/3 after {frame / (float)LogicTime.FramesPerSecond:0.0}s; " +
            $"the dog at {MathUtil.Distance2D(dog.Position, middle):0} from where it started, alive {!dog.IsEffectivelyDead}; tank health {tank.Health:0}/{tank.MaxHealth:0}");
        Assert.True(leapt, "it pounced");
        Assert.True(gis[0].IsEffectivelyDead, "the bite killed the soldier");
        Assert.Equal(tank.MaxHealth, tank.Health);
        Assert.False(game.Parasites.IsBusy(dog), "it landed");
    }

    [Fact]
    public void TerrorDronesEatVehiclesFromInside()
    {
        if (!content.Available || content.Rules!.Section("Objects")["RA2SOV_DRON"] == null) return;
        var (game, me, them, middle) = Setup();
        var drone = Make(game, "RA2SOV_DRON", me, middle);
        var gi = Make(game, "RA2ALL_E1", them, middle + new Vector3(40, 40, 0));
        var tank = Make(game, "RA2ALL_MTNK", them, middle + new Vector3(150, 0, 0));
        Assert.False(drone.Weapons.All.Any(w => w.Template.CanTarget(gi)), "a drone can't go for infantry");

        new AttackObjectCommand(me.Index, [drone.Id], tank.Id).Execute(game);
        int inside = -1, frame = 0;
        for (; frame < 120 * LogicTime.FramesPerSecond && !tank.IsEffectivelyDead; frame++)
        {
            game.Update();
            if (inside < 0 && game.Parasites.Host.ContainsKey(drone.Id))
                inside = frame;
        }
        for (int i = 0; i < 10; i++)
            game.Update();
        output.WriteLine($"drone inside the tank at {inside / (float)LogicTime.FramesPerSecond:0.0}s; tank destroyed {tank.IsEffectivelyDead} at {frame / (float)LogicTime.FramesPerSecond:0.0}s; " +
            $"drone out again {!game.Parasites.IsBusy(drone)}, alive {!drone.IsEffectivelyDead}, hidden {drone.IsHiddenInContainer}; GI health {gi.Health:0}/{gi.MaxHealth:0}");
        Assert.True(inside >= 0, "it got inside");
        Assert.True(tank.IsEffectivelyDead, "it ate the tank");
        Assert.False(game.Parasites.IsBusy(drone));
        Assert.False(drone.IsHiddenInContainer);
    }
}
