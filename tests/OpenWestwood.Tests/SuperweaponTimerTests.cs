using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.SpecialPowers;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>A superweapon charges from the moment its building is finished, and can't be fired before.</summary>
public class SuperweaponTimerTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("GLAScudStorm", "SuperweaponScudStorm")]
    [InlineData("ChinaNuclearMissileLauncher", "SuperweaponNeutronMissile")]
    public void ChargesFromWhenItIsBuilt(string building, string powerName)
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!;
        var commandCenter = game.ObjectsOwnedBy(alice).First(o => o.IsKindOf("COMMANDCENTER"));
        var at = commandCenter.Position + new Vector3(200, 0, 0);
        var silo = game.CreateObject(game.Templates.Find(building)!, alice, at with { Z = game.Terrain.GroundHeight(at) }, underConstruction: true);
        for (int i = 0; i < 30; i++)
            game.Update();
        var power = game.Catalog.SpecialPower(powerName)!;
        var module = SpecialPowerRules.ModuleFor(silo, power)!;
        output.WriteLine($"under construction: ready {module.IsReady} {module.PercentReady:P0}");

        game.CompleteConstruction(silo);
        int built = game.Frame;
        game.Update();
        output.WriteLine($"built: ready {module.IsReady} {module.PercentReady:P1}, ready frame {module.ReadyFrame} (now {game.Frame}, reload {power.ReloadFrames})");
        Assert.False(module.IsReady, "a new superweapon has to charge first");
        // (Low power pauses the countdown, which pushes it back.)
        Assert.True(module.ReadyFrame >= built + power.ReloadFrames);
        int readyFrame = module.ReadyFrame;

        // Fired too early: nothing happens.
        var target = game.ObjectsOwnedBy(game.Players.Find("Bob")!).First(o => o.IsKindOf("COMMANDCENTER")).Position;
        int objects = game.Objects.Count();
        new DoSpecialPowerCommand(alice.Index, powerName, silo.Id, [], Position: target).Execute(game);
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.False(module.IsReady);
        Assert.True(module.ReadyFrame >= readyFrame, "firing too early doesn't use up the charge");

        // Charged: it fires, and starts charging again.
        alice.Money.Deposit(100000);
        for (int i = 0; i < 2; i++)
            game.CreateObject("AmericaPowerPlant", alice, commandCenter.Position + new Vector3(-150, 120 * i - 60, 0));
        while (!module.IsReady)
            game.Update();
        new DoSpecialPowerCommand(alice.Index, powerName, silo.Id, [], Position: target).Execute(game);
        game.Update();
        output.WriteLine($"fired at {game.Frame}: ready {module.IsReady}, ready frame {module.ReadyFrame}");
        Assert.False(module.IsReady);
        Assert.True(module.ReadyFrame > game.Frame + power.ReloadFrames / 2);
    }
}
