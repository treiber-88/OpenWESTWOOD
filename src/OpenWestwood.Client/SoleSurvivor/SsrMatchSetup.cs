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

    /// <summary>A person in the match (a network game's players; the host first).</summary>
    public sealed record Human(string Name, string Faction, int Team);

    /// <summary>Everyone at a keyboard (a network game); null: just this machine's "Player" on <see cref="Faction"/>.</summary>
    public List<Human>? Humans { get; set; }

    /// <summary>A network game's seed (the host's); null: any.</summary>
    public ulong? Seed { get; set; }

    /// <summary>The settings as a network game's lobby carries them (GameInfo.Extra).</summary>
    public string Encode() => string.Join(";", "SSR", $"Mode={(int)Mode}", $"Unit={(UnitType is { } u ? (int)u : -1)}", $"Lives={Lives}",
        $"Computers={Computers}", $"Allies={AlliedComputers}", $"Enemies={EnemyComputers}", $"Time={TimeLimitMinutes}", $"Kills={KillLimit}",
        $"Skill={(int)Difficulty}", $"Start={Start}");

    /// <summary>Whether a lobby's extra settings are a Sole Survivor Remix game's.</summary>
    public static bool IsSsr(string extra) => extra.StartsWith("SSR;", StringComparison.Ordinal);

    /// <summary>The settings from a lobby's extra settings (the map and the people come from the lobby itself).</summary>
    public static SsrMatchSetup Decode(string extra)
    {
        var values = extra.Split(';').Skip(1).Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => int.TryParse(kv[1], out int v) ? v : 0);
        int Get(string key, int fallback) => values.TryGetValue(key, out int v) ? v : fallback;
        int unit = Get("Unit", 0);
        return new SsrMatchSetup
        {
            Mode = (SsrMode)Math.Clamp(Get("Mode", 0), 0, Enum.GetValues<SsrMode>().Length - 1),
            UnitType = unit < 0 ? null : (SsrCategory)unit,
            Lives = Get("Lives", 0),
            Computers = Math.Clamp(Get("Computers", 3), 0, MaxComputers),
            AlliedComputers = Math.Clamp(Get("Allies", 1), 0, MaxComputers),
            EnemyComputers = Math.Clamp(Get("Enemies", 2), 0, MaxComputers),
            TimeLimitMinutes = Get("Time", 0),
            KillLimit = Get("Kills", 0),
            Difficulty = (Logic.Scripting.Difficulty)Get("Skill", 1),
            Start = Get("Start", -1),
        };
    }

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
        // (A network game: every machine draws the same "random" choices, from the host's seed.)
        var random = Seed is { } seed ? new Random((int)(seed ^ (seed >> 32))) : new Random();
        bool teams = settings.Teams;
        bool battle = Mode == SsrMode.TeamBattle;
        var people = Humans is { Count: > 0 } humans ? humans : [new Human("Player", Faction, 0)];
        int hostTeam = teams ? Math.Clamp(people[0].Team, 0, 1) : 0;
        if (battle)
            Computers = Math.Clamp(AlliedComputers, 0, MaxComputers) + Math.Clamp(EnemyComputers, 0, MaxComputers);
        int mapPlayers = Math.Max(1, MapPlayers);
        // Classic needs a start point a player; the others spread round the map anyway.
        int computers = Math.Clamp(Mode == SsrMode.Classic ? Math.Min(Computers, Math.Max(0, mapPlayers - people.Count)) : Computers, 0, MaxComputers);
        // Start points: the host's as chosen (or any); the other team's (or the other players') the rest, farthest first.
        int mine = Start >= 0 && Start < mapPlayers ? Start : random.Next(mapPlayers);
        var others = Enumerable.Range(0, mapPlayers).Where(i => i != mine)
            .OrderByDescending(i => MapInfo?.StartPosition(i) is { } p && MapInfo.StartPosition(mine) is { } m ? Vector2Distance(p, m) : 0).ToList();
        int TeamStart(int team) => (team == hostTeam ? mine : others.Count > 0 ? others[0] : mine) + 1;
        var slots = new List<(PlayerSlot Slot, SsrFaction? Faction, bool Computer, int Team)>();
        int[] teamSize = new int[2];
        int nextStart = 0;
        for (int i = 0; i < people.Count + computers; i++)
        {
            bool computer = i >= people.Count;
            var person = computer ? null : people[i];
            var faction = computer ? factions[random.Next(factions.Count)] : all.FirstOrDefault(f => f.Name == person!.Faction) ?? factions[0];
            int team;
            if (!teams)
                team = i;
            else if (!computer)
                team = Math.Clamp(person!.Team, 0, 1);
            else if (battle)
                // Team Battle: the host's allies first, then the other team.
                team = i - people.Count < Math.Clamp(AlliedComputers, 0, MaxComputers) ? hostTeam : 1 - hostTeam;
            else
                // The other team modes: each computer to the smaller team.
                team = teamSize[0] <= teamSize[1] ? 0 : 1;
            if (teams)
                teamSize[team]++;
            int start;
            if (teams)
                start = TeamStart(team);
            else if (i == 0)
                start = mine + 1;
            else
                start = nextStart < others.Count ? others[nextStart++] + 1 : -1;
            string name = computer ? $"Computer {i - people.Count + 1}" : person!.Name;
            slots.Add((new PlayerSlot(name, faction.Name, IsHuman: !computer, start, Colors[i % Colors.Length],
                Team: teams ? team : -1, Difficulty), faction, computer, team));
        }
        var game = GameSetup.FromMap(map, content.Rules, Seed ?? (ulong)Environment.TickCount64, slots.Select(s => s.Slot).ToList(),
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
