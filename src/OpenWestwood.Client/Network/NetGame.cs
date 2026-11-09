using OpenWestwood.Logic;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Logic.Players;

namespace OpenWestwood.Client.Network;

/// <summary>Building a network game: the same on every machine, from the settings the host sent.</summary>
public static class NetGame
{
    /// <summary>
    /// The host settles every random faction, colour and start position before the start (from the factions every
    /// player can load), so all machines build the very same game.
    /// </summary>
    public static void ResolveRandoms(GameContent content, MapCache maps, GameInfo info, NetLobby lobby)
    {
        var locked = content.Rules.Section("ChallengeGenerals").Entries
            .Where(e => !Core.Rules.RuleValue.Bool(e.Value.GetString("StartsEnabled"), true))
            .Select(e => e.Value.GetString("PlayerTemplate")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        info.ToPlayerSlots(new Logic.Core.GameRandom(info.Seed ^ 0x5EED5EED), content.Rules, maps.Find(info.Map), locked.Contains, lobby.IsFactionAllowed);
    }

    /// <summary>The game from the host's settings, with this machine's player (found by its slot's name) as the local one.</summary>
    public static (GameLogic Game, Formats.Maps.MapFile Map, Player Local, List<Player> Humans) Build(
        GameContent content, MapCache maps, GameInfo info, NetLobby lobby, int clientId)
    {
        var map = TiberianDawn.TdMaps.Read(content.FileSystem, info.Map);
        var game = GameSetup.FromGameInfo(map, content.Rules, info, maps, content.Models,
            p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        Player? Human(string name) => game.Players.All.FirstOrDefault(p => p.IsHuman && p.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        int slot = lobby.SlotOf(clientId);
        var local = (slot >= 0 ? Human(info.Slots[slot].Name) : null)
            ?? throw new InvalidOperationException("This machine's player is not in the game.");
        game.Players.Local = local;
        // Every game's art its factions need, read now: a machine stopping mid-game to load stops everyone.
        ArtPreloader.PreloadFor(game, content, info.Map);
        var humans = info.Slots.Where(s => s.IsHuman).Select(s => Human(s.Name)).OfType<Player>().ToList();
        return (game, map, local, humans);
    }
}
