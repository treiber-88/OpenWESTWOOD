using OpenWestwood.Logic.SoleSurvivor;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Probe (SSR_FIRE=1): the fire FX each of some units' shots sends in Sole Survivor.</summary>
public class SsrFireProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("FactionTDNod", "TDNOD_E5")]
    [InlineData("FactionKWNod", "KW_NODBlackHand")]
    public void FireFxOf(string faction, string unit)
    {
        if (Environment.GetEnvironmentVariable("SSR_FIRE") != "1") return;
        var c = Client.GameContent.Load();
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 1, MapPlayers = 2, UnitType = SsrCategory.Infantry, Faction = faction };
        var (game, _, match) = setup.Build(c);
        var me = match.Players.First(p => !p.IsComputer);
        var them = match.Players.First(p => p.IsComputer);
        them.RespawnFrame = int.MaxValue;
        var soldier = match.Spawn(me, unit)!;
        var at = soldier.Position + new System.Numerics.Vector3(50, 0, 0);
        at.Z = game.Terrain.GroundHeight(at.X, at.Y);
        var enemy = game.CreateObject("AmericaInfantryRanger", them.Player, at)!;
        var fx = new List<string>();
        game.Events.OnWeaponFireFx += f => fx.Add($"frame {game.Frame}: {f.Name} slot {f.Slot}");
        game.Events.OnFx += f => fx.Add($"frame {game.Frame}: OnFx {f.Name}");
        me.AimTarget = enemy.Id;
        me.Aim = enemy.Position;
        me.FirePrimary = true;
        for (int i = 0; i < 300; i++) { game.IsGameOver = false; game.Update(); }
        output.WriteLine($"{unit}: weapon {soldier.Weapons.Slots[0]?.Template.Name}, enemy health {enemy.Health}");
        foreach (var l in fx.Take(30)) output.WriteLine(l);
    }
}
