using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

// OpenGZH additions for Dune 2000 maps and factions (the Dune 2000 data's sandworm, thumper and spice bloom
// rules: ActorSpawnManager, Sandworm, AttractsWorms, SpiceBloom). Distances are in TdScale.Cell cells.

/// <summary>
/// The map's worm spawners (ActorSpawnManager with Actors: sandworm): the first worm comes after InitialDelay
/// (9000 ticks: 6 minutes), then one each SpawnInterval (6000 ticks: 4 minutes) while fewer than Maximum (4)
/// are about, each from a spawner picked at random.
/// </summary>
public sealed class D2kWormSpawner : Module, IUpdateModule
{
    sealed class Census
    {
        public int NextSpawn = -1;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameLogic, Census> census = new();

    float InitialDelaySeconds => FloatSetting("InitialDelay", 360);
    float IntervalSeconds => FloatSetting("SpawnInterval", 240);
    int Maximum => IntSetting("Maximum", 4);
    string Worm => Setting("Worm") is { Length: > 0 } w ? w : "D2K_Sandworm";

    public int Update()
    {
        // Worms belong to Dune 2000 maps only: no sand, no worms.
        if (!Game.Terrain.HasSand)
            return LogicTime.SleepForever;
        var state = census.GetOrCreateValue(Game);
        if (state.NextSpawn < 0)
            state.NextSpawn = Game.Frame + (int)(InitialDelaySeconds * LogicTime.FramesPerSecond);
        if (Game.Frame < state.NextSpawn)
            return LogicTime.FramesPerSecond;
        state.NextSpawn = Game.Frame + (int)(IntervalSeconds * LogicTime.FramesPerSecond);
        if (Game.Objects.Count(o => !o.IsEffectivelyDead && o.Template.Name == Worm) >= Maximum)
            return LogicTime.FramesPerSecond;
        var spawners = Game.Objects.Where(o => o.FindModule<D2kWormSpawner>() != null).ToList();
        var from = spawners[Game.Random.Int(0, spawners.Count - 1)];
        if (Game.Templates.Find(Worm) is { } worm)
        {
            Game.CreateObject(worm, Game.Players.Neutral, from.Position with { Z = Game.Terrain.GroundHeight(from.Position) });
            foreach (var player in Game.Players.Active)
                Game.Events.Eva("D2kWormSign", player);
        }
        return LogicTime.FramesPerSecond;
    }
}

/// <summary>
/// A sandworm (Sandworm, AttackSwallow, WormJaw): it keeps to the sand, goes for whatever draws it most
/// (AttractsWorms: harvesters, MCVs and carryalls by their Intensity, a deployed thumper by Intensity and
/// Falloff), otherwise wanders within WanderMoveRadius (5 cells); a vehicle on the sand within the jaw's reach
/// (1.5 cells) is swallowed whole (not infantry, not buildings).
/// </summary>
public sealed class D2kSandworm : Module, IUpdateModule
{
    float Cell => TdScale.Cell;
    float WanderRadius => FloatSetting("WanderMoveRadius", 5);
    float JawRange => FloatSetting("JawRange", 1.5f);

    /// <summary>Seconds left of the swallow (the client shows the mouth).</summary>
    public bool IsEating => Game.Frame < eatingUntil;
    int eatingUntil;
    int nextThink;

