using OpenWestwood.Core.Installs;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Scripting;

namespace OpenWestwood.Logic.Multiplayer;

/// <summary>What a game slot holds (the original's SlotState).</summary>
public enum SlotState { Open, Closed, EasyAI, MedAI, BrutalAI, Player }

/// <summary>
/// One seat in a skirmish or network game (the original's GameSlot): who sits there and their
/// colour, faction, start position and team. Colour, faction and start position may be random
/// (-1) until the game starts.
/// </summary>
public sealed class GameSlot
{
    public const int PlayerTemplateRandom = -1, PlayerTemplateObserver = -2;

    public SlotState State { get; set; } = SlotState.Open;
    public string Name { get; set; } = "";
    /// <summary>Index into MultiplayerColors, or -1 for random.</summary>
    public int Color { get; set; } = -1;
    /// <summary>Index into the player templates, -1 random, -2 observer.</summary>
    public int PlayerTemplate { get; set; } = PlayerTemplateRandom;
    /// <summary>0-based start position, or -1 for random.</summary>
    public int StartPos { get; set; } = -1;
    /// <summary>Team 0-3, or -1 for none.</summary>
    public int TeamNumber { get; set; } = -1;

    public bool IsAI => State is SlotState.EasyAI or SlotState.MedAI or SlotState.BrutalAI;
    public bool IsHuman => State == SlotState.Player;
    public bool IsOccupied => IsHuman || IsAI;

    public void SetState(SlotState state, string name = "")
    {
        State = state;
        if (name.Length > 0)
            Name = name;
        if (!IsOccupied)
        {
            Color = -1;
            PlayerTemplate = PlayerTemplateRandom;
            StartPos = -1;
            TeamNumber = -1;
        }
    }

    public GameSlot Clone() => (GameSlot)MemberwiseClone();
}

/// <summary>
/// A game being set up (the original's GameInfo / SkirmishGameInfo): eight slots, the map, the
/// starting money and the superweapon limit. <see cref="ToPlayerSlots"/> settles the random choices
/// with the game's own random numbers, as GameLogic::startNewGame did, and hands the result to
/// <see cref="GameSetup"/>.
/// </summary>
public sealed class GameInfo
{
    public const int MaxSlots = 8;

    public GameSlot[] Slots { get; } = Enumerable.Range(0, MaxSlots).Select(_ => new GameSlot()).ToArray();
    public string Map { get; set; } = "";
    public int StartingCash { get; set; } = 10000;
    public bool SuperweaponRestriction { get; set; }
    /// <summary>OpenWESTWOOD: the whole map starts explored (terrain known, fog over what isn't seen).</summary>
    public bool ExploredMap { get; set; }
    /// <summary>OpenWESTWOOD: no fog of war: everyone sees everything, all game long.</summary>
    public bool NoFogOfWar { get; set; }
    /// <summary>OpenWESTWOOD: 0 normal, 1 fast (1.5x), 2 very fast (2x); the host's choice for everyone.</summary>
    public int GameSpeed { get; set; }

    public static readonly float[] GameSpeeds = [1f, 1.5f, 2f];
    public static readonly string[] GameSpeedNames = ["Normal", "Fast", "Very fast"];
    public ulong Seed { get; set; }
    public int LocalSlot { get; set; }

    public int NumPlayers => Slots.Count(s => s.IsOccupied);

    public bool IsColorTaken(int color, int exceptSlot = -1) =>
        Slots.Where((s, i) => i != exceptSlot && s.IsOccupied).Any(s => s.Color == color);

    public bool IsStartPositionTaken(int position) => Slots.Any(s => s.IsOccupied && s.StartPos == position);

    /// <summary>The playable factions, in the original's player template order: (index, template name).</summary>
    public static List<(int Index, string Name, RuleNode Rules)> PlayerTemplates(RuleSet rules) =>
        rules.Section("PlayerTemplates").Entries.Select((e, i) => (i, e.Key, e.Value)).ToList();

    public static bool IsPlayableTemplate(RuleNode template) =>
        IsLobbyFaction(template) && RuleValue.Bool(template.GetString("PlayableSide"), true);

    /// <summary>
    /// A faction a lobby slot can pick: one with a starting building (the original's test), or (OpenWESTWOOD)
    /// a Tiberian Dawn faction, which starts with only an MCV.
    /// </summary>
    public static bool IsLobbyFaction(RuleNode template) =>
        (template.GetString("StartingBuilding").Length > 0 || IsTdFaction(template)) && IsInstalledFaction(template);

