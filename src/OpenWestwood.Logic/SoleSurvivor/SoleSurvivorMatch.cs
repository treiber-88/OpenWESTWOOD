using System.Numerics;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;

namespace OpenWestwood.Logic.SoleSurvivor;

/// <summary>Sole Survivor Remix's ways to play.</summary>
public enum SsrMode
{
    /// <summary>Every player for themselves; kills score.</summary>
    Freeplay,
    /// <summary>Two teams, each with a zone round its base: a team scores while its units stand in the other's zone.</summary>
    ZoneDefense,
    /// <summary>Two teams: take the other team's flag back to your own.</summary>
    CaptureTheFlag,
    /// <summary>Everyone starts with a base too, builds and harvests (money only from harvesting); the last side standing wins.</summary>
    Classic,
}

/// <summary>A match's settings (sole-survivor.yaml's defaults, changed in the setup screen).</summary>
public sealed class SsrSettings
{
    public SsrMode Mode { get; set; }
    /// <summary>Lives each player has; 0 = respawn without limit.</summary>
    public int Lives { get; set; }
    public float RespawnSeconds { get; set; } = 3;
    /// <summary>A kill pays this share of what the victim cost.</summary>
    public int KillRewardPercent { get; set; } = 40;
    /// <summary>Zone Defense: points to win (a point a second for each unit in one of the other team's zones).</summary>
    public int ZoneScoreLimit { get; set; } = 300;
    public float ZoneRadius { get; set; } = 140;
    /// <summary>Zone Defense: each team's zones, out from its base toward the middle (nobody comes in on one).</summary>
    public int ZonesPerTeam { get; set; } = 3;
    /// <summary>
    /// The kind of unit everyone plays (chosen at the start): vehicles, infantry or naval; aircraft may be taken any time
    /// besides, except in an infantry match (the user's rule: infantry and aircraft don't play together). Null: any kind.
    /// </summary>
    public SsrCategory? UnitType { get; set; }

    /// <summary>Whether aircraft may be taken besides the match's unit type.</summary>
    public bool AircraftAllowed => UnitType != SsrCategory.Infantry;

    public bool Allowed(SsrUnit unit) => KindAllowed(unit.Category);

    public bool KindAllowed(SsrCategory kind) => UnitType == null || kind == UnitType || kind == SsrCategory.Aircraft && AircraftAllowed;
    /// <summary>Capture the Flag: captures to win.</summary>
    public int FlagCaptureLimit { get; set; } = 3;
    /// <summary>A dropped flag goes home after this long.</summary>
    public float FlagReturnSeconds { get; set; } = 30;
    /// <summary>The object shown as a flag (Kane's Wrath's capture flag); none: only the interface marks it.</summary>
    public string FlagObject { get; set; } = "KW_CaptureFlag";
    /// <summary>Upgrade steps: weapons +%, armour +% and speed +% each level.</summary>
    public float WeaponStep { get; set; } = 0.12f;
    public float ArmorStep { get; set; } = 0.12f;
    public float SpeedStep { get; set; } = 0.08f;
}

/// <summary>Each player's part in the match: their unit, upgrades, lives and score, and what they steer.</summary>
public sealed class SsrPlayer
{
    public required Player Player { get; init; }
    public int Team { get; init; }
    public bool IsComputer { get; init; }
    public SsrFaction? Faction { get; init; }
    public uint UnitId { get; set; }
    public string? Unit { get; set; }
    public int WeaponLevel { get; set; }
    public int ArmorLevel { get; set; }
    public int SpeedLevel { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    /// <summary>Credits earned in this match (a computer player spends them on better units and upgrades).</summary>
    public int Credits { get; set; }
    public int Spent { get; set; }
    /// <summary>The frame its next unit may come in (after a death).</summary>
    public int RespawnFrame { get; set; }
    /// <summary>Every life used.</summary>
    public bool Out { get; set; }
    public bool HasFlag { get; set; }
    /// <summary>Units it has come in as (the first comes in at its start point).</summary>
    public int Spawns { get; set; }

    // What the player steers (SsrInputCommand) or the computer wants.
    public Vector2 Move { get; set; }
    public Vector3? Destination { get; set; }
    public Vector3 Aim { get; set; }
    public uint AimTarget { get; set; }
    public bool FirePrimary { get; set; }
    public bool FireSecondary { get; set; }
    internal int NextThink;
    internal Vector3? LastOrder;
    internal int LastOrderFrame;
    internal bool Defender;
}

/// <summary>
/// OpenWESTWOOD: a game of Sole Survivor Remix (GameLogic.SoleSurvivor): each player is one unit of their faction, steered
/// directly (SsrInputCommand: a direction, the aim and the triggers) or by the computer. Kills pay credits (a share of the
/// victim's cost); upgrades make a player's weapons, armour and speed better; a player comes back after a death while lives
/// last. The modes decide the winner (Freeplay has none: it goes on). Classic keeps the normal skirmish too (bases, money
/// from harvesting, the usual victory) with each player's unit in it.
/// </summary>
public sealed class SoleSurvivorMatch
{
    readonly GameLogic game;
    public SsrSettings Settings { get; }
    public List<SsrPlayer> Players { get; } = new();

