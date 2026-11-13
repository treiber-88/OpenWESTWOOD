using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.SoleSurvivor;

namespace OpenWestwood.Client.SoleSurvivor;

/// <summary>
/// A Sole Survivor Remix match as set up in its setup screen, and how it is made: the map with a player for the person and
/// each computer player (each computer on a random faction of any game that has the chosen kind of unit), their teams for
/// Zone Defense / Capture the Flag, and the start points (the person's as chosen, the others' spread over the rest).
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
    /// <summary>Team Battle: computers on the person's team, and on the other team (Computers isn't used then).</summary>
    public int AlliedComputers { get; set; } = 1;
    public int EnemyComputers { get; set; } = 2;
    public int TimeLimitMinutes { get; set; }
    public int KillLimit { get; set; }
    public int Lives { get; set; }
    public Logic.Scripting.Difficulty Difficulty { get; set; } = Logic.Scripting.Difficulty.Normal;
    /// <summary>The kind of unit everyone plays (aircraft allowed besides, except with infantry); null: any.</summary>
    public SsrCategory? UnitType { get; set; } = SsrCategory.Vehicle;
    /// <summary>The person's start point (0-based, the map's Player_N_Start); -1: any.</summary>
    public int Start { get; set; } = -1;
    /// <summary>The map's list entry (its picture and extent, for the minimap); found from the map cache if not given.</summary>
    public Logic.Multiplayer.MapMetaData? MapInfo { get; set; }
    /// <summary>Debug: the unit the person goes in as at once (no choice panel first).</summary>
    public string? AutoUnit { get; set; }

    /// <summary>Most computer players a match may have.</summary>
    public const int MaxComputers = 80;

    static readonly uint[] Colors = [0x2050FF, 0xE02020, 0x20C020, 0xE0A000, 0xA040E0, 0x20C0C0, 0xFF8040, 0xC0C0C0];

    public (GameLogic Game, MapFile Map, SoleSurvivorMatch Match) Build(GameContent content)
    {
        var map = TiberianDawn.TdMaps.Read(content.FileSystem, Map);
        var settings = new SsrSettings
        {
            Mode = Mode,
            Lives = Lives,
            UnitType = UnitType,
            TimeLimitMinutes = TimeLimitMinutes,
            KillLimit = KillLimit,
            KillRewardPercent = (int)Core.Rules.RuleValue.Float(content.Rules.Section("SoleSurvivor").GetString("KillRewardPercent"), 40),
        };
        var all = SsrCatalog.Factions(content.Rules, new Logic.Templates.TemplateStore(content.Rules));
        // The computers play factions that have a fighting unit of the chosen kind (aircraft don't count for that).
        var factions = all.Where(f => f.Units.Any(u => u.Power > 0 && u.Category != SsrCategory.Aircraft && settings.Allowed(u))).ToList();
        if (factions.Count == 0)
            factions = all.ToList();
        var random = new Random();
        bool teams = settings.Teams;
        bool battle = Mode == SsrMode.TeamBattle;
        if (battle)
            Computers = Math.Clamp(AlliedComputers, 0, MaxComputers) + Math.Clamp(EnemyComputers, 0, MaxComputers);
        int mapPlayers = Math.Max(1, MapPlayers);
        // Classic needs a start point a player; the others spread round the map anyway.
        int computers = Math.Clamp(Mode == SsrMode.Classic ? Math.Min(Computers, Math.Max(1, mapPlayers - 1)) : Computers, 0, MaxComputers);
        // Start points: the person's as chosen (or any), the other team's (or the other players') the rest, farthest first.
        int mine = Start >= 0 && Start < mapPlayers ? Start : random.Next(mapPlayers);
        var others = Enumerable.Range(0, mapPlayers).Where(i => i != mine)
            .OrderByDescending(i => MapInfo?.StartPosition(i) is { } p && MapInfo.StartPosition(mine) is { } m ? Vector2Distance(p, m) : 0).ToList();
        var slots = new List<(PlayerSlot Slot, SsrFaction? Faction, bool Computer, int Team)>();
        for (int i = 0; i <= computers; i++)
        {
            bool computer = i > 0;
            var faction = computer ? factions[random.Next(factions.Count)] : all.FirstOrDefault(f => f.Name == Faction) ?? factions[0];
            // (Team Battle: the person's allies first, then the other team; the other team modes alternate.)
            int team = battle ? (i <= Math.Clamp(AlliedComputers, 0, MaxComputers) ? 0 : 1) : teams ? i % 2 : i;
            int start;
            if (teams)
                start = (team == 0 ? mine : others.Count > 0 ? others[0] : mine) + 1;
            else if (i == 0)
                start = mine + 1;
            else
                start = i - 1 < others.Count ? others[i - 1] + 1 : -1;
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
        var match = new SoleSurvivorMatch(game, settings, players);
        game.SoleSurvivor = match;
        return (game, map, match);
    }

    static float Vector2Distance(System.Numerics.Vector3 a, System.Numerics.Vector3 b) =>
        System.Numerics.Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
}
