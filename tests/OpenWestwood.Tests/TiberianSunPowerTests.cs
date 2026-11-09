using System.Numerics;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Tiberian Sun's EMP Cannon, Hunter-Seeker and Firestorm (mods/zh/rules/ts, from export-ts-factions).</summary>
public class TiberianSunPowerTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    bool Ready => content.Available && content.Rules!.Section("Objects")["TSGDI_GAFIRE"] != null;

    GameLogic NewMatch()
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionTSGDI", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionTSNod", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    static GameObject Put(GameLogic game, string name, string player, Vector3 at) =>
        game.CreateObject(name, game.Players.Find(player)!, at with { Z = game.Terrain.GroundHeight(at) })!;

    static Vector3 Mid(GameLogic game) => new(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);

    static Ra2Superweapon Power(GameObject building) => building.FindModules<Ra2Superweapon>().Single();

    static void Run(GameLogic game, float seconds)
    {
        for (int i = 0; i < seconds * LogicTime.FramesPerSecond; i++)
            game.Update();
    }

    [Fact]
    public void EmpCannonKnocksOutVehiclesAndBuildingsButNotInfantry()
    {
        if (!Ready) return;
        var game = NewMatch();
        var mid = Mid(game);
        var cannon = Put(game, "TSGDI_NAPULS", "Alice", mid + new Vector3(-600, 0, 0));
        Assert.Null(cannon.AI); // (it no longer shoots by itself)
        var tank = Put(game, "TSNOD_TTNK", "Bob", mid);
        var soldier = Put(game, "TSNOD_E1", "Bob", mid + new Vector3(30, 30, 0));
        var hand = Put(game, "TSNOD_NAHAND", "Bob", mid + new Vector3(100, -100, 0));
        float health = tank.Health;
        Power(cannon).DoSpecialPowerAtLocation(mid, float.NaN, 0);
        game.Update();
        Assert.True(tank.IsDisabledBy("DISABLED_EMP"));
        Assert.True(hand.IsDisabledBy("DISABLED_EMP"));
        Assert.False(soldier.IsDisabled);
        Assert.Equal(health, tank.Health);
        Run(game, 81);
        Assert.False(tank.IsDisabledBy("DISABLED_EMP"), "it wears off");
    }

    [Fact]
    public void HunterSeekerFindsAndDestroysATarget()
    {
        if (!Ready) return;
        var game = NewMatch();
        var mid = Mid(game);
        var temple = Put(game, "TSNOD_NATMPL", "Bob", mid + new Vector3(-500, 0, 0));
        var factory = Put(game, "TSGDI_GAWEAP", "Alice", mid + new Vector3(500, 200, 0));
        Power(temple).DoSpecialPower(0);
        var droid = game.Objects.Single(o => o.Template.Name == "TSNOD_NHUNTER");
        Assert.Equal(factory.Id, droid.FindModule<TsHunterSeekerUpdate>()!.TargetId);
        int frames = 0;
        for (; frames < 60 * LogicTime.FramesPerSecond && !factory.IsEffectivelyDead; frames++)
            game.Update();
        output.WriteLine($"factory destroyed after {frames / (float)LogicTime.FramesPerSecond:0.0}s");
        Assert.True(factory.IsEffectivelyDead);
        Assert.True(droid.IsEffectivelyDead || droid.IsDestroyed);
    }

    [Fact]
    public void FirestormWallBurnsWhatCrossesAndShieldsItself()
    {
        if (!Ready) return;
        var game = NewMatch();
        var mid = Mid(game);
        var generator = Put(game, "TSGDI_GAFIRE", "Alice", mid + new Vector3(-400, 0, 0));
        var wall = Put(game, "TSGDI_GAFSDF", "Alice", mid);
        var buggy = Put(game, "TSNOD_BGGY", "Bob", mid + new Vector3(200, 0, 0));
        float wallHealth = wall.Health, generatorHealth = generator.Health;

        // Not burning: a hit hurts the wall.
        wall.Damage(new Logic.Combat.DamageInfo { Amount = 50, DamageType = "EXPLOSION" });
        Assert.True(wall.Health < wallHealth);
        wallHealth = wall.Health;

        Power(generator).DoSpecialPower(0);
        game.Update();
        Assert.True(wall.FindModule<TsFirestormWall>()!.IsBurning);
        wall.Damage(new Logic.Combat.DamageInfo { Amount = 100, DamageType = "EXPLOSION" });
        Assert.Equal(wallHealth, wall.Health);
        Assert.True(generator.Health < generatorHealth, "the generator takes a share");

        // Something in the firestorm burns.
        buggy.SetPosition(wall.Position);
        Run(game, 1);
        Assert.True(buggy.IsEffectivelyDead);

        // It goes out once the charge is spent.
        Run(game, 61);
        Assert.False(wall.FindModule<TsFirestormWall>()!.IsBurning);
    }
}