    /// <summary>Credits earned by a player (the game's interface adds the local player's to their profile).</summary>
    public event Action<Player, int>? CreditsEarned;

    /// <summary>The winning team (or, Freeplay, nobody); set when the match is won.</summary>
    public int? WinningTeam { get; private set; }
    public int[] TeamScores { get; } = new int[2];

    // Capture the Flag: each team's flag (its home, where it is, who carries it, when it was dropped).
    public sealed class Flag
    {
        public int Team { get; init; }
        public Vector3 Home { get; init; }
        public Vector3 Position { get; set; }
        public SsrPlayer? Carrier { get; set; }
        public int DroppedFrame { get; set; } = -1;
        public uint ObjectId { get; set; }
        public bool AtHome => Carrier == null && Vector2.Distance(new(Position.X, Position.Y), new(Home.X, Home.Y)) < 1;
    }
    public List<Flag> Flags { get; } = new();
    /// <summary>How often a flag has been picked up by the other team (Capture the Flag).</summary>
    public int FlagTakes { get; private set; }

    /// <summary>Each team's base (where its units start, its flag).</summary>
    public Vector3[] TeamBases { get; } = new Vector3[2];

    /// <summary>Zone Defense: the zones, each a team's to hold (the other team scores while in one).</summary>
    public List<(int Team, Vector3 Center)> Zones { get; } = new();

    public SoleSurvivorMatch(GameLogic game, SsrSettings settings, IEnumerable<SsrPlayer> players)
    {
        this.game = game;
        Settings = settings;
        Players.AddRange(players);
        game.Events.OnObjectDied += OnObjectDied;
        // Team bases: the first start point of each team's players (the map's start waypoints otherwise).
        for (int team = 0; team < 2; team++)
        {
            var start = Players.Where(p => p.Team == team).Select(p => p.Player.StartPosition).FirstOrDefault(s => s != null);
            TeamBases[team] = start ?? new Vector3(game.Terrain.WorldWidth * (team == 0 ? 0.2f : 0.8f), game.Terrain.WorldHeight / 2, 0);
        }
        if (settings.Mode == SsrMode.ZoneDefense)
            PlaceZones();
        if (settings.Mode == SsrMode.CaptureTheFlag)
            for (int team = 0; team < 2; team++)
            {
                var home = TeamBases[team] with { Z = game.Terrain.GroundHeight(TeamBases[team]) };
                var flag = new Flag { Team = team, Home = home, Position = home };
                if (settings.FlagObject.Length > 0 && game.Templates.Find(settings.FlagObject) != null
                    && game.CreateObject(settings.FlagObject, game.Players.Neutral, home) is { } obj)
                    flag.ObjectId = obj.Id;
                Flags.Add(flag);
            }
        // The computer players come in at once; the people choose first (their interface calls Spawn).
        foreach (var p in Players.Where(p => p.IsComputer))
            p.RespawnFrame = game.Frame;
    }

    bool? hasWater;

    /// <summary>Whether the map has water (for boats): a look at a grid of points over it.</summary>
    bool HasWater => hasWater ??= Enumerable.Range(0, 400).Any(i =>
        game.Terrain.IsUnderwater(game.Terrain.WorldWidth * (i % 20 + 0.5f) / 20, game.Terrain.WorldHeight * (i / 20 + 0.5f) / 20, out _));

