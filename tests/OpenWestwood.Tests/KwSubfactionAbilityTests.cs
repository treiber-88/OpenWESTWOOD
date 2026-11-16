using System.Numerics;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Modules.SpecialPowers;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Kane's Wrath's sub-factions' own unit abilities (exported by export-kw-factions).</summary>
public class KwSubfactionAbilityTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    bool Ready => content.Available && content.Rules!.Section("Objects")["KW_SteelTalonsTitan"] != null;

    GameLogic NewMatch(string alice, string bob)
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", alice, IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", bob, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Scripts = null;
        return game;
    }

    static GameObject Place(GameLogic game, string template, string player, Vector3 at) =>
        game.CreateObject(template, game.Players.Find(player)!, at with { Z = game.Terrain.GroundHeight(at) })!;

    static SpecialPowerModule Power(GameObject unit, string contains) =>
        unit.FindModules<SpecialPowerModule>().First(m => m.Power?.Name.Contains(contains, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Marked of Kane's Awakened: their EMP blast stops an enemy vehicle where it stands.</summary>
    [Fact]
    public void AwakenedEmpBlastDisablesAVehicle()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWMarkedOfKane", "FactionKWGDI");
        var mid = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        string awakened = content.Rules!.Section("Objects").Keys.First(k => k.StartsWith("KW_MOKCyborgInfantry", StringComparison.OrdinalIgnoreCase));
        var cyborg = Place(game, awakened, "Alice", mid);
        var tank = Place(game, "KW_GDIPredator", "Bob", mid + new Vector3(150, 0, 0));
        var emp = Power(cyborg, "EMPBlast");
        emp.DoSpecialPowerAtObject(tank, 0);
        bool disabled = false;
        for (int f = 0; f < 300 && !disabled; f++)
        {
            game.Update();
            disabled = tank.IsDisabledBy("DISABLED_EMP");
        }
        output.WriteLine($"{awakened} -> predator disabled: {disabled} at frame {game.Frame}");
        Assert.True(disabled, "the EMP blast disables the Predator");
    }

    /// <summary>Steel Talons' Titan: Adaptive Armor (after its upgrade) makes it take less damage for a while.</summary>
    [Fact]
    public void TitanAdaptiveArmorCutsDamageForAWhile()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWSteelTalons", "FactionKWNod");
        var mid = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var titan = Place(game, "KW_SteelTalonsTitan", "Alice", mid);
        Assert.NotNull(titan.FindModule<TimedAttributeModifierUpdate>());
        float before = titan.Body!.DamageScalar;
        var update = titan.FindModule<TimedAttributeModifierUpdate>()!;
        var power = Power(titan, "AdaptiveArmor");
        Assert.True(update.InitiateIntentToDoSpecialPower(power.Power!, null, null, 0));
        Assert.True(titan.Body.DamageScalar < before, "armoured while it lasts");
        for (int f = 0; f < 30 * 11; f++)
            game.Update();
        Assert.Equal(before, titan.Body.DamageScalar, 3);
    }
}