    /// <summary>
    /// OpenWESTWOOD: the original game a faction needs the player to have installed (Zero Hour's America, China and
    /// GLA and their generals; Kane's Wrath's GDI, Nod and Scrin), or null for the other games' factions.
    /// </summary>
    public static OriginalGame? RequiredGame(RuleNode template)
    {
        string side = template.GetString("Side");
        if (side.StartsWith("KW", StringComparison.OrdinalIgnoreCase))
            return OriginalGame.KanesWrath;
        foreach (string zh in new[] { "America", "China", "GLA", "Boss" })
            if (side.StartsWith(zh, StringComparison.OrdinalIgnoreCase))
                return OriginalGame.ZeroHour;
        return null;
    }

    /// <summary>Whether a faction may be played: its original game is installed, or it needs none.</summary>
    public static bool IsInstalledFaction(RuleNode template) => RequiredGame(template) is not { } game || GameInstalls.Has(game);

    /// <summary>OpenWESTWOOD: Tiberian Dawn's GDI or Nod (mods/zh/rules/td).</summary>
    public static bool IsTdFaction(RuleNode template) => RuleValue.Bool(template.GetString("TdFaction"));

    public static List<(string Name, uint Rgb, string TooltipName)> Colors(RuleSet rules) =>
        rules.Section("MultiplayerColors").Entries.Select(e => (e.Key, ParseRgb(e.Value.GetString("RGBColor")), e.Value.GetString("TooltipName"))).ToList();