    public SsrPlayer? Of(Player player) => Players.FirstOrDefault(p => p.Player == player);

    public GameObject? UnitOf(SsrPlayer p) => p.UnitId != 0 && game.FindObject(p.UnitId) is { IsEffectivelyDead: false } u ? u : null;

    /// <summary>Whether the player may come in now (a unit of theirs isn't out, a life is left, the wait is over).</summary>
    public bool CanSpawn(SsrPlayer p) => !p.Out && UnitOf(p) == null && game.Frame >= p.RespawnFrame && WinningTeam == null;

    /// <summary>Seconds until the player may come in again (0 if they may).</summary>
    public float RespawnWait(SsrPlayer p) => Math.Max(0, (p.RespawnFrame - game.Frame) / (float)LogicTime.FramesPerSecond);

    /// <summary>The kind of unit a template is in the player's faction (vehicle if not listed).</summary>
    static SsrCategory CategoryOf(SsrPlayer p, string? template) =>
        p.Faction?.Units.FirstOrDefault(u => u.Template == template)?.Category ?? SsrCategory.Vehicle;

    /// <summary>Brings the player in as this unit, at a spawn point, with their upgrades.</summary>
    public GameObject? Spawn(SsrPlayer p, string template)
    {
        if (!CanSpawn(p) || game.Templates.Find(template) is not { } t)
            return null;
        var unit = Place(p, t, template, SpawnSpot(p, template), game.Random.Float(0, MathF.Tau));
        p.Spawns++;
        return unit;
    }

    /// <summary>
    /// Swaps the player's unit for another at once, where it stands (a person may change units any time): the new one has the
    /// same share of its health, and the swap is no death. With no unit in, the same as <see cref="Spawn"/>.
    /// </summary>
    public GameObject? Switch(SsrPlayer p, string template)
    {
        if (UnitOf(p) is not { } old)
            return Spawn(p, template);
        if (WinningTeam != null || p.Out || game.Templates.Find(template) is not { } t)
            return null;
        float health = old.MaxHealth > 0 ? Math.Clamp(old.Health / old.MaxHealth, 0.05f, 1) : 1;
        bool naval = CategoryOf(p, template) == SsrCategory.Naval;
        var at = naval != (CategoryOf(p, p.Unit) == SsrCategory.Naval) ? WaterOrGround(p, old.Position, naval) : old.Position;
        var unit = Place(p, t, template, at, old.Orientation);
        unit.Body?.SetHealth(unit.MaxHealth * health);
        game.DestroyObject(old);
        return unit;
    }

    GameObject Place(SsrPlayer p, Templates.ObjectTemplate t, string template, Vector3 at, float facing)
    {
        var unit = game.CreateObject(t, p.Player, at with { Z = game.Terrain.GroundHeight(at) }, facing);
        if (unit.AI is { } ai && !game.Pathfinder.IsValidMovementTerrain(unit.Position, ai.Surfaces))
        {
            if (game.Pathfinder.NearestPassable(unit.Position, ai.Surfaces, 8) is { } free)
                unit.SetPosition(free with { Z = game.Terrain.GroundHeight(free) });
            else if (SpawnSpot(p, template) is var elsewhere)
                unit.SetPosition(elsewhere with { Z = game.Terrain.GroundHeight(elsewhere) });
        }
        unit.DamageBonus *= 1 + Settings.WeaponStep * p.WeaponLevel;
        unit.ArmorBonus *= 1 + Settings.ArmorStep * p.ArmorLevel;
        unit.SpeedBonus *= 1 + Settings.SpeedStep * p.SpeedLevel;
        p.UnitId = unit.Id;
        p.Unit = template;
        p.Move = Vector2.Zero;
        p.Destination = null;
        p.LastOrder = null;
        return unit;
    }

    /// <summary>Where a unit of this kind comes in: open ground (or open water for a boat) near the player's spawn point,
    /// clear of the zones.</summary>
    Vector3 SpawnSpot(SsrPlayer p, string template)
    {
        bool naval = CategoryOf(p, template) == SsrCategory.Naval;
        var at = SpawnPoint(p);
        if (naval)
            return WaterOrGround(p, at, naval: true);
        for (int tries = 0; tries < 30 && !(OpenGround(at) && ClearOfZones(at)); tries++)
            at = SpawnPoint(p);
        return at;
    }