    public int Update()
    {
        if (Owner.IsEffectivelyDead || Owner.AI is not { } ai)
            return LogicTime.SleepForever;
        if (IsEating || Game.Frame < nextThink)
            return 3;
        nextThink = Game.Frame + LogicTime.FramesPerSecond / 2;

        // Swallow a vehicle on the sand within reach.
        foreach (var victim in Game.ObjectsInRadius(Owner.Position, JawRange * Cell))
        {
            if (victim == Owner || victim.IsEffectivelyDead || !victim.IsMobile || victim.IsKindOf(Kind.Infantry) || victim.IsKindOf(Kind.Structure)
                || victim.IsKindOf(Kind.Aircraft) && victim.IsAboveTerrain || victim.IsContained || !Game.Terrain.IsSand(victim.Position.X, victim.Position.Y))
                continue;
            if (victim.Template.Name == Owner.Template.Name)
                continue;
            eatingUntil = Game.Frame + 2 * LogicTime.FramesPerSecond;
            ai.MoveTo(Owner.Position, Commands.CommandSource.AI);
            victim.Kill("NORMAL", Owner.Id);
            Game.DestroyObject(victim); // swallowed: nothing is left
            Game.Events.Eva("D2kUnitLostToWorm", victim.Owner);
            return 3;
        }

        // Head for what draws it most, else wander.
        Vector3? best = null;
        float bestPull = 0;
        foreach (var obj in Game.ObjectsInRadius(Owner.Position, 16 * Cell))
        {
            if (obj.IsEffectivelyDead || obj == Owner || obj.FindModule<D2kWormAttractor>() is not { } pull)
                continue;
            float strength = pull.PullAt(Owner.Position);
            if (strength > bestPull && Game.Terrain.IsSand(obj.Position.X, obj.Position.Y))
            {
                bestPull = strength;
                best = obj.Position;
            }
        }
        if (best is { } target)
        {
            ai.MoveTo(target, Commands.CommandSource.AI);
            return 3;
        }
        if (!ai.IsMoving)
        {
            for (int tries = 0; tries < 8; tries++)
            {
                float angle = Game.Random.Float(0, MathF.Tau), distance = Game.Random.Float(1, WanderRadius) * Cell;
                var to = Owner.Position + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * distance;
                if (Game.Terrain.IsInsidePlayableArea(to.X, to.Y) && Game.Terrain.IsSand(to.X, to.Y))
                {
                    ai.MoveTo(to, Commands.CommandSource.AI);
                    break;
                }
            }
        }
        return 3;
    }
}

/// <summary>
/// What draws sandworms (AttractsWorms): Intensity, scaled by Falloff (percent for each cell of distance; by
/// default it carries undiminished to 16 cells). OnlyWhenDeployed: the thumper draws them only while thumping.
/// </summary>
public sealed class D2kWormAttractor : Module
{
    public float PullAt(Vector3 from)
    {
        if (BoolSetting("OnlyWhenDeployed") && Owner.FindModule<D2kThumper>() is not { IsDeployed: true })
            return 0;
        float intensity = FloatSetting("Intensity", 100);
        int cells = (int)(MathUtil.Distance2D(from, Owner.Position) / TdScale.Cell);
        var falloff = RuleValue.Words(Setting("Falloff").Replace(',', ' '));
        if (falloff.Length == 0)
            return intensity;
        float percent = cells < falloff.Length ? RuleValue.Float(falloff[cells]) : 0;
        return intensity * percent / 100f;
    }
}

/// <summary>The thumper: deployed (D), it stands still and thumps, drawing worms (GrantConditionOnDeploy).</summary>
public sealed class D2kThumper : Module
{
    public bool IsDeployed { get; private set; }

    public void ToggleDeploy()
    {
        IsDeployed = !IsDeployed;
        if (IsDeployed && Owner.AI is { } ai)
            ai.MoveTo(Owner.Position, Commands.CommandSource.Player);
    }
}

/// <summary>
/// A spice bloom point (spicebloom.spawnpoint and SpiceBloom): once no spice lies round it, a bloom grows there
/// after 3000-3250 ticks (2 minutes); it grows through four stages over its Lifetime (1000-3000 ticks), then
/// bursts: BloomExplosion hurts what is near, and 2-12 clumps of spice land within 5 cells, each spreading
/// spice round where it lands. Then it starts again.
/// </summary>
public sealed class D2kSpiceBloom : Module, IUpdateModule
{
    float Cell => TdScale.Cell;
    string SpiceName => Setting("Spice") is { Length: > 0 } s ? s : "D2K_Spice03";

    /// <summary>-1 while no bloom shows; else its stage (0-3).</summary>
    public int Stage { get; private set; } = -1;

    /// <summary>A spice pile (D2K_Spice01..12; not a bloom point).</summary>
    public static bool IsSpice(GameObject o) =>
        o.Template.Name.Length > 9 && o.Template.Name.StartsWith("D2K_Spice", StringComparison.Ordinal) && char.IsDigit(o.Template.Name[9]);
    int nextEvent = -1, growUntil, growStarted;

