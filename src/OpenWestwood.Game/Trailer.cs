using System.Numerics;
using OpenWestwood.Client;
using OpenWestwood.Client.InGame;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;

namespace OpenWestwood.Game;

/// <summary>
/// OpenWESTWOOD's trailer: six scripted battles (--trailer N), each played with no interface, the camera flown by the
/// scene, the map unexplored (black) until units see it and never fogged again, and every unit ordered by the
/// scene (no computer players). With --trailer-signal DIR the scene waits for DIR/go before it starts (so a recorder
/// can begin first), and writes DIR/ready when it is waiting and DIR/done when its time is up.
/// </summary>
static class Trailer
{
    /// <summary>A camera key: where it looks, its turn (radians), tilt and distance, at a time in the scene.</summary>
    public readonly record struct Shot(float At, Vector3 Target, float Yaw, float Pitch, float Distance);

    public static (MapFile Map, GameLogic Game, SceneDirector Director) Build(GameContent content, int number)
    {
        var (map, factions, seconds) = number switch
        {
            1 => ("d2k:the-great-flat", new[] { "FactionD2KHarkonnen" }, 14f),
            2 => (Environment.GetEnvironmentVariable("TRAILER_MAP2") ?? "kw:map_mp_2_black1", new[] { "FactionRA2Russians", "FactionKWNod" }, 15f),
            3 => ("td:morbid-aimless-poseidon", BattleFactions, 15f),
            4 => ("Maps/Green Pastures/Green Pastures.map", new[] { "FactionTSGDI" }, 5f),
            5 => ("d2k:the-great-flat", new[] { "FactionD2KAtreides", "FactionTSNod", "FactionD2KHarkonnen" }, 9f),
            6 => ("d2k:imperial-basin", BattleFactions, 45f),
            _ => throw new ArgumentException($"There is no trailer scene {number} (1-6)."),
        };
        var mapFile = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem, map);
        var slots = factions.Select((f, i) => new PlayerSlot($"P{i}", f, IsHuman: i == 0, StartPosition: 1 + i % 6, Color: Colors[i % Colors.Length])).ToList();
        var game = GameSetup.FromMap(mapFile, content.Rules, 4242, slots, startingCash: 0, models: content.Models);
        // Only what the scene places: no starting bases, no computer players, no victory screen.
        foreach (var player in game.Players.All.Where(p => !p.IsNeutral))
        {
            player.AI = null;
            foreach (var obj in game.ObjectsOwnedBy(player).ToList())
                game.DestroyObject(obj);
        }
        game.Victory.IsMultiplayer = false;
        if (Environment.GetEnvironmentVariable("TRAILER_SKIP") is { Length: > 0 } skip)
            foreach (var o in game.Objects.Where(o => o.Owner.IsNeutral && skip.Split(',').Any(s => o.Template.Name.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList())
                game.DestroyObject(o);
        var director = new SceneDirector(game, number, seconds);
        return (mapFile, game, director);
    }

    static readonly string[] BattleFactions =
    [
        "FactionRA2Arabs", "FactionRAGermany", "FactionTSGDI", "FactionTSNod", "FactionD2KAtreides",
        "FactionD2KHarkonnen", "FactionD2KOrdos", "FactionKWScrin", "FactionChina",
    ];

    static readonly uint[] Colors = [0xE02020, 0x2050FF, 0xF0D020, 0x20C040, 0xFF8000, 0xA030E0, 0x20D0D0, 0xFF60C0, 0xC0C0C0, 0x806040];

    /// <summary>Each battle faction's army (templates, how many of each).</summary>
    public static readonly Dictionary<string, (string Template, int Count)[]> Armies = new()
    {
        ["FactionRA2Arabs"] = [("RA2SOV_DESO", 4), ("RA2SOV_HTNK", 4), ("RA2SOV_E2", 4), ("RA2SOV_V3", 1)],
        ["FactionRAGermany"] = [("RAALL_2TNK", 4), ("RAALL_1TNK", 3), ("RAALL_ARTY", 2), ("RAALL_E1", 5)],
        ["FactionTSGDI"] = [("TSGDI_SONIC", 3), ("TSGDI_HVR", 2), ("TSGDI_E2", 4), ("TSGDI_E1", 4)],
        ["FactionTSNod"] = [("TSNOD_TTNK", 4), ("TSNOD_ART2", 1), ("TSNOD_BGGY", 2), ("TSNOD_CYBORG", 4)],
        ["FactionD2KAtreides"] = [("D2KATR_SONIC_TANK", 2), ("D2KATR_COMBAT_TANK_A", 4), ("D2KATR_TROOPER", 4), ("D2KATR_FREMEN", 2)],
        ["FactionD2KHarkonnen"] = [("D2KHAR_DEVASTATOR", 2), ("D2KHAR_COMBAT_TANK_H", 4), ("D2KHAR_MPSARDAUKAR", 4)],
        ["FactionD2KOrdos"] = [("D2KORD_COMBAT_TANK_O", 4), ("D2KORD_RAIDER", 3), ("D2KORD_SIEGE_TANK", 2), ("D2KORD_TROOPER", 3)],
        ["FactionKWScrin"] = [("KW_AlienAnnihilatorTripod", 2), ("KW_AlienSeekerTank", 3), ("KW_AlienDevourerTank", 2), ("KW_AlienShockTrooper", 4)],
        ["FactionChina"] = [("ChinaTankOverlord", 2), ("ChinaTankBattleMaster", 4), ("ChinaInfantryRedguard", 5), ("ChinaTankGattling", 2)],
    };
}

/// <summary>Runs one trailer scene: places the armies, gives the orders, flies the camera, keeps the shroud.</summary>
sealed class SceneDirector(GameLogic game, int number, float seconds)
{
    readonly List<(float At, Action Do)> events = new();
    readonly List<Trailer.Shot> shots = new();
    readonly HashSet<(int, int, int)> revealed = new();
    Player? viewer;
    string? signal;
    bool waiting, done;
    /// <summary>
    /// Seconds the scene runs before its first recorded frame: the orders are given then, so units are already on the
    /// move when the picture starts (the recorder cuts the clip from the scene's second 0).
    /// </summary>
    public const float Preroll = 1.5f;
    float clock = -Preroll;
    int framesBeforeReady = 120;
    /// <summary>Units the camera follows (their middle), and units kept marching together (at the slowest one's pace).</summary>
    List<GameObject>? follow, together;
    Vector3? followAt;