    /// <summary>The nearest open water (a boat) or open ground to a point, clear of the zones.</summary>
    Vector3 WaterOrGround(SsrPlayer p, Vector3 near, bool naval)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < 400; i++)
        {
            float margin = 60;
            var spot = new Vector3(game.Random.Float(margin, game.Terrain.WorldWidth - margin), game.Random.Float(margin, game.Terrain.WorldHeight - margin), 0);
            if (!(naval ? OpenWater(spot) : OpenGround(spot)) || !ClearOfZones(spot))
                continue;
            float d = MathUtil.Distance2D(spot, near);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = spot;
            }
        }
        return best ?? near;
    }

    bool ClearOfZones(Vector3 spot) => Zones.All(z => MathUtil.Distance2D(z.Center, spot) > Settings.ZoneRadius + 120);

    /// <summary>Ground units can drive off this spot in most directions.</summary>
    bool OpenGround(Vector3 spot) => Open(spot, Locomotion.Surfaces.Ground);

    /// <summary>Boats can sail off this spot in most directions.</summary>
    bool OpenWater(Vector3 spot) => Open(spot, Locomotion.Surfaces.Water);

    bool Open(Vector3 spot, Locomotion.Surfaces surfaces)
    {
        int open = 0;
        for (int k = 0; k < 8; k++)
        {
            var dir = MathUtil.Forward(k * MathF.PI / 4);
            if (game.Pathfinder.IsValidMovementTerrain(spot + dir * 40, surfaces) && game.Pathfinder.IsValidMovementTerrain(spot + dir * 100, surfaces))
                open++;
        }
        return open >= 6 && game.Pathfinder.IsValidMovementTerrain(spot, surfaces);
    }

    Vector3 SpawnPoint(SsrPlayer p)
    {
        // The first unit comes in at the player's start point (chosen in the setup); teams round their base; otherwise
        // the place farthest from the others among a few tries.
        if (Settings.Mode == SsrMode.Freeplay && p.Spawns == 0 && p.Player.StartPosition is { } start)
        {
            float a = game.Random.Float(0, MathF.Tau), r = game.Random.Float(0, 80);
            return start + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0);
        }
        if (Settings.Mode is SsrMode.ZoneDefense or SsrMode.CaptureTheFlag)
        {
            float a = game.Random.Float(0, MathF.Tau), r = game.Random.Float(40, 140);
            return TeamBases[p.Team] + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0);
        }
        if (Settings.Mode == SsrMode.Classic && p.Player.StartPosition is { } home)
            return home + new Vector3(game.Random.Float(-120, 120), game.Random.Float(-120, 120), 0);
        var others = Players.Where(o => o != p).Select(UnitOf).OfType<GameObject>().Select(u => u.Position).ToList();
        Vector3 best = default;
        float bestDistance = -1;
        for (int i = 0; i < 12; i++)
        {
            float margin = 120;
            var spot = new Vector3(game.Random.Float(margin, game.Terrain.WorldWidth - margin), game.Random.Float(margin, game.Terrain.WorldHeight - margin), 0);
            float nearest = others.Count == 0 ? 99999 : others.Min(o => MathUtil.Distance2D(o, spot));
            if (nearest > bestDistance)
            {
                bestDistance = nearest;
                best = spot;
            }
        }
        return best;
    }

    /// <summary>Spends a computer player's credits; a person's upgrades come from their profile (set before Spawn).</summary>
    public int UpgradeCost(int level) => 400 * (level + 1);

    void OnObjectDied(GameObject obj, Combat.DamageInfo? blow)
    {
        var killer = blow is { SourceId: not 0 } b && game.FindObject(b.SourceId) is { } source ? Of(source.Owner) : null;
        var victim = Players.FirstOrDefault(p => p.UnitId == obj.Id);
        // A kill of an enemy pays a share of what it cost.
        if (killer != null && obj.Owner != killer.Player && !obj.Owner.IsNeutral && obj.Template.BuildCost > 0
            && killer.Player.RelationshipTo(obj.Owner) == Relationship.Enemies)
        {
            int reward = Math.Max(25, obj.Template.BuildCost * Settings.KillRewardPercent / 100);
            killer.Credits += reward;
            CreditsEarned?.Invoke(killer.Player, reward);
            if (victim != null)
                killer.Kills++;
        }
        if (victim == null)
            return;
        victim.UnitId = 0;
        victim.Deaths++;
        victim.RespawnFrame = game.Frame + (int)(Settings.RespawnSeconds * LogicTime.FramesPerSecond);
        if (Settings.Lives > 0 && victim.Deaths >= Settings.Lives)
            victim.Out = true;
        // A flag carrier drops the flag where they fell.
        foreach (var flag in Flags.Where(f => f.Carrier == victim))
        {
            flag.Carrier = null;
            flag.Position = obj.Position;
            flag.DroppedFrame = game.Frame;
            victim.HasFlag = false;
        }
    }

    // ---- Each frame ----

    public void Update()
    {
        foreach (var p in Players)
        {
            if (p.IsComputer)
            {
                if (CanSpawn(p) && ChooseComputerUnit(p) is { } choice)
                    Spawn(p, choice);
                Think(p);
            }
            Steer(p);
        }
        switch (Settings.Mode)
        {
            case SsrMode.ZoneDefense:
                UpdateZones();
                break;
            case SsrMode.CaptureTheFlag:
                UpdateFlags();
                break;
        }
        // Everyone out of lives but one team (or player): the match is over.
        if (WinningTeam == null && Settings.Lives > 0 && Settings.Mode != SsrMode.Classic)
        {
            var standing = Players.Where(p => !p.Out).Select(p => Settings.Mode == SsrMode.Freeplay ? p.Player.Index : p.Team).Distinct().ToList();
            if (standing.Count == 1 && Players.Count > 1)
                WinningTeam = Settings.Mode == SsrMode.Freeplay ? Players.First(p => !p.Out).Team : standing[0];
        }
    }

    /// <summary>Moves the unit as steered and fires its weapons at the aim.</summary>
    void Steer(SsrPlayer p)
    {
        if (UnitOf(p) is not { AI: { } ai } unit)
            return;
        // A person drives tank-style: Move.Y forward (1) or back (-1) along the facing, Move.X turns (1 left, -1 right).
        // The computer moves toward its destination.
        bool driven = !p.IsComputer;
        Vector3? goal = driven ? null : p.Destination;
        if (driven)
        {
            ai.Drive(p.Move.Y, p.Move.X);
        }
        else if (goal is { } to)
        {
            bool changed = p.LastOrder is not { } last || Vector3.DistanceSquared(last, to) > 30 * 30 || game.Frame - p.LastOrderFrame > 10;
            if (changed)
            {
                ai.MoveTo(to, CommandSource.Player);
                p.LastOrder = to;
                p.LastOrderFrame = game.Frame;
            }
        }
        else if (p.LastOrder != null)
        {
            ai.Stop(CommandSource.Player);
            p.LastOrder = null;
        }
        // Aiming and firing.
        var target = p.AimTarget != 0 && game.FindObject(p.AimTarget) is { IsEffectivelyDead: false } t && t != unit ? t : null;
        var aim = target?.Position ?? p.Aim;
        float angle = MathF.Atan2(aim.Y - unit.Position.Y, aim.X - unit.Position.X);
        foreach (var turret in ai.Turrets)
            turret.AimAt(angle, unit.Orientation, 0.05f, game.Frame);
        // A unit without a turret standing still swings round to shoot (not while it is being driven by the keys).
        if (ai.Turrets.Count == 0 && goal == null && p.Move == Vector2.Zero && (p.FirePrimary || p.FireSecondary))
            unit.SetOrientation(MathUtil.TurnTowards(unit.Orientation, angle, 0.25f));
        Fire(unit, p.FirePrimary ? Combat.WeaponSlot.Primary : null, target, aim);
        Fire(unit, p.FireSecondary ? Combat.WeaponSlot.Secondary : null, target, aim);
    }

    static void Fire(GameObject unit, Combat.WeaponSlot? slot, GameObject? target, Vector3 aim)
    {
        if (slot is not { } s || unit.Weapons.Slots[(int)s] is not { } weapon || weapon.Status != Combat.WeaponStatus.ReadyToFire)
            return;
        float range = weapon.AttackRange;
        if (target != null && MathUtil.Distance2D(target.Position, unit.Position) <= range + target.Radius)
        {
            weapon.FireAt(target);
            return;
        }
        // At the aim point, or as far as the weapon reaches toward it.
        var flat = new Vector2(aim.X - unit.Position.X, aim.Y - unit.Position.Y);
        if (flat.Length() > range && flat.LengthSquared() > 0)
            flat = Vector2.Normalize(flat) * range;
        var point = unit.Position + new Vector3(flat.X, flat.Y, 0);
        weapon.FireAtPosition(point with { Z = unit.Game.Terrain.GroundHeight(point) });
    }

    // ---- The computer's players ----

    string? ChooseComputerUnit(SsrPlayer p)
    {
        if (p.Faction is not { Units.Count: > 0 } faction)
            return null;
        // Better units as it earns: any it can afford (the cheaper ones free), and an upgrade now and then.
        int budget = 800 + p.Credits - p.Spent;
        // (Only units that can hurt other units: no hacker, sentry drone or mind-controller as a computer's fighter.)
        var allowed = faction.Units.Where(u => Settings.Allowed(u) && (u.Category != SsrCategory.Naval || HasWater)).ToList();
        if (allowed.Count == 0)
            return null;
        var affordable = allowed.Where(u => u.Cost <= budget && u.Power > 0).ToList();
        if (affordable.Count == 0)
            affordable = allowed.Where(u => u.Power > 0).OrderBy(u => u.Cost).Take(1).DefaultIfEmpty(allowed[0]).ToList();
        affordable.Sort((a, b) => a.Power.CompareTo(b.Power));
        foreach (int i in Enumerable.Range(0, 3))
        {
            int level = i == 0 ? p.WeaponLevel : i == 1 ? p.ArmorLevel : p.SpeedLevel;
            if (level < 5 && p.Credits - p.Spent >= UpgradeCost(level) * 2 && game.Random.Chance(0.5f))
            {
                p.Spent += UpgradeCost(level);
                if (i == 0) p.WeaponLevel++; else if (i == 1) p.ArmorLevel++; else p.SpeedLevel++;
            }
        }
        // Mostly the strongest it can afford, sometimes something else.
        var pick = game.Random.Chance(0.6f) ? affordable[^1] : affordable[game.Random.Int(0, affordable.Count - 1)];
        return pick.Template;
    }

    void Think(SsrPlayer p)
    {
        if (UnitOf(p) is not { } unit || game.Frame < p.NextThink)
            return;
        p.NextThink = game.Frame + LogicTime.FramesPerSecond / 2;
        float range = unit.Weapons.All.Select(w => w.AttackRange).DefaultIfEmpty(100).Max();
        // The nearest enemy it can see.
        GameObject? enemy = null;
        float nearest = 700;
        foreach (var o in game.ObjectsInRadius(unit.Position, 700))
            if (!o.IsEffectivelyDead && o.Owner != p.Player && !o.Owner.IsNeutral && p.Player.RelationshipTo(o.Owner) == Relationship.Enemies
                && (o.IsMobile || o.IsKindOf(Kind.Structure)) && !o.IsKindOf("UNATTACKABLE") && MathUtil.Distance2D(o.Position, unit.Position) is var d && d < nearest)
            {
                enemy = o;
                nearest = d;
            }
        p.FirePrimary = enemy != null && nearest <= range * 1.05f;
        p.FireSecondary = p.FirePrimary && unit.Weapons.Slots[(int)Combat.WeaponSlot.Secondary] != null;
        p.AimTarget = enemy?.Id ?? 0;
        if (enemy != null)
            p.Aim = enemy.Position;
        // Where to go. Freeplay and Classic: after the enemy (stopping in range). The team modes put the goal first: an
        // attacker heads for it, shooting what comes in range on the way; a defender (a third of a team) holds home and
        // goes only after intruders.
        var goal = Goal(p, unit);
        bool objective = Settings.Mode is SsrMode.ZoneDefense or SsrMode.CaptureTheFlag;
        if (enemy != null && (!objective || p.Defender && HomeDistance(p, enemy.Position) < Settings.ZoneRadius * 1.6f)
            && (goal == null || !objective || p.Defender))
            p.Destination = nearest > range * 0.8f ? enemy.Position : null;
        else
            p.Destination = goal;
        p.Move = Vector2.Zero;
    }

    Vector3? Goal(SsrPlayer p, GameObject unit)
    {
        int other = 1 - p.Team;
        switch (Settings.Mode)
        {
            case SsrMode.ZoneDefense:
            {
                // A third hold their own zones, the rest go for the other team's, spread over them.
                int k = Players.Where(o => o.Team == p.Team).ToList().IndexOf(p);
                p.Defender = k % 3 == 2;
                var zones = Zones.Where(z => z.Team == (p.Defender ? p.Team : other)).Select(z => z.Center).ToList();
                var to = zones.Count > 0 ? zones[k % zones.Count] : TeamBases[p.Defender ? p.Team : other];
                return Near(to, Settings.ZoneRadius * 0.5f, unit);
            }
            case SsrMode.CaptureTheFlag:
                p.Defender = Players.Where(o => o.Team == p.Team).ToList().IndexOf(p) % 3 == 2;
                if (p.HasFlag)
                    return TeamBases[p.Team];
                var mine = Flags.FirstOrDefault(f => f.Team == p.Team);
                if (mine is { AtHome: false })
                    return mine.Carrier != null && UnitOf(mine.Carrier) is { } thief ? thief.Position : mine.Position;
                if (p.Defender)
                    return Near(TeamBases[p.Team], 80, unit);
                return Flags.FirstOrDefault(f => f.Team == other) is { } theirs ? theirs.Carrier != null && UnitOf(theirs.Carrier) is { } friend ? friend.Position : theirs.Position : null;
            case SsrMode.Classic:
                // Guarding home for two minutes, then raiding the nearest enemy base.
                if (game.Frame > 2 * 60 * LogicTime.FramesPerSecond
                    && game.Objects.Where(o => !o.IsEffectivelyDead && o.IsKindOf(Kind.Structure) && p.Player.RelationshipTo(o.Owner) == Relationship.Enemies && !o.Owner.IsNeutral)
                        .OrderBy(o => MathUtil.Distance2D(o.Position, unit.Position)).FirstOrDefault() is { } raid)
                    return raid.Position;
                return p.Player.StartPosition is { } home && MathUtil.Distance2D(home, unit.Position) > 500 ? home : null;
            default:
                // Freeplay: toward the nearest other player's unit, wherever it is.
                return Players.Where(o => o != p).Select(UnitOf).OfType<GameObject>().OrderBy(o => MathUtil.Distance2D(o.Position, unit.Position))
                    .Select(o => (Vector3?)o.Position).FirstOrDefault();
        }
    }

    Vector3 Near(Vector3 point, float spread, GameObject unit) =>
        MathUtil.Distance2D(point, unit.Position) < spread ? unit.Position : point;

    /// <summary>How far a point is from what the player's team defends (its nearest zone, or its base).</summary>
    float HomeDistance(SsrPlayer p, Vector3 point) =>
        Zones.Where(z => z.Team == p.Team).Select(z => MathUtil.Distance2D(z.Center, point)).DefaultIfEmpty(MathUtil.Distance2D(TeamBases[p.Team], point)).Min();

    // ---- Zone Defense ----

    int nextZoneTick;

    /// <summary>
    /// Each team's zones: a row across the way from its base to the middle of the map, far enough out that nobody comes
    /// in on one (spawns keep clear of them too), each moved onto open ground.
    /// </summary>
    void PlaceZones()
    {
        float r = Settings.ZoneRadius, w = game.Terrain.WorldWidth, h = game.Terrain.WorldHeight;
        var center = new Vector3(w / 2, h / 2, 0);
        for (int team = 0; team < 2; team++)
        {
            var home = TeamBases[team];
            var toward = new Vector2(center.X - home.X, center.Y - home.Y);
            float length = toward.Length();
            var dir = length > 1 ? toward / length : new Vector2(team == 0 ? 1 : -1, 0);
            var across = new Vector2(-dir.Y, dir.X);
            float along = Math.Max(r + 320, length * 0.55f);
            int n = Math.Max(1, Settings.ZonesPerTeam);
            for (int k = 0; k < n; k++)
            {
                var flat = new Vector2(home.X, home.Y) + dir * along + across * ((k - (n - 1) / 2f) * r * 2.6f);
                var spot = new Vector3(Math.Clamp(flat.X, r + 40, w - r - 40), Math.Clamp(flat.Y, r + 40, h - r - 40), 0);
                // Onto open ground close by, if it isn't.
                for (int tries = 0; tries < 60 && !OpenGround(spot); tries++)
                {
                    float a = tries * 2.4f, d = 20 + tries * 6;
                    var moved = spot + new Vector3(MathF.Cos(a) * d, MathF.Sin(a) * d, 0);
                    if (OpenGround(moved))
                    {
                        spot = moved;
                        break;
                    }
                }
                Zones.Add((team, spot with { Z = game.Terrain.GroundHeight(spot) }));
            }
        }
    }

    void UpdateZones()
    {
        if (WinningTeam != null || game.Frame < nextZoneTick)
            return;
        nextZoneTick = game.Frame + LogicTime.FramesPerSecond;
        foreach (var p in Players)
            if (UnitOf(p) is { } unit && Zones.Any(z => z.Team != p.Team && MathUtil.Distance2D(unit.Position, z.Center) <= Settings.ZoneRadius))
                TeamScores[p.Team]++;
        for (int team = 0; team < 2; team++)
            if (TeamScores[team] >= Settings.ZoneScoreLimit)
                WinningTeam = team;
    }

    // ---- Capture the Flag ----

    void UpdateFlags()
    {
        foreach (var flag in Flags)
        {
            if (flag.Carrier is { } carrier && UnitOf(carrier) is { } carrierUnit)
                flag.Position = carrierUnit.Position;
            else if (!flag.AtHome && flag.DroppedFrame >= 0 && game.Frame - flag.DroppedFrame > Settings.FlagReturnSeconds * LogicTime.FramesPerSecond)
                ReturnHome(flag);
            if (flag.ObjectId != 0 && game.FindObject(flag.ObjectId) is { } shown)
                shown.SetPosition(flag.Position with { Z = game.Terrain.GroundHeight(flag.Position) + (flag.Carrier != null ? 12 : 0) });
            if (flag.Carrier != null)
                continue;
            foreach (var p in Players)
            {
                if (UnitOf(p) is not { } unit || MathUtil.Distance2D(unit.Position, flag.Position) > 40)
                    continue;
                if (p.Team != flag.Team)
                {
                    // Taken.
                    flag.Carrier = p;
                    flag.DroppedFrame = -1;
                    FlagTakes++;
                    p.HasFlag = true;
                    break;
                }
                if (!flag.AtHome)
                {
                    // Its own team brings it back.
                    ReturnHome(flag);
                    break;
                }
            }
        }
        // A carrier home with its own flag there: a capture.
        foreach (var flag in Flags.Where(f => f.Carrier != null).ToList())
        {
            var carrier = flag.Carrier!;
            var own = Flags.First(f => f.Team == carrier.Team);
            if (UnitOf(carrier) is { } unit && own.AtHome && MathUtil.Distance2D(unit.Position, own.Home) < 60)
            {
                TeamScores[carrier.Team]++;
                carrier.HasFlag = false;
                ReturnHome(flag);
                if (TeamScores[carrier.Team] >= Settings.FlagCaptureLimit)
                    WinningTeam = carrier.Team;
            }
        }
    }

    static void ReturnHome(Flag flag)
    {
        if (flag.Carrier != null)
            flag.Carrier.HasFlag = false;
        flag.Carrier = null;
        flag.Position = flag.Home;
        flag.DroppedFrame = -1;
    }
}

/// <summary>
/// Sole Survivor Remix: what a player steers this frame: MoveX the turn (1 left, -1 right), MoveY the throttle (1 forward,
/// -1 back) along the unit's facing, where they aim
/// (a point, and the object under the mouse if any) and which triggers are held (Space primary, H secondary).
/// </summary>
public sealed record SsrInputCommand(int PlayerIndex, float MoveX, float MoveY, Vector3 Aim, uint AimTarget, bool FirePrimary, bool FireSecondary)
    : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (game.SoleSurvivor?.Players.FirstOrDefault(p => p.Player.Index == PlayerIndex && !p.IsComputer) is not { } p)
            return;
        p.Move = new Vector2(MoveX, MoveY);
        p.Destination = null;
        p.Aim = Aim;
        p.AimTarget = AimTarget;
        p.FirePrimary = FirePrimary;
        p.FireSecondary = FireSecondary;
    }
}