    static uint ParseRgb(string text)
    {
        int r = 0, g = 0, b = 0;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[1], out int v))
                continue;
            switch (kv[0].ToUpperInvariant())
            {
                case "R": r = v; break;
                case "G": g = v; break;
                case "B": b = v; break;
            }
        }
        return (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
    }

    /// <summary>
    /// Settles random factions, colours and start positions (the original's populateRandomSideAndColor
    /// and populateRandomStartPosition), then lists the occupied slots for <see cref="GameSetup.FromMap"/>.
    /// </summary>
    public List<PlayerSlot> ToPlayerSlots(GameRandom random, RuleSet rules, MapMetaData? map, Func<string, bool>? isGeneralLocked = null,
        Func<RuleNode, bool>? allowFaction = null)
    {
        var templates = PlayerTemplates(rules);
        var colors = Colors(rules);
        PopulateRandomSideAndColor(random, templates, colors.Count, isGeneralLocked, allowFaction);
        PopulateRandomStartPosition(random, map);
        var result = new List<PlayerSlot>();
        for (int i = 0; i < MaxSlots; i++)
        {
            var slot = Slots[i];
            if (!slot.IsOccupied)
                continue;
            string faction = slot.PlayerTemplate >= 0 && slot.PlayerTemplate < templates.Count ? templates[slot.PlayerTemplate].Name : "FactionObserver";
            uint color = slot.Color >= 0 && slot.Color < colors.Count ? colors[slot.Color].Rgb : 0xFFFFFF;
            var difficulty = slot.State switch
            {
                SlotState.EasyAI => Difficulty.Easy,
                SlotState.BrutalAI => Difficulty.Hard,
                _ => Difficulty.Normal,
            };
            string name = slot.Name.Length > 0 ? slot.Name : $"Player {i + 1}";
            result.Add(new PlayerSlot(name, faction, slot.IsHuman, slot.StartPos + 1, color, slot.TeamNumber, difficulty));
        }
        return result;
    }

    void PopulateRandomSideAndColor(GameRandom random, List<(int Index, string Name, RuleNode Rules)> templates, int colorCount, Func<string, bool>? isGeneralLocked,
        Func<RuleNode, bool>? allowFaction = null)
    {
        // (A network game's host passes the factions every player can load; otherwise those installed here.)
        Func<RuleNode, bool> allowed = allowFaction ?? IsInstalledFaction;
        // The playable factions a random pick may land on (locked challenge generals excluded; as in the
        // original, only factions with a starting building, so "Random" never picks a Tiberian Dawn faction).
        var startSlots = templates.Where(t => t.Rules.GetString("StartingBuilding").Length > 0 && allowed(t.Rules) && !(isGeneralLocked?.Invoke(t.Name) ?? false))
            .Select(t => t.Index).ToList();
        // OpenWESTWOOD: with neither Zero Hour nor Kane's Wrath installed, any faction the lobby offers.
        if (startSlots.Count == 0)
            startSlots = templates.Where(t => (t.Rules.GetString("StartingBuilding").Length > 0 || IsTdFaction(t.Rules)) && allowed(t.Rules)
                && !(isGeneralLocked?.Invoke(t.Name) ?? false)).Select(t => t.Index).ToList();
        for (int i = 0; i < MaxSlots; i++)
        {
            var slot = Slots[i];
            if (!slot.IsOccupied)
                continue;
            int playerTemplateIdx = slot.PlayerTemplate;
            while (playerTemplateIdx != GameSlot.PlayerTemplateObserver && (playerTemplateIdx < 0 || playerTemplateIdx >= templates.Count) && startSlots.Count > 0)
            {
                // (The original burns seed % 7 extra numbers first.)
                ulong silly = Seed % 7;
                for (ulong n = 0; n < silly; n++)
                    random.Int(0, 1);
                playerTemplateIdx = startSlots[random.Int(0, 1000) % startSlots.Count];
                slot.PlayerTemplate = playerTemplateIdx;
            }
            int colorIdx = slot.Color;
            if ((colorIdx < 0 || colorIdx >= colorCount) && colorCount > 0)
            {
                colorIdx = -1;
                while (colorIdx == -1)
                {
                    colorIdx = random.Int(0, colorCount - 1);
                    if (IsColorTaken(colorIdx))
                        colorIdx = -1;
                }
                slot.Color = colorIdx;
            }
        }
    }

    void PopulateRandomStartPosition(GameRandom random, MapMetaData? map)
    {
        // (A map may have more start points than the game has slots, e.g. Dune 2000's 12: only the first eight are used.)
        int numPlayers = Math.Min(map?.NumPlayers ?? MaxSlots, MaxSlots);
        if (numPlayers <= 0)
            return;
        var distance = new float[MaxSlots, MaxSlots];
        for (int i = 0; i < MaxSlots; i++)
            for (int j = 0; j < MaxSlots; j++)
            {
                if (i == j || i >= numPlayers || j >= numPlayers)
                    continue;
                var a = map?.StartPosition(i);
                var b = map?.StartPosition(j);
                distance[i, j] = a is { } p1 && b is { } p2 ? MathF.Sqrt((p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y)) : 1000000f;
            }
        bool hasStartSpotBeenPicked = false;
        var taken = new bool[MaxSlots];
        for (int i = 0; i < MaxSlots; i++)
            taken[i] = i >= numPlayers;
        foreach (var slot in Slots)
        {
            if (!slot.IsOccupied || slot.PlayerTemplate == GameSlot.PlayerTemplateObserver)
                continue;
            if (slot.StartPos >= 0 && slot.StartPos < MaxSlots)
            {
                hasStartSpotBeenPicked = true;
                taken[slot.StartPos] = true;
            }
        }

        // Teams start next to each other; everyone else as far apart as possible.
        var teamPosIdx = Enumerable.Repeat(-1, MaxSlots).ToArray();
        foreach (var slot in Slots)
        {
            if (!slot.IsOccupied || slot.PlayerTemplate == GameSlot.PlayerTemplateObserver)
                continue;
            int posIdx = slot.StartPos;
            if (posIdx >= 0 && posIdx < numPlayers)
                continue;
            int team = slot.TeamNumber;
            if (!hasStartSpotBeenPicked)
            {
                posIdx = -1;
                while (posIdx == -1)
                {
                    posIdx = random.Int(0, numPlayers - 1);
                    if (IsStartPositionTaken(posIdx))
                        posIdx = -1;
                }
                hasStartSpotBeenPicked = true;
                slot.StartPos = posIdx;
                taken[posIdx] = true;
                if (team > -1)
                    teamPosIdx[team] = posIdx;
            }
            else if (team < 0 || teamPosIdx[team] == -1)
            {
                float farthestDistance = 0;
                int farthestIndex = -1;
                for (posIdx = 0; posIdx < numPlayers; posIdx++)
                {
                    if (taken[posIdx])
                        continue;
                    float dist = 0;
                    for (int n = 0; n < numPlayers; n++)
                        if (taken[n] && n != posIdx)
                            dist += distance[posIdx, n];
                    if (farthestIndex < 0 || dist > farthestDistance)
                    {
                        farthestDistance = dist;
                        farthestIndex = posIdx;
                    }
                }
                if (farthestIndex < 0)
                    continue;
                slot.StartPos = farthestIndex;
                taken[farthestIndex] = true;
                if (team > -1)
                    teamPosIdx[team] = farthestIndex;
            }
            else
            {
                float closestDist = float.MaxValue;
                int closestIdx = 0;
                for (int n = 0; n < numPlayers; n++)
                {
                    float dist = distance[teamPosIdx[team], n];
                    if (!taken[n] && dist < closestDist)
                    {
                        closestDist = dist;
                        closestIdx = n;
                    }
                }
                slot.StartPos = closestIdx;
                taken[closestIdx] = true;
            }
        }
    }
}
