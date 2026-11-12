using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>A Westwood game's vehicle leaves its burnt-out husk where it died (WestwoodHuskDie), which fades away later.</summary>
public class WestwoodHuskTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("TDGDI_MTNK")]
    [InlineData("RASOV_3TNK")]
    [InlineData("D2KATR_COMBAT_TANK_A")]
    [InlineData("RA2SOV_HTNK")]
    [InlineData("TSGDI_MMCH")]
    public void VehicleLeavesItsHusk(string vehicle)
    {
        if (!content.Available || content.Rules!.Section("Objects")[vehicle] == null) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0xFF0000),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Scripts = null;
        var middle = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        middle.Z = game.Terrain.GroundHeight(middle);
        var tank = game.CreateObject(vehicle, game.Players.Find("Alice")!, middle, orientation: 1.2f)!;
        game.Update();
        tank.Kill();
        for (int i = 0; i < 2 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var husk = game.Objects.FirstOrDefault(o => o.Template.Name == vehicle + "_Husk");
        output.WriteLine($"{vehicle}: husk {husk?.Template.Name} at {husk?.Position}, facing {husk?.Orientation:0.00}, owner {husk?.Owner.Name}; tank gone {tank.IsDestroyed}");
        Assert.NotNull(husk);
        Assert.True(MathUtil.Distance2D(husk!.Position, middle) < 1);
        Assert.Equal(1.2f, husk.Orientation, 2);
        Assert.True(husk.Owner.IsNeutral);
        // It goes away after its time (20-25 s, then a couple of seconds fading).
        for (int i = 0; i < 30 * LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.True(husk.IsDestroyed, "the husk went away");
    }
}

/// <summary>Debug: HUSK_TURRET=1 kills tanks until one loses its turret and follows the piece's flight.</summary>
public class HuskTurretProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Flight()
    {
        if (Environment.GetEnvironmentVariable("HUSK_TURRET") != "1" || !content.Available) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0xFF0000),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Scripts = null;
        var middle = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        middle.Z = game.Terrain.GroundHeight(middle);
        for (int n = 0; n < 6; n++)
        {
            var tank = game.CreateObject("RA2ALL_MTNK", game.Players.Find("Alice")!, middle + new Vector3(n * 50, 0, 0))!;
            game.Update();
            tank.Kill();
        }
        for (int i = 0; i < 90; i++)
        {
            game.Update();
            if (i % 5 == 0)
                foreach (var t in game.Objects.Where(o => o.Template.Name.EndsWith("_HuskTurret")))
                    output.WriteLine($"{i,3}: piece #{t.Id} at {t.Position.X:0},{t.Position.Y:0},{t.Position.Z:0.0} ground {game.Terrain.GroundHeight(t.Position):0.0} destroyed {t.IsDestroyed}");
        }
    }
}