    public int Update()
    {
        if (Stage < 0)
        {
            bool spiceNear = Game.ObjectsInRadius(Owner.Position, Cell * 1.5f).Any(IsSpice);
            if (spiceNear)
            {
                nextEvent = -1;
                return LogicTime.FramesPerSecond;
            }
            if (nextEvent < 0)
                nextEvent = Game.Frame + Game.Random.Int(3000, 3250) * LogicTime.FramesPerSecond / 25;
            if (Game.Frame >= nextEvent)
            {
                Stage = 0;
                growStarted = Game.Frame;
                growUntil = Game.Frame + Game.Random.Int(1000, 3000) * LogicTime.FramesPerSecond / 25;
            }
            return LogicTime.FramesPerSecond;
        }
        if (Game.Frame < growUntil)
        {
            Stage = Math.Clamp(4 * (Game.Frame - growStarted) / Math.Max(1, growUntil - growStarted), 0, 3);
            return LogicTime.FramesPerSecond;
        }
        Burst();
        Stage = -1;
        nextEvent = -1;
        return LogicTime.FramesPerSecond;
    }

    void Burst()
    {
        if (Setting("Weapon") is { Length: > 0 } weapon)
            Game.Damage.FireTemporaryWeapon(weapon, null, Owner.Position);
        int pieces = Game.Random.Int(2, 12);
        for (int i = 0; i < pieces; i++)
        {
            float angle = Game.Random.Float(0, MathF.Tau), distance = Game.Random.Float(0.5f, 5) * Cell;
            var at = Owner.Position + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * distance;
            if (!Game.Terrain.IsInsidePlayableArea(at.X, at.Y) || !Game.Terrain.IsSand(at.X, at.Y))
                continue;
            if (Game.ObjectsInRadius(at, Cell * 0.8f).Any(IsSpice))
                continue;
            if (Game.Templates.Find(SpiceName) is { } spice)
                Game.CreateObject(spice, Game.Players.Neutral, at with { Z = Game.Terrain.GroundHeight(at) });
        }
    }
}

/// <summary>
/// The Starport's delivery (OpenRA BulkProductionQueue and ProductionBulkAirdrop): orders are paid for at once and
/// held; the first starts the countdown (DeliveryDelay, 1500 ticks: a minute), and when it runs out up to
/// MaxCapacity (6) of them arrive together at the pad. Orders beyond that wait for the next delivery.
/// </summary>
public sealed class D2kStarportExit : Production.DefaultProductionExitUpdate, IUpdateModule
{
    readonly List<Templates.ObjectTemplate> waiting = new();
    int arrivesAt = -1;

    float DelaySeconds => FloatSetting("DeliveryDelay", 60);
    int Capacity => IntSetting("MaxCapacity", 6);

    /// <summary>Orders waiting for the frigate.</summary>
    public int Waiting => waiting.Count;
    /// <summary>Seconds until the frigate lands (-1: none on the way).</summary>
    public float SecondsToDelivery => arrivesAt < 0 ? -1 : Math.Max(0, arrivesAt - Game.Frame) / (float)LogicTime.FramesPerSecond;

    public override void Exit(GameObject unit)
    {
        // The order is held: the unit only comes with the frigate.
        waiting.Add(unit.Template);
        Game.DestroyObject(unit);
        if (arrivesAt < 0)
        {
            arrivesAt = Game.Frame + (int)(DelaySeconds * LogicTime.FramesPerSecond);
            Game.Events.Eva("D2kOrderPlaced", Owner.Owner);
        }
    }

    public int Update()
    {
        if (arrivesAt < 0 || Game.Frame < arrivesAt)
            return LogicTime.FramesPerSecond / 2;
        if (Owner.IsEffectivelyDead || Owner.IsUnderConstruction)
            return LogicTime.FramesPerSecond;
        int count = Math.Min(Capacity, waiting.Count);
        for (int i = 0; i < count; i++)
        {
            var unit = Game.CreateObject(waiting[i], Owner.Owner, Owner.Position);
            if (unit != null)
                base.Exit(unit);
        }
        waiting.RemoveRange(0, count);
        Game.Events.Eva("D2kReinforcementsArrived", Owner.Owner);
        arrivesAt = waiting.Count > 0 ? Game.Frame + (int)(DelaySeconds * LogicTime.FramesPerSecond) : -1;
        return LogicTime.FramesPerSecond / 2;
    }
}

/// <summary>A vehicle a carryall may ferry (AutoCarryable: when it has more than MinDistance cells to go).</summary>
public sealed class D2kCarryable : Module
{
    public float MinDistance => FloatSetting("MinDistance", 6) * TdScale.Cell;

