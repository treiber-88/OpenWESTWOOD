using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.SoleSurvivor;

namespace OpenWestwood.Client.SoleSurvivor;

/// <summary>
/// A Sole Survivor Remix match as set up in its setup screen, and how it is made: the map with a player for the person and
/// each computer player (each computer on a random faction of any game), their teams for Zone Defense / Capture the Flag.
/// Apart from Classic the map starts bare: the start bases are cleared, the computers have no base-building brains, the
/// usual victory is off and the match (SoleSurvivorMatch) decides who wins.
/// </summary>
public sealed class SsrMatchSetup
{
    public SsrMode Mode { get; set; } = SsrMode.Freeplay;
    public string Map { get; set; } = "Maps/Tournament Desert/Tournament Desert.map";
    public int MapPlayers { get; set; } = 2;
    public string Faction { get; set; } = "FactionAmerica";
    public int Computers { get; set; } = 3;
    public int Lives { get; set; }
    public Logic.Scripting.Difficulty Difficulty { get; set; } = Logic.Scripting.Difficulty.Normal;
    /// <summary>Debug: the unit the person goes in as at once (no choice panel first).</summary>
    public string? AutoUnit { get; set; }

    static readonly uint[] Colors = [0x2050FF, 0xE02020, 0x20C020, 0xE0A000, 0xA040E0, 0x20C0C0, 0xFF8040, 0xC0C0C0];

    public (GameLogic Game, MapFile Map, SoleSurvivorMatch Match) Build(GameContent content)
    {
        var map = TiberianDawn.TdMaps.Read(content.FileSystem, Map);
        var factions = SsrCatalog.Factions(content.Rules, new Logic.Templates.TemplateStore(content.Rules));
        var random = new Random();
        bool teams = Mode is SsrMode.ZoneDefense or SsrMode.CaptureTheFlag;
        // Classic needs a start point a player; the others spread round the map anyway.
        int computers = Mode == SsrMode.Classic ? Math.Min(Computers, Math.Max(1, MapPlayers - 1)) : Computers;
        var slots = new List<(PlayerSlot Slot, SsrFaction? Faction, bool Computer, int Team)>();
        for (int i = 0; i <= computers; i++)
        {
            bool computer = i > 0;
            var faction = computer ? factions[random.Next(factions.Count)] : factions.FirstOrDefault(f => f.Name == Faction) ?? factions[0];
            int team = teams ? i % 2 : i;
            // Teams share their base's start point; otherwise each player its own while there are enough of them.
            int start = teams ? team + 1 : i < MapPlayers ? i + 1 : -1;
            slots.Add((new PlayerSlot(computer ? $"Computer {i}" : "Player", faction.Name, IsHuman: !computer, start, Colors[i % Colors.Length],
                Team: teams ? team : -1, Difficulty), faction, computer, team));
        }
        var game = GameSetup.FromMap(map, content.Rules, (ulong)Environment.TickCount64, slots.Select(s => s.Slot).ToList(),
            startingCash: Mode == SsrMode.Classic ? 10000 : 0, models: content.Models,
            readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var players = new List<SsrPlayer>();
        foreach (var (slot, faction, computer, team) in slots)
            if (game.Players.Find(slot.Name) is { } player)
                players.Add(new SsrPlayer { Player = player, Team = team, IsComputer = computer, Faction = faction });
        if (Mode != SsrMode.Classic)
        {
            // A bare arena: no bases, no base-building brains, no mission or skirmish scripts, no usual victory.
            game.Scripts = null;
            game.Victory.Suspended = true;
            foreach (var p in players)
            {
                p.Player.AI = null;
                foreach (var obj in game.ObjectsOwnedBy(p.Player).ToList())
                    game.DestroyObject(obj);
            }
        }
        var match = new SoleSurvivorMatch(game, new SsrSettings
        {
            Mode = Mode,
            Lives = Lives,
            KillRewardPercent = (int)Core.Rules.RuleValue.Float(content.Rules.Section("SoleSurvivor").GetString("KillRewardPercent"), 40),
        }, players);
        game.SoleSurvivor = match;
        return (game, map, match);
    }
}
