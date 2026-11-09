using System.Numerics;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Weapons that make something to see: the V3's rocket, the Annihilator Tripod's laser.</summary>
public class WestwoodWeaponTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    bool Ready => content.Available && content.Rules!.Section("Objects")["RA2SOV_V3"] != null && content.Rules.Section("Objects")["KW_AlienAnnihilatorTripod"] != null;

    GameLogic NewMatch()
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionRA2Russians", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionKWGDI", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    /// <summary>Fires the shooter at a held tank and counts the objects of a template that appear.</summary>
    int ObjectsMadeWhenFiring(string shooterName, string madeName, float distance)
    {
        var game = NewMatch();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var mid = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var shooter = game.CreateObject(shooterName, alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        var at = mid + new Vector3(distance, 0, 0);
        var target = game.CreateObject("KW_GDIPredator", bob, at with { Z = game.Terrain.GroundHeight(at) })!;
        target.SetDisabled("DISABLED_HELD");
        float before = target.Body!.Health;
        shooter.AI!.AttackObject(target, Logic.Commands.CommandSource.Player);
        var seen = new HashSet<uint>();
        for (int i = 0; i < 12 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            foreach (var o in game.ObjectsInRadius(mid, 2000))
                if (o.Template.Name == madeName)
                    seen.Add(o.Id);
        }
        output.WriteLine($"{shooterName}: {seen.Count} {madeName}; target health {before} -> {(target.IsEffectivelyDead ? 0 : target.Body.Health)}");
        Assert.True(target.IsEffectivelyDead || target.Body.Health < before, "the shots hurt");
        return seen.Count;
    }

    [Fact]
    public void V3LaunchesItsRocket()
    {
        if (!Ready) return;
        Assert.True(ObjectsMadeWhenFiring("RA2SOV_V3", "RA2_Projectile_Rocket_V3ROCKET", 400) > 0);
    }

    [Fact]
    public void AnnihilatorTripodFiresItsLaser()
    {
        if (!Ready) return;
        Assert.True(ObjectsMadeWhenFiring("KW_AlienAnnihilatorTripod", "KW_Laser_AlienAnnihilatorTripod_1", 200) > 0);
    }
}