    /// <summary>The carryall coming for it, or carrying it (0: none).</summary>
    public uint CarriedBy { get; set; }

    public bool WantsLift =>
        CarriedBy == 0 && !Owner.IsEffectivelyDead && !Owner.IsContained && !Owner.IsDisabledBy("DISABLED_HELD")
        && Owner.AI is { IsMoving: true } ai && MathUtil.Distance2D(Owner.Position, ai.Destination) > MinDistance;
}

/// <summary>
/// A carryall (AutoCarryall): idle, it looks for its owner's vehicles with far to go, flies to the nearest,
/// lifts it (BeforeLoadDelay), carries it to where it was going and sets it down (BeforeUnloadDelay); the
/// vehicle then carries on with what it was doing.
/// </summary>
public sealed class D2kCarryall : Module, IUpdateModule
{
    enum Phase { Idle, Fetching, Loading, Carrying, Unloading }

    Phase phase;
    uint cargoId;
    Vector3 destination;
    int waitUntil, nextScan, nextSteer;
    float Reach => TdScale.Cell * 0.6f;

    public GameObject? Cargo => cargoId == 0 ? null : Game.FindObject(cargoId);
    public bool IsCarrying => phase is Phase.Carrying or Phase.Unloading;
    /// <summary>What it is doing (diagnostics): Idle, Fetching, Loading, Carrying, Unloading; circling when idle.</summary>
    public string State => phase + (circling ? " circling" : "");

    public int Update()
    {
        // (Not yet flying, e.g. just built: look again shortly rather than sleeping for good.)
        if (Owner.AI is not { } ai)
            return LogicTime.FramesPerSecond;
        if (Owner.IsEffectivelyDead)
        {
            Drop(Owner.Position); // the cargo falls with it
            return LogicTime.SleepForever;
        }
        var cargo = Cargo;
        switch (phase)
        {
            case Phase.Idle:
                if (Game.Frame < nextScan)
                {
                    Circle(ai);
                    return 3;
                }
                nextScan = Game.Frame + LogicTime.FramesPerSecond;
                var pick = Game.Objects
                    .Where(o => o.Owner == Owner.Owner && o.FindModule<D2kCarryable>() is { WantsLift: true })
                    .OrderBy(o => MathUtil.Distance2D(o.Position, Owner.Position)).FirstOrDefault();
                if (pick == null)
                {
                    Circle(ai);
                    return 3;
                }
                circling = false;
                cargoId = pick.Id;
                pick.FindModule<D2kCarryable>()!.CarriedBy = Owner.Id;
                // Reserved, it waits where it is for the carryall (Carryable's WaitForCarryable).
                destination = pick.AI!.Destination;
                pick.SetDisabled("DISABLED_HELD");
                phase = Phase.Fetching;
                ai.MoveTo(pick.Position, Commands.CommandSource.AI);
                return 1;

            case Phase.Fetching:
                if (cargo == null || cargo.IsEffectivelyDead || cargo.AI == null)
                {
                    Release();
                    return 3;
                }
                if (MathUtil.Distance2D(cargo.Position, Owner.Position) <= Reach)
                {
                    ai.MoveTo(Owner.Position, Commands.CommandSource.AI);
                    waitUntil = Game.Frame + (int)(FloatSetting("BeforeLoadDelay", 0.4f) * LogicTime.FramesPerSecond);
                    phase = Phase.Loading;
                    return 1;
                }
                if (Game.Frame >= nextSteer)
                {
                    nextSteer = Game.Frame + LogicTime.FramesPerSecond / 2;
                    ai.MoveTo(cargo.Position, Commands.CommandSource.AI);
                }
                return 1;

            case Phase.Loading:
                if (cargo == null || cargo.IsEffectivelyDead)
                {
                    Release();
                    return 3;
                }
                if (Game.Frame >= waitUntil)
                {
                    phase = Phase.Carrying;
                    ai.MoveTo(destination, Commands.CommandSource.AI);
                }
                return 1;

            case Phase.Carrying:
                if (cargo == null || cargo.IsEffectivelyDead)
                {
                    Release();
                    return 3;
                }
                Hold(cargo);
                if (MathUtil.Distance2D(Owner.Position, destination) <= Reach)
                {
                    waitUntil = Game.Frame + (int)(FloatSetting("BeforeUnloadDelay", 0.6f) * LogicTime.FramesPerSecond);
                    phase = Phase.Unloading;
                }
                else if (!ai.IsMoving)
                    ai.MoveTo(destination, Commands.CommandSource.AI);
                return 1;

            case Phase.Unloading:
                if (cargo != null)
                    Hold(cargo);
                if (Game.Frame >= waitUntil)
                    Drop(Owner.Position);
                return 1;
        }
        return 3;
    }

