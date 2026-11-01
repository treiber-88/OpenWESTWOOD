using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic;
using OpenGZH.Logic.AI;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Client.Shell;

/// <summary>
/// OpenGZH's shell map: a battle for Dune behind the menus (GameData OpenGZHShellMap, a "d2k:" map). Each start point
/// is a house run by the computer (OpenGZHShellHouses), watched by an observer; the battle is played ahead a few minutes
/// (OpenGZHShellPreroll seconds) so the bases are up and the armies out when the menus open, and the houses attack
/// often. When one house is left (or the time runs out) the battle starts over.
/// </summary>
public static class DuneShellBattle
{
    public static float Number(RuleSet rules, string key, float fallback) =>
        float.TryParse(rules.Section("GameData").GetString(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>Whether the rules ask for a Dune shell battle (and which map).</summary>
    public static string? MapName(RuleSet rules) =>
        rules.Section("GameData").GetString("OpenGZHShellMap") is { Length: > 0 } map ? map : null;

    /// <summary>Sets the battle up (and plays it ahead). Null if the map can't be read.</summary>
    public static (GameLogic Game, MapFile Map)? Create(GameContent content, int seed) =>
        Create(content.Rules, content.FileSystem, content.Models, seed);

    public static (GameLogic Game, MapFile Map)? Create(RuleSet rules, Core.FileSystem.VirtualFileSystem files, Formats.W3d.W3dLibrary? models, int seed)
    {
        if (MapName(rules) is not { } mapName)
            return null;
        var map = TiberianDawn.TdMaps.Read(files, mapName);
        int starts = map.Objects.Count(o => o.IsWaypoint && (o.Get("waypointName") ?? "").EndsWith("_Start"));
        var houses = RuleValue.Words(rules.Section("GameData").GetString("OpenGZHShellHouses")).ToList();
        if (houses.Count == 0)
            houses = ["FactionD2KAtreides", "FactionD2KHarkonnen", "FactionD2KOrdos", "FactionD2KHarkonnen"];
        uint[] colors = [0x2060FF, 0xE02020, 0x20C020, 0xE0A000, 0xA040E0, 0x20C0C0, 0xFF8040, 0xC0C0C0];
        var slots = new List<PlayerSlot> { new("Observer", "FactionObserver", IsHuman: true, StartPosition: -1, Color: 0xFFFFFF) };
        for (int i = 0; i < Math.Min(starts, Math.Min(houses.Count, Logic.Multiplayer.GameInfo.MaxSlots - 1)); i++)
            slots.Add(new PlayerSlot($"House{i + 1}", houses[i], IsHuman: false, StartPosition: i + 1, Color: colors[i % colors.Length]));
        var game = GameSetup.FromMap(map, rules, seed: (ulong)seed, slots, startingCash: (int)Number(rules, "OpenGZHShellCash", 20000f),
            models: models, readFile: p => files.Exists(p) ? files.ReadAllBytes(p) : null);
        game.IsShellGame = true;
        // No shroud or fog for anyone: the observer sees the whole map, and the houses find each other.
        GameSetup.ApplyVisibility(game, exploredMap: true, noFogOfWar: true);
        // The houses attack early and often (a skirmish AI waits five minutes, then every three).
        foreach (var player in game.Players.Active)
            if (player.AI is TdFactionAI ai)
            {
                ai.AttackDelaySeconds = Number(rules, "OpenGZHShellFirstAttack", 60f);
                ai.AttackIntervalSeconds = Number(rules, "OpenGZHShellAttackInterval", 45f);
            }
        int preroll = (int)(Number(rules, "OpenGZHShellPreroll", 150f) * LogicTime.FramesPerSecond);
        for (int i = 0; i < preroll; i++)
            game.Update();
        return (game, map);
    }

    /// <summary>The houses still fighting (with a building or an armed unit).</summary>
    public static int HousesLeft(GameLogic game) =>
        game.Players.Active.Count(p => p.AI is TdFactionAI && game.ObjectsOwnedBy(p).Any(o => !o.IsEffectivelyDead && (o.IsKindOf(Kind.Structure) || o.Template.Rules.TryGet("WeaponSet", out _))));

    /// <summary>A house's base (the middle of its buildings), the houses taken in turn by <paramref name="turn"/>; null if none is left.</summary>
    public static Vector3? Base(GameLogic game, int turn)
    {
        var bases = game.Players.Active.Where(p => p.AI is TdFactionAI)
            .Select(p => game.ObjectsOwnedBy(p).Where(o => !o.IsEffectivelyDead && o.IsKindOf(Kind.Structure)).ToList())
            .Where(b => b.Count > 0).ToList();
        if (bases.Count == 0)
            return null;
        var b = bases[(turn % bases.Count + bases.Count) % bases.Count];
        return new Vector3(b.Average(o => o.Position.X), b.Average(o => o.Position.Y), 0);
    }

    /// <summary>Where the fighting is: the middle of the objects hurt in the last few seconds (null: nowhere).</summary>
    public static Vector3? Fighting(GameLogic game)
    {
        var hurt = game.Objects.Where(o => !o.IsEffectivelyDead && o.Body is { } b && game.Frame - b.LastDamageFrame < 4 * LogicTime.FramesPerSecond
            && !o.Owner.IsNeutral).ToList();
        if (hurt.Count == 0)
            return null;
        // The densest spot: the hurt object with the most hurt objects near it.
        var best = hurt.OrderByDescending(o => hurt.Count(n => MathUtil.Distance2D(n.Position, o.Position) < 250)).First();
        var near = hurt.Where(n => MathUtil.Distance2D(n.Position, best.Position) < 250).ToList();
        return new Vector3(near.Average(n => n.Position.X), near.Average(n => n.Position.Y), 0);
    }
}