    public float Seconds => seconds;

    /// <summary>The scene's i'th side (its slot's player, not one of the map's own).</summary>
    Player P(int i) => game.Players.FindByNameKey($"player{i}") ?? throw new InvalidOperationException($"no player{i}");

    /// <summary>The scene's sides, in slot order.</summary>
    List<Player> Sides => Enumerable.Range(0, 32).Select(i => game.Players.FindByNameKey($"player{i}")).TakeWhile(p => p != null).Select(p => p!).ToList();

    Vector3 Ground(Vector3 at) => at with { Z = game.Terrain.GroundHeight(at.X, at.Y) };

    Vector3 Start(int n) => game.Waypoints.Find($"Player_{n}_Start")?.Position ?? new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);

    Vector3 Middle => new(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);

    /// <summary>Is a spot open ground (no cliff, no deep water)?</summary>
    bool Open(Vector3 at)
    {
        if (!game.Terrain.IsInsidePlayableArea(at.X, at.Y) || game.Terrain.IsUnderwater(at.X, at.Y, out _))
            return false;
        var (cx, cy) = game.Terrain.CellAt(at.X, at.Y);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (game.Terrain.IsCliffCell(cx + dx, cy + dy))
                    return false;
        return true;
    }

    /// <summary>The nearest open spot (spiralling out).</summary>
    Vector3 Nearest(Vector3 at)
    {
        if (Open(at))
            return at;
        for (float r = 10; r < 600; r += 10)
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.Tau / 16;
                var p = at + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0);
                if (Open(p))
                    return p;
            }
        return at;
    }

    /// <summary>
    /// An army in ranks facing <paramref name="towards"/>: <paramref name="units"/> in order, rows of
    /// <paramref name="perRow"/>, <paramref name="spacing"/> apart.
    /// </summary>
    List<GameObject> Army(Player owner, IEnumerable<(string Template, int Count)> units, Vector3 centre, Vector3 towards, int perRow = 6, float spacing = 32)
    {
        var skipUnits = Environment.GetEnvironmentVariable("TRAILER_NOUNIT") ?? "";
        var list = units.Where(u => skipUnits.Length == 0 || !skipUnits.Split(',').Any(s => u.Template.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(u => Enumerable.Repeat(u.Template, u.Count)).ToList();
        var forward = Vector3.Normalize((towards - centre) with { Z = 0 });
        var side = new Vector3(-forward.Y, forward.X, 0);
        float facing = MathF.Atan2(forward.Y, forward.X);
        var made = new List<GameObject>();
        for (int i = 0; i < list.Count; i++)
        {
            int row = i / perRow, column = i % perRow;
            int inRow = Math.Min(perRow, list.Count - row * perRow);
            var at = centre - forward * (row * spacing) + side * ((column - (inRow - 1) / 2f) * spacing);
            if (game.CreateObject(list[i], owner, Ground(Nearest(at)), facing) is { } obj)
                made.Add(obj);
            else
                Console.Error.WriteLine($"trailer: no such object {list[i]}");
        }
        return made;
    }

    static void AttackMove(IEnumerable<GameObject> units, Vector3 to)
    {
        foreach (var u in units)
            u.AI?.AttackMoveTo(to, OpenWestwood.Logic.Commands.CommandSource.Script);
    }

    static void MoveTo(IEnumerable<GameObject> units, Vector3 offset)
    {
        foreach (var u in units)
            u.AI?.MoveTo(u.Position + offset, OpenWestwood.Logic.Commands.CommandSource.Script);
    }

    void At(float time, Action action) => events.Add((time, action));

    /// <summary>
    /// A camera position at a time. On a Westwood game's map the camera keeps that game's own angle (north up, looking
    /// down as it did); only where it looks and how far out it is move.
    /// </summary>
    void Shot(float at, Vector3 target, float yawDegrees, float pitch, float distance)
    {
        if (OpenWestwood.Client.TiberianDawn.TdMaps.ViewPitch is { } original)
            (yawDegrees, pitch) = (-90, original);
        shots.Add(new Trailer.Shot(at, target, yawDegrees * MathF.PI / 180, pitch, distance));
    }

    /// <summary>Places everything that is there from the start, and plans the orders and the camera.</summary>
    public void Setup(GameScreen screen, string? signalFolder)
    {
        signal = signalFolder;
        viewer = P(0);
        screen.Cinematic = true;
        switch (number)
        {
            case 1: SceneHarkonnenMarch(); break;
            case 2: SceneRussianAssault(); break;
            case 3: SceneBigBattle(armiesPerFaction: 1, radius: 430, reinforcements: []); break;
            case 4: SceneRainMarch(screen); break;
            case 5: SceneHarvesterAmbush(); break;
            case 6: SceneBigBattle(armiesPerFaction: 2, radius: 650, reinforcements: [11, 22, 33]); break;
        }
        shots.Sort((a, b) => a.At.CompareTo(b.At));
        waiting = signal != null;
        ApplyCamera(screen, 0);
    }

    // ---- The scenes ----

    /// <summary>1: Harkonnen Sardaukar, combat tanks and devastators march across a spice field.</summary>
    void SceneHarkonnenMarch()
    {
        var spice = SpiceField();
        var dir = Vector3.Normalize(new Vector3(1, 0.35f, 0));
        var start = spice - dir * 330;
        var army = Army(P(0), [("D2KHAR_MPSARDAUKAR", 6), ("D2KHAR_COMBAT_TANK_H", 5), ("D2KHAR_DEVASTATOR", 3)], start, start + dir, perRow: 5, spacing: 36);
        At(0, () => MoveTo(army, dir * 900));
        follow = together = army;
        Shot(0, start, 200, 0.55f, 480);
        Shot(7, start, 215, 0.5f, 440);
        Shot(14, start, 235, 0.45f, 400);
    }

    /// <summary>Where the spice is thickest (the supply piles' busiest spot).</summary>
    Vector3 SpiceField()
    {
        var piles = game.Objects.Where(o => o.IsKindOf("SUPPLY_SOURCE")).Select(o => o.Position).ToList();
        if (piles.Count == 0)
            return Middle;
        return piles.OrderByDescending(p => piles.Count(q => Vector3.DistanceSquared(p, q) < 220 * 220)).First();
    }

    /// <summary>2: a big Russian army storms a Nod base from Kane's Wrath.</summary>
    void SceneRussianAssault()
    {
        var russians = P(0);
        var nod = P(1);
        var basePos = Nearest(Start(2));
        var toward = Vector3.Normalize((Start(1) - basePos) with { Z = 0 });
        var side = new Vector3(-toward.Y, toward.X, 0);
        void Build(string template, float forward, float across)
        {
            if (Environment.GetEnvironmentVariable("TRAILER_NOBUILD") is { Length: > 0 } nb && template.Contains(nb, StringComparison.OrdinalIgnoreCase))
                return;
            var at = Ground(Nearest(basePos + toward * forward + side * across));
            if (game.CreateObject(template, nod, at, MathF.Atan2(toward.Y, toward.X)) is { } b)
                game.TreatAsBuilt(b);
        }
        Build("KW_NODConstructionYard", 0, 0);
        Build("KW_NODPowerPlant", -40, -130);
        Build("KW_NODPowerPlant", -40, 130);
        Build("KW_NODRefinery", -150, 0);
        Build("KW_NODHandOfNOD", 60, -150);
        Build("KW_NODOperationsCenter", 60, 150);
        Build("KW_NODPowerPlant", -150, 150);
        Build("KW_NODPowerPlant", -150, -150);
        Build("KW_NODPowerPlant", -260, -70);
        Build("KW_NODPowerPlant", -260, 70);
        Build("KW_NODPowerPlant", -260, 200);
        Build("KW_NODPowerPlant", -260, -200);
        Build("KW_NODObelisk", 170, -70);
        Build("KW_NODObelisk", 170, 70);
        Build("KW_NODLaserCannon", 150, -200);
        Build("KW_NODLaserCannon", 150, 200);
        Build("KW_NODShredderTurretHub", 200, 0);
        // Its guard: infantry by the gates (fast vehicles would only lead the attack away from the base).
        var defenders = Army(nod, [("KW_NODMilitant", 8), ("KW_NODMilitantRocket", 4)], basePos + toward * 120, basePos + toward * 400, perRow: 6, spacing: 26);
        var armyAt = Nearest(basePos + toward * Math.Min(640, Vector3.Distance(Start(1), basePos) * 0.6f));
        // A clear field for the assault: the map's props between the army and the base (wrecks, ruins, fences) go.
        foreach (var o in game.Objects.Where(o => o.Owner.IsNeutral && !o.Template.Name.StartsWith("KW_Tiberium")).ToList())
        {
            var ab = armyAt - basePos;
            float s = Math.Clamp(Vector3.Dot(o.Position - basePos, ab) / ab.LengthSquared(), 0, 1);
            if (Vector3.Distance((o.Position - (basePos + ab * s)) with { Z = 0 }, Vector3.Zero) < 320)
                game.DestroyObject(o);
        }
        var army = Army(russians, [("RA2SOV_APOC", 4), ("RA2SOV_HTNK", 12), ("RA2SOV_FLAKT", 2), ("RA2SOV_V3", 3), ("RA2SOV_SHK", 6), ("RA2SOV_E2", 12)],
            armyAt, basePos, perRow: 8, spacing: 34);
        // Kane's Wrath's health is on a scale about ten times Red Alert 2's (and its armour doesn't know Red Alert 2's
        // damage types): the Nod base is brought down to Red Alert 2's scale so the assault can break it.
        foreach (var o in game.ObjectsOwnedBy(nod).ToList())
            o.Body?.SetMaxHealth(o.MaxHealth * 0.08f, Logic.Modules.Bodies.MaxHealthChange.FullyHeal);
        // Kane's Wrath's defences out-range Red Alert 2's tanks (its scale is larger): the attackers get the reach to answer.
        foreach (var u in army)
        {
            u.RangeBonus = 1.6f;
            u.DamageBonus = 4;
        }
        At(0, () => AttackMove(army, basePos));
        if (Environment.GetEnvironmentVariable("TRAILER_DEBUG") != null)
            foreach (var g in game.ObjectsInRadius(armyAt + toward * 150, 700).Where(o => o.Owner.IsNeutral).GroupBy(o => o.Template.Name))
                Console.WriteLine($"near: {g.Key} x{g.Count()} r={g.First().Radius} at {g.First().Position}");

        // From the side (the map's edge behind the attackers is high, unexplored ground).
        var inward = Vector3.Dot(side, Middle - basePos) >= 0 ? side : -side;
        Shot(0, Vector3.Lerp(armyAt, basePos, 0.3f), Yaw(inward) + 10, 0.72f, 640);
        Shot(7, (armyAt + basePos) / 2, Yaw(inward) + 8, 0.72f, 640);
        Shot(15, basePos + toward * 140, Yaw(inward) - 6, 0.78f, 700);
    }

    /// <summary>The camera turn that looks along a direction.</summary>
    static float Yaw(Vector3 direction) => MathF.Atan2(direction.Y, direction.X) * 180 / MathF.PI + 180;

    /// <summary>3 and 6: every faction against every other in one battle (with waves of reinforcements in 6).</summary>
    void SceneBigBattle(int armiesPerFaction, float radius, float[] reinforcements)
    {
        var centre = Nearest(Middle);
        var players = Sides;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            float angle = i * MathF.Tau / players.Count;
            var at = centre + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * radius;
            var army = new List<GameObject>();
            for (int k = 0; k < armiesPerFaction; k++)
                army.AddRange(Army(player, Trailer.Armies[player.FactionName], at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * (k * 140), centre, perRow: 6, spacing: 30));
            At(0, () => AttackMove(army, centre));
            foreach (float wave in reinforcements)
                At(wave, () =>
                {
                    var fresh = Army(player, Trailer.Armies[player.FactionName], centre + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * (radius + 120), centre, perRow: 6, spacing: 30);
                    AttackMove(fresh, centre);
                });
        }
        if (number == 3)
        {
            Shot(0, centre, 225, 0.75f, 1300);
            Shot(8, centre, 250, 0.62f, 950);
            Shot(15, centre + new Vector3(40, 30, 0), 275, 0.55f, 720);
        }
        else
        {
            // A tour of the battle: wide, down into the fighting, round it, and wide again as reinforcements arrive.
            Shot(0, centre, 200, 0.8f, 1700);
            Shot(6, centre, 220, 0.6f, 1100);
            Shot(12, centre + new Vector3(250, 0, 0), 250, 0.5f, 650);
            Shot(18, centre + new Vector3(0, 250, 0), 300, 0.5f, 600);
            Shot(24, centre + new Vector3(-250, 0, 0), 350, 0.52f, 650);
            Shot(30, centre + new Vector3(0, -200, 0), 400, 0.55f, 750);
            Shot(37, centre, 440, 0.65f, 1150);
            Shot(45, centre, 470, 0.8f, 1700);
        }
    }

    /// <summary>4: twenty Tiberian Sun GDI soldiers march through the rain on a Generals map.</summary>
    void SceneRainMarch(GameScreen screen)
    {
        screen.World.Raining = true;
        var start = Nearest(Middle - new Vector3(120, 60, 0));
        var dir = Vector3.Normalize(new Vector3(1, 0.5f, 0));
        var squad = Army(P(0), [("TSGDI_E1", 14), ("TSGDI_E2", 6)], start, start + dir, perRow: 5, spacing: 18);
        At(0, () => MoveTo(squad, dir * 400));
        follow = together = squad;
        Shot(0, start + dir * 20, 205, 0.45f, 300);
        Shot(5, start + dir * 120, 215, 0.42f, 280);
    }

    /// <summary>5: a line of harvesters from five games, ambushed by Tiberian Sun Nod and fifteen Sardaukar.</summary>
    void SceneHarvesterAmbush()
    {
        var harvesters = P(0);
        var start = Nearest(Middle - new Vector3(320, 0, 0));
        var dir = new Vector3(1, 0, 0);
        var line = new List<GameObject>();
        string[] kinds = ["D2KATR_HARVESTER", "TDGDI_HARV", "RA2SOV_HARV", "RASOV_HARV", "TSGDI_HARV"];
        for (int i = 0; i < kinds.Length; i++)
            if (game.CreateObject(kinds[i], harvesters, Ground(Nearest(start - dir * (i * 70))), 0) is { } h)
                line.Add(h);
        var road = start + dir * 220;
        var nod = Army(P(1), [("TSNOD_TTNK", 3), ("TSNOD_BGGY", 2), ("TSNOD_BIKE", 3), ("TSNOD_E1", 4)], road + new Vector3(0, 330, 0), road, perRow: 6, spacing: 30);
        var sardaukar = Army(P(2), [("D2KHAR_MPSARDAUKAR", 15)], road + new Vector3(60, -300, 0), road, perRow: 5, spacing: 24);
        At(0, () => MoveTo(line, dir * 800));
        together = line;
        At(0, () => { AttackMove(nod, road + new Vector3(80, 0, 0)); AttackMove(sardaukar, road + new Vector3(80, 0, 0)); });
        // The ambushers go for the harvesters (not for each other).
        P(1).SetRelationship(P(2), Relationship.Allies);
        P(2).SetRelationship(P(1), Relationship.Allies);
        Shot(0, start + dir * 60, 180, 0.5f, 560);
        Shot(9, start + dir * 260, 195, 0.55f, 600);
    }

    // ---- Running ----

    public void Tick(GameScreen screen, float dt)
    {
        // A slow frame (loading art) mustn't skip the scene ahead.
        dt = Math.Min(dt, 1 / 30f);
        RevealWhatUnitsSee();
        if (waiting)
        {
            if (--framesBeforeReady == 0 && signal != null)
                File.WriteAllText(Path.Combine(signal, "ready"), "");
            if (framesBeforeReady <= 0 && signal != null && File.Exists(Path.Combine(signal, "go")))
                waiting = false;
            else
                return;
        }
        float before = clock;
        clock += dt;
        KeepTogether();
        KeepFighting(before);
        foreach (var e in events)
            if (e.At - Preroll >= before && e.At - Preroll < clock)
                e.Do();
        ApplyCamera(screen, Math.Max(0, clock));
        if (Environment.GetEnvironmentVariable("TRAILER_LOG") != null && (int)(before / 2) != (int)(clock / 2))
            Console.WriteLine($"t={clock:0}: " + string.Join(" | ", Sides.Select(p =>
            {
                var mine = game.ObjectsOwnedBy(p).Where(o => !o.IsEffectivelyDead).ToList();
                if (p == P(0))
                    foreach (var o in mine.Where(o => o.AI?.CurrentVictim != null).Take(6))
                        Console.WriteLine($"   {o.Template.Name} -> {o.AI!.CurrentVictim!.Template.Name} d={Vector3.Distance(o.Position, o.AI.CurrentVictim.Position):0} range={o.Weapons.Current?.Template.AttackRange} weapon={o.Weapons.Current?.Template.Name} reload={o.Weapons.Current?.Status} victimHp={o.AI.CurrentVictim.Health:0}/{o.AI.CurrentVictim.MaxHealth:0} dmg={o.Weapons.Current?.Template.PrimaryDamage}");
                int targeting = mine.Count(o => o.AI?.CurrentVictim != null), fired = mine.Count(o => o.Weapons.Current is { } w && game.Frame - w.LastFireFrame < 60);
                string states = string.Join(",", mine.Where(o => o.AI != null).GroupBy(o => o.AI!.State).Select(g => $"{g.Key}:{g.Count()}"));
                return $"{p.FactionName}: {mine.Count} units hp {mine.Sum(o => o.Health):0} targeting {targeting} fired {fired} [{states}]";
            })));
        if (!done && clock >= seconds)
        {
            done = true;
            if (signal != null)
                File.WriteAllText(Path.Combine(signal, "done"), "");
        }
    }

    readonly Dictionary<uint, float> lastSentToFight = new();
    const int FightOrdersPerFrame = 3;

    /// <summary>
    /// The battle scenes have no idle moments: any armed unit standing about (its attack-move done, no enemy in reach)
    /// is sent at the nearest enemy, at most every half second. Only a few are sent a frame, so their path searches
    /// don't all land on one frame and stall the picture.
    /// </summary>
    void KeepFighting(float before)
    {
        if (number is 1 or 4 || clock < 0)
            return;
        var enemies = game.Objects.Where(o => !o.IsDestroyed && !o.IsEffectivelyDead && !o.Owner.IsNeutral).ToList();
        int sent = 0;
        foreach (var u in enemies.OrderBy(u => lastSentToFight.GetValueOrDefault(u.Id, float.MinValue)))
        {
            if (sent >= FightOrdersPerFrame)
                break;
            if (clock - lastSentToFight.GetValueOrDefault(u.Id, float.MinValue) < 0.5f)
                continue;
            if (u.AI is not { IsIdle: true } ai || u.Weapons.Current == null || u.IsKindOf("STRUCTURE") || (together?.Contains(u) ?? false))
                continue;
            GameObject? nearest = null;
            float best = float.MaxValue;
            foreach (var e in enemies)
            {
                if (!u.Owner.IsEnemyOf(e.Owner))
                    continue;
                float d = Vector3.DistanceSquared(u.Position, e.Position);
                if (d < best)
                {
                    best = d;
                    nearest = e;
                }
            }
            if (nearest != null)
            {
                ai.AttackMoveTo(nearest.Position, OpenWestwood.Logic.Commands.CommandSource.Script);
                lastSentToFight[u.Id] = clock;
                sent++;
            }
        }
    }

    /// <summary>A marching group keeps together: everyone at the pace of the slowest.</summary>
    void KeepTogether()
    {
        if (together == null)
            return;
        var moving = together.Where(u => !u.IsDestroyed && u.AI?.Locomotor is { Speed: > 0 }).ToList();
        if (moving.Count == 0)
            return;
        float slowest = moving.Min(u => u.AI!.Locomotor!.Speed);
        foreach (var u in moving)
            u.SpeedBonus = slowest / u.AI!.Locomotor!.Speed;
    }

    /// <summary>No fog of war, but an unexplored map: wherever a unit has seen stays visible to the viewer.</summary>
    void RevealWhatUnitsSee()
    {
        if (viewer == null)
            return;
        uint mask = 1u << viewer.Index;
        foreach (var o in game.Objects)
        {
            if (o.IsDestroyed || o.Owner.IsNeutral || o.VisionRange <= 0)
                continue;
            var key = ((int)(o.Position.X / 30), (int)(o.Position.Y / 30), (int)o.VisionRange);
            if (revealed.Add(key))
                game.Shroud.DoShroudReveal(new Vector2(o.Position.X, o.Position.Y), o.VisionRange, mask);
        }
    }

    void ApplyCamera(GameScreen screen, float time)
    {
        if (shots.Count == 0)
            return;
        var a = shots[0];
        var b = shots[^1];
        for (int i = 0; i + 1 < shots.Count; i++)
            if (time >= shots[i].At && time <= shots[i + 1].At)
            {
                a = shots[i];
                b = shots[i + 1];
                break;
            }
        float t = b.At > a.At ? Math.Clamp((time - a.At) / (b.At - a.At), 0, 1) : 1;
        t = t * t * (3 - 2 * t); // ease in and out
        var camera = screen.World.Camera;
        var target = Vector3.Lerp(a.Target, b.Target, t);
        if (follow?.Where(u => !u.IsDestroyed && !u.IsEffectivelyDead).ToList() is { Count: > 0 } alive)
        {
            // The middle of the followed group, eased so the camera glides.
            var middle = alive.Aggregate(Vector3.Zero, (s, u) => s + u.Position) / alive.Count;
            followAt = followAt is { } f ? Vector3.Lerp(f, middle, 0.04f) : middle;
            target = followAt.Value;
        }
        camera.Target = Ground(target);
        camera.Yaw = a.Yaw + (b.Yaw - a.Yaw) * t;
        camera.Pitch = a.Pitch + (b.Pitch - a.Pitch) * t;
        camera.Distance = a.Distance + (b.Distance - a.Distance) * t;
    }
}