    // ---- Waiting: a carryall with nothing to carry circles where it is (as Dune 2000's do over their base) ----

    bool circling;
    Vector3 circleCenter;
    float circleAngle;

    void Circle(AI.AIUpdateInterface ai)
    {
        float radius = TdScale.Cell * 3;
        if (!circling)
        {
            circling = true;
            // The circle kept inside the map.
            float margin = radius + TdScale.Cell;
            circleCenter = new Vector3(Math.Clamp(Owner.Position.X, margin, Math.Max(margin, Game.Terrain.WorldWidth - margin)),
                Math.Clamp(Owner.Position.Y, margin, Math.Max(margin, Game.Terrain.WorldHeight - margin)), Owner.Position.Z);
            circleAngle = Game.Random.Float(0, MathF.Tau);
        }
        var waypoint = circleCenter + new Vector3(MathF.Cos(circleAngle), MathF.Sin(circleAngle), 0) * radius;
        // The next point round (an eighth of a turn) once nearing this one, or if it stopped.
        if (MathUtil.Distance2D(Owner.Position, waypoint) < TdScale.Cell * 1.5f || !ai.IsMoving)
        {
            circleAngle += MathF.Tau / 8;
            waypoint = circleCenter + new Vector3(MathF.Cos(circleAngle), MathF.Sin(circleAngle), 0) * radius;
            ai.MoveTo(waypoint, Commands.CommandSource.AI);
            // Refused (off the playable map): the circle drifts in towards the middle.
            if (!ai.IsMoving)
            {
                var middle = new Vector3(Game.Terrain.WorldWidth / 2, Game.Terrain.WorldHeight / 2, circleCenter.Z);
                circleCenter = Vector3.Lerp(circleCenter, middle, 0.25f);
            }
        }
    }

    // The cargo hangs under the carryall.
    void Hold(GameObject cargo) =>
        cargo.SetPosition(Owner.Position with { Z = Math.Max(Game.Terrain.GroundHeight(Owner.Position), Owner.Position.Z - 12) });

    void Drop(Vector3 at)
    {
        if (Cargo is { IsEffectivelyDead: false } cargo)
        {
            var ground = Game.Pathfinder.NearestPassable(at, cargo.AI?.Surfaces ?? Locomotion.Surfaces.Ground) ?? at;
            cargo.SetDisabled("DISABLED_HELD", false);
            if (cargo.AI is { } cargoAi)
            {
                cargoAi.Teleport(ground);
                // A harvester picks up its own errand; anything else goes on to where it was headed.
                if (cargoAi is not Supply.SupplyTruckAIUpdate && MathUtil.Distance2D(ground, destination) > Reach)
                    cargoAi.MoveTo(destination, Commands.CommandSource.AI);
            }
            else
                cargo.SetPosition(ground with { Z = Game.Terrain.GroundHeight(ground) });
        }
        Release();
    }

    void Release()
    {
        if (Cargo?.FindModule<D2kCarryable>() is { } carryable && carryable.CarriedBy == Owner.Id)
            carryable.CarriedBy = 0;
        if (Cargo is { } cargo && phase is Phase.Fetching or Phase.Loading or Phase.Carrying)
            cargo.SetDisabled("DISABLED_HELD", false);
        cargoId = 0;
        phase = Phase.Idle;
        nextScan = Game.Frame + LogicTime.FramesPerSecond;
    }
}

/// <summary>
/// Concrete laid on a Dune 2000 map (the map's Concrete terrain): the centres of its cells. A building's cell
/// on concrete is safe from <see cref="D2kRockDamage"/>.
/// </summary>
public static class D2kConcreteLayer
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameLogic, List<Vector3>> layers = new();

    public static IReadOnlyList<Vector3> Cells(GameLogic game) => layers.GetOrCreateValue(game);

    public static void Add(GameLogic game, Vector3 cell)
    {
        if (!IsConcrete(game, cell))
            layers.GetOrCreateValue(game).Add(cell);
    }

    public static bool IsConcrete(GameLogic game, Vector3 point)
    {
        float reach = TdScale.Cell * 0.5f;
        foreach (var c in layers.GetOrCreateValue(game))
            if (MathF.Abs(c.X - point.X) < reach && MathF.Abs(c.Y - point.Y) < reach)
                return true;
        return false;
    }

    /// <summary>A building's solid cells in the world (its template's D2kCells offsets from its position).</summary>
    public static IEnumerable<Vector3> CellsOf(Templates.ObjectTemplate template, Vector3 position)
    {
        foreach (string pair in RuleValue.Words(template.Rules.GetString("D2kCells")))
            if (pair.Split(',') is [var x, var y])
                yield return position + new Vector3(RuleValue.Float(x), RuleValue.Float(y), 0);
    }
}

/// <summary>A concrete slab (concretea, concreteb): placed, it lays concrete on its cells and is gone.</summary>
public sealed class D2kConcrete : Module, IUpdateModule
{
    public int Update()
    {
        if (Owner.IsUnderConstruction)
            return 1;
        foreach (var cell in D2kConcreteLayer.CellsOf(Owner.Template, Owner.Position))
            D2kConcreteLayer.Add(Game, cell with { Z = Game.Terrain.GroundHeight(cell) });
        Game.DestroyObject(Owner);
        return LogicTime.SleepForever;
    }
}

/// <summary>
/// A Dune 2000 building off concrete (D2kBuilding's DamageTerrainTypes: Rock): its health is held down to
/// DamageThreshold (50%) plus the rest in proportion to its cells on concrete; it starts there, and anything
/// mended above that wears off again, Damage every DamageInterval. Only on Dune 2000 maps.
/// </summary>
public sealed class D2kRockDamage : Module, IUpdateModule
{
    int safe = -1, total;
    int nextDamage;

    float Floor => Owner.MaxHealth * (IntSetting("DamageThreshold", 50) + (100 - IntSetting("DamageThreshold", 50)) * (float)safe / Math.Max(1, total)) / 100f;

    public int Update()
    {
        if (!Game.Terrain.HasSand || Owner.IsEffectivelyDead)
            return LogicTime.SleepForever;
        if (Owner.IsUnderConstruction)
            return LogicTime.FramesPerSecond / 2;
        if (safe < 0)
        {
            var cells = D2kConcreteLayer.CellsOf(Owner.Template, Owner.Position).ToList();
            // Computer players build on concrete laid for them (the d2k AI's global-auto-concrete).
            if (Owner.Owner.IsComputer)
            {
                foreach (var cell in cells)
                    D2kConcreteLayer.Add(Game, cell with { Z = Game.Terrain.GroundHeight(cell) });
                return LogicTime.SleepForever;
            }
            total = cells.Count;
            safe = cells.Count(c => D2kConcreteLayer.IsConcrete(Game, c));
            if (total == 0 || safe == total)
                return LogicTime.SleepForever;
            // StartOnThreshold: it goes up at the floor.
            float delta = Owner.Health - Floor;
            if (delta > 0)
                Hurt(delta);
            nextDamage = Game.Frame + Interval;
            return LogicTime.FramesPerSecond / 2;
        }
        if (Game.Frame >= nextDamage)
        {
            nextDamage = Game.Frame + Interval;
            if (Owner.Health > Floor)
                Hurt(FloatSetting("Damage", 5));
        }
        return LogicTime.FramesPerSecond / 2;
    }

    int Interval => Math.Max(1, (int)(FloatSetting("DamageInterval", 4) * LogicTime.FramesPerSecond));

    void Hurt(float amount) =>
        Owner.Damage(new DamageInfo { Amount = amount, DamageType = "UNRESISTABLE", DeathType = "NORMAL", SourceId = 0 });
}
