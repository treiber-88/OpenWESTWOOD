using System.Numerics;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Core;

namespace OpenWestwood.Logic.Modules.Behaviors;

/// <summary>
/// OpenWESTWOOD addition for Tiberian Dawn factions: a Mobile Construction Vehicle unfolds into its
/// Construction Yard (Structure:) where it stands, if the yard fits there (TD's UnitClass::Try_To_Deploy).
/// The yard keeps the MCV's health fraction, and the MCV is gone.
/// </summary>
public sealed class TdDeployToStructure : Module
{
    /// <summary>The structure the MCV becomes.</summary>
    public string StructureName => Setting("Structure");

    /// <summary>Whether the yard would fit where the MCV stands.</summary>
    public bool CanDeploy()
    {
        if (Owner.IsEffectivelyDead || Game.Templates.Find(StructureName) is not { } yard)
            return false;
        return Game.Building.CheckLocation(yard, Owner.Position, 0, Owner.Owner, Owner) == Construction.BuildLocation.Ok;
    }

    /// <summary>Unfolds into the Construction Yard (false if it doesn't fit; TD says "Cannot deploy here").</summary>
    public bool Deploy()
    {
        if (!CanDeploy() || Game.Templates.Find(StructureName) is not { } yard)
        {
            Game.Events.Eva("TdCannotDeployHere", Owner.Owner);
            return false;
        }
        var player = Owner.Owner;
        var position = Owner.Position with { Z = Game.Terrain.GroundHeight(Owner.Position) };
        float health = Owner.MaxHealth > 0 ? Owner.Health / Owner.MaxHealth : 1;
        var team = Owner.Team;
        Game.Building.ClearSceneryFor(yard, position, 0, Owner);
        Game.DestroyObject(Owner);
        var structure = Game.CreateObject(yard, player, position, 0, team);
        Game.TreatAsBuilt(structure);
        if (structure.Body != null && health < 1)
            structure.Body.SetHealth(structure.MaxHealth * health);
        player.Score.AddObjectBuilt(structure);
        player.AI?.OnStructureProduced(null, structure);
        Game.Events.Eva("TdDeploy", player);
        return true;
    }
}

/// <summary>
/// OpenWESTWOOD addition for Tiberian Dawn factions: BuildingClass::Grand_Opening's free unit. A finished
/// refinery puts a harvester (Unit:) next to its south-west corner; if that spot is taken, somewhere
/// nearby. (A captured building is already built, so it gives nothing.)
/// </summary>
public sealed class TdFreeUnitOnBuilt : Module, IUpdateModule
{
    bool done;

    public int Update()
    {
        if (done)
            return LogicTime.SleepForever;
        if (Owner.IsUnderConstruction)
            return 1;
        done = true;
        if (Owner.Owner.IsNeutral || Game.Templates.Find(Setting("Unit")) is not { } unit)
            return LogicTime.SleepForever;
        var corner = Owner.Position + new Vector3(-Owner.Geometry.MajorRadius - 18, -Owner.Geometry.MinorRadius - 18, 0);
        var at = Spatial.PositionFinder.FindPositionAround(Game, corner, 0, 150) ?? corner;
        at.Z = Game.Terrain.GroundHeight(at);
        var obj = Game.CreateObject(unit, Owner.Owner, at, MathF.PI * 1.25f, Owner.Team);
        Game.TreatAsBuilt(obj);
        Owner.Owner.OnUnitCreated(Owner, obj);
        if (obj.AI is Supply.SupplyTruckAIUpdate truck)
            truck.Gatherer.ForceWanting = true; // off to harvest, as a harvester leaving a factory
        return LogicTime.SleepForever;
    }

}

/// <summary>Sizes of Tiberian Dawn objects in Zero Hour matches (must agree with OpenWestwood.Tools' TdFactionExporter).</summary>
public static class TdScale
{
    /// <summary>TD objects at their original size: a whole 36-unit TD cell (it also sets speeds, ranges and sight).
    /// (They were drawn 10% smaller for a while; the user asked for the original size back on 2026-11-09.)</summary>
    public const float Size = 1f;

    /// <summary>One TD cell of building footprint: the placement grid and the "next to the base" distance.</summary>
    public const float Cell = 36f * Size;

    /// <summary>World units per TD art pixel (24 pixels a cell).</summary>
    public const float UnitsPerPixel = Cell / 24f;
}

/// <summary>
/// OpenWESTWOOD addition for Tiberian Dawn and Dune 2000 factions: the repair button (BuildingClass::Repair /
/// the AI's Repair): while on, the building gets RepairStep health back each RepairSeconds, paying a share
/// of its cost each time (a full repair costs RepairPercent of the price); it stops when whole or broke.
/// </summary>
public sealed class TdStructureRepair : Module, IUpdateModule
{
    /// <summary>TD's RULES: 7 strength a step, a step every second, a full repair 25% of the cost.</summary>
    float Step => FloatSetting("RepairStep", 7);
    float Seconds => FloatSetting("RepairSeconds", 1);
    float Percent => FloatSetting("RepairPercent", 25);

    public bool IsRepairing { get; private set; }
    int nextStep;
    float owed;

    public void Toggle()
    {
        IsRepairing = !IsRepairing && Owner.Health < Owner.MaxHealth;
        nextStep = Game.Frame;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (!IsRepairing)
            return LogicTime.SleepForever;
        if (Owner.IsEffectivelyDead || Owner.Health >= Owner.MaxHealth || Owner.Body == null)
        {
            IsRepairing = false;
            return LogicTime.SleepForever;
        }
        if (Game.Frame < nextStep)
            return 1;
        nextStep = Game.Frame + Math.Max(1, (int)(Seconds * LogicTime.FramesPerSecond));
        float step = Math.Min(Step, Owner.MaxHealth - Owner.Health);
        owed += Owner.Template.BuildCost * Percent / 100f * step / Math.Max(1, Owner.MaxHealth);
        int cost = (int)owed;
        if (cost > 0 && !Owner.Owner.Money.TryWithdraw(cost))
        {
            IsRepairing = false;
            Game.Events.Eva("TdInsufficientFunds", Owner.Owner);
            return LogicTime.SleepForever;
        }
        owed -= cost;
        Owner.Body.SetHealth(Owner.Health + step);
        return 1;
    }
}

/// <summary>
/// OpenWESTWOOD addition for Tiberian Dawn and Dune 2000 infantry that act on a building by reaching it
/// (InfantryClass's MISSION_CAPTURE / MISSION_SABOTAGE and the commando's C4):
/// Mode Capture (engineers: the building becomes theirs, the engineer is used up), Demolish (the
/// building is destroyed; Consumed: Yes for a saboteur, No for TD's commando, who walks away).
/// </summary>
public sealed class TdInfiltrate : Module, IUpdateModule
{
    public bool Captures => Setting("Mode").Equals("Capture", StringComparison.OrdinalIgnoreCase);
    /// <summary>A Red Alert 2 spy: what it does depends on the building (its Ra2SpyEffect).</summary>
    public bool Spies => Setting("Mode").Equals("Spy", StringComparison.OrdinalIgnoreCase);
    bool Consumed => Definition.Settings.Has("Consumed") ? BoolSetting("Consumed") : Captures;

    uint target;
    Vector3 orderedTo;

    /// <summary>The building it is after (0: none).</summary>
    public uint Target => target;

    /// <summary>Whether this unit can do its job on that building.</summary>
    public bool CanTarget(GameObject building)
    {
        if (building.IsEffectivelyDead || !building.IsKindOf(Kind.Structure) || building.Owner == Owner.Owner || building.IsUnderConstruction)
            return false;
        if (Owner.Owner.RelationshipTo(building.Owner) == Players.Relationship.Allies)
            return false;
        // TD and Dune 2000 buildings say whether they can be captured; Zero Hour's can unless IMMUNE_TO_CAPTURE.
        bool capturable = building.IsKindOf("TD_OBJECT") ? building.IsKindOf("CAPTURABLE") : !building.IsKindOf("IMMUNE_TO_CAPTURE");
        if (Spies)
            return !building.Owner.IsNeutral && building.Template.Rules.GetString("Ra2SpyEffect").Length > 0;
        return Captures ? capturable : !building.Owner.IsNeutral || capturable;
    }

    public void Order(GameObject building)
    {
        if (!CanTarget(building) || Owner.AI is not { } ai)
            return;
        target = building.Id;
        ai.MoveTo(building.Position, Commands.CommandSource.Player);
        orderedTo = ai.Destination; // where the pathfinder can actually take it
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (target == 0)
            return LogicTime.SleepForever;
        if (Game.FindObject(target) is not { } building || !CanTarget(building) || Owner.IsEffectivelyDead || Owner.AI is not { } ai)
        {
            target = 0;
            return LogicTime.SleepForever;
        }
        // Another order since ours: the job is off.
        if (ai.Destination != orderedTo && ai.IsMoving)
        {
            target = 0;
            return LogicTime.SleepForever;
        }
        // Close enough when touching the building's box (it can't walk to the middle of it).
        var local = Vector3.Transform((Owner.Position - building.Position) with { Z = 0 }, DMath.RotationZ(-building.Orientation));
        float outX = Math.Max(0, MathF.Abs(local.X) - building.Geometry.MajorRadius);
        float outY = Math.Max(0, MathF.Abs(local.Y) - building.Geometry.MinorRadius);
        if (MathF.Sqrt(outX * outX + outY * outY) > Owner.Radius + 12)
        {
            if (!ai.IsMoving)
            {
                ai.MoveTo(building.Position, Commands.CommandSource.Player);
                orderedTo = ai.Destination;
            }
            return 5;
        }
        target = 0;
        var player = Owner.Owner;
        if (Spies)
            Spy(building, player);
        else if (Captures)
        {
            building.SetOwner(player);
            Game.Events.Eva("TdBuildingCaptured", player);
        }
        else
        {
            building.Kill("EXPLODED", Owner.Id);
            Game.Events.Eva("TdBuildingDemolished", player);
        }
        if (Consumed || Spies)
            Game.DestroyObject(Owner);
        else
            ai.MoveTo(Owner.Position + (Owner.Position - building.Position) with { Z = 0 } * 0.5f, Commands.CommandSource.AI);
        return LogicTime.SleepForever;
    }

    /// <summary>
    /// Red Alert 2's infiltration (BuildingClass::Infiltrated_By): a Battle Lab gives its side's stolen tech, a power
    /// plant blacks out its owner's power, a refinery gives up half its owner's money, a superweapon building
    /// starts its charge over, a radar loses its owner's map.
    /// </summary>
    void Spy(GameObject building, Players.Player player)
    {
        var victim = building.Owner;
        var effect = OpenWestwood.Core.Rules.RuleValue.Words(building.Template.Rules.GetString("Ra2SpyEffect")).ToList();
        switch (effect.FirstOrDefault())
        {
            case "StolenTech" or "Veteran" when effect.Count > 1:
                player.CompleteUpgrade(effect[1]);
                break;
            case "Power":
                victim.Energy.SabotagedUntilFrame = Math.Max(victim.Energy.SabotagedUntilFrame, Game.Frame + 60 * LogicTime.FramesPerSecond);
                break;
            case "Money":
                int stolen = victim.Money.WithdrawUpTo(victim.Money.Amount / 2);
                player.Money.Deposit(stolen);
                break;
            case "Superweapon":
                foreach (var power in building.FindModules<SpecialPowers.SpecialPowerModule>())
                    power.StartPowerRecharge();
                break;
            case "Radar":
                Game.Shroud.ShroudMapForPlayer(victim);
                break;
        }
        Game.Events.Eva("Ra2BuildingInfiltrated", player);
    }
}

/// <summary>
/// OpenWESTWOOD addition for Tiberian Dawn maps: Tiberium grows, spreads and poisons infantry (MapClass::Logic,
/// InfantryClass::Per_Cell_Process). TD scans the whole map about every 9 seconds (30 cells a frame, 15 frames
/// a second) and then grows two random cells a stage (up to 11 of 12) and seeds a neighbouring clear cell from
/// two rich ones (over stage 6), or from a blossom tree (three chances). Here each patch (Mode Tiberium) or
/// blossom tree (Mode Spawner) takes its share of those picks every 9 seconds. A patch's stage is its
/// supply boxes (one a stage), so harvesting thins it. Infantry walking through take 2 fire damage a cell
/// (every 1.5 seconds), except the chem warrior.
/// </summary>
public sealed class TdTiberium : Module, IUpdateModule
{
    const int ScanFrames = 9 * LogicTime.FramesPerSecond, PoisonFrames = 45, MaxStage = 12;

    bool Spawner => Setting("Mode").Equals("Spawner", StringComparison.OrdinalIgnoreCase);
    string SeedName => Setting("Seed") is { Length: > 0 } seed ? seed : "TD_Tiberium01";

    int nextScan = -1, nextPoison;

    sealed class Census
    {
        public int Frame = -1, Growable, Rich;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameLogic, Census> censuses = new();

    /// <summary>How many patches could grow, and how many are rich enough to spread (counted once a frame).</summary>
    Census Count()
    {
        var census = censuses.GetOrCreateValue(Game);
        if (census.Frame == Game.Frame)
            return census;
        census.Frame = Game.Frame;
        census.Growable = census.Rich = 0;
        foreach (var obj in Game.Objects)
        {
            if (obj.FindModule<TdTiberium>() is not { Spawner: false } || obj.FindModule<Supply.SupplyWarehouseDockUpdate>() is not { } pile)
                continue;
            if (pile.Boxes < MaxStage - 1)
                census.Growable++;
            if (pile.Boxes > 7)
                census.Rich++;
        }
        return census;
    }

    public int Update()
    {
        if (nextScan < 0)
            nextScan = Game.Frame + Game.Random.Int(1, ScanFrames);
        // (Red Alert's ore and gems grow and spread but hurt no one: Poison: No.)
        if (!Spawner && Game.Frame >= nextPoison && !Setting("Poison").Equals("No", StringComparison.OrdinalIgnoreCase))
        {
            nextPoison = Game.Frame + PoisonFrames;
            Poison();
        }
        if (Game.Frame >= nextScan)
        {
            nextScan = Game.Frame + ScanFrames;
            var census = Count();
            var pile = Owner.FindModule<Supply.SupplyWarehouseDockUpdate>();
            if (Spawner)
            {
                // A blossom tree enters the spread list three times; two picks are made from it.
                if (Game.Random.Float(0, 1) < 6f / Math.Max(1, census.Rich + 3))
                    Spread();
            }
            else if (pile != null)
            {
                if (pile.Boxes < MaxStage - 1 && Game.Random.Float(0, 1) < 2f / Math.Max(1, census.Growable))
                    pile.SetBoxes(pile.Boxes + 1);
                if (pile.Boxes > 7 && Game.Random.Float(0, 1) < 2f / Math.Max(1, census.Rich))
                    Spread();
            }
        }
        return Spawner ? ScanFrames : PoisonFrames;
    }

    /// <summary>A stage-one patch on a neighbouring clear cell (TD tries the eight directions from a random one).</summary>
    void Spread()
    {
        float cell = TdScale.Cell;
        int start = Game.Random.Int(0, 7);
        for (int i = 0; i < 8; i++)
        {
            float angle = (start + i) * MathF.Tau / 8;
            var at = Owner.Position + new Vector3(DMath.Cos(angle), DMath.Sin(angle), 0) * (i % 2 == 0 ? cell : cell * 1.41f);
            var (cx, cy) = Game.Terrain.CellAt(at.X, at.Y);
            if (!Game.Terrain.IsInsidePlayableArea(at.X, at.Y) || Game.Terrain.IsCliffCell(cx, cy) || Game.Terrain.IsWaterCell(cx, cy))
                continue;
            if (Game.ObjectsInRadius(at, cell * 0.6f).Any(o => !o.IsEffectivelyDead && (o.IsKindOf(Kind.Structure) || o.IsKindOf(Kind.Immobile))))
                continue;
            if (Game.Templates.Find(SeedName) is { } seed)
                Game.CreateObject(seed, Game.Players.Neutral, at with { Z = Game.Terrain.GroundHeight(at) });
            return;
        }
    }

    /// <summary>Infantry walking through the patch are hurt (TD hurts them on reaching each cell's middle).</summary>
    void Poison()
    {
        foreach (var obj in Game.ObjectsInRadius(Owner.Position, TdScale.Cell * 0.5f))
        {
            if (obj.IsEffectivelyDead || !obj.IsKindOf(Kind.Infantry) || obj.AI is not { IsMoving: true } || obj.IsContained)
                continue;
            if (obj.Template.ModulesOfType("Draw").Any(m => m.Settings.GetString("TdType") == "INFANTRY_E5"))
                continue; // the chem warrior is immune
            obj.Damage(new Combat.DamageInfo { Amount = 2, DamageType = "FLAME", DeathType = "BURNED", SourceId = Owner.Id });
        }
    }
}

/// <summary>A TD Helipad: one aircraft at a time lands there to rearm.</summary>
public sealed class TdHelipad : Module
{
    /// <summary>The aircraft that has claimed it (0: free).</summary>
    public uint Claimant { get; set; }

    public bool IsFreeFor(GameObject aircraft) =>
        Claimant == 0 || Claimant == aircraft.Id || Game.FindObject(Claimant) is not { IsEffectivelyDead: false } other
        || other.FindModule<TdAircraftAmmo>() is not { } ammo || ammo.Pad != Owner;
}

/// <summary>
/// A TD aircraft's ammunition (TechnoTypeClass MaxAmmo: the Orca 6, the Apache 15): every shot uses one; out of
/// ammo it can't fire and flies back to a free Helipad of its owner's, where it gets one back each
/// Rearm interval (BuildingClass Mission_Repair: three seconds less one at full power). An aircraft idle with
/// ammo spent heads back too (TD's MISSION_RETREAT after an attack).
/// </summary>
public sealed class TdAircraftAmmo : Module, IUpdateModule
{
    int MaxAmmo => IntSetting("MaxAmmo", 6);
    float ReachDistance => TdScale.Cell * 0.6f;

    public int Ammo { get; private set; } = -1;
    public GameObject? Pad { get; private set; }
    int shotsSeen, nextRearm, idleSince = -1;

    public int Update()
    {
        if (Owner.IsEffectivelyDead || Owner.AI is not { } ai)
            return LogicTime.SleepForever;
        int shots = Owner.Weapons.All.Sum(w => w.ShotsFired);
        if (Ammo < 0)
        {
            Ammo = MaxAmmo;
            shotsSeen = shots;
        }
        if (shots > shotsSeen)
        {
            Ammo = Math.Max(0, Ammo - (shots - shotsSeen));
            shotsSeen = shots;
            if (Ammo == 0)
                foreach (var weapon in Owner.Weapons.All)
                    weapon.Empty();
        }

        if (Ammo >= MaxAmmo)
        {
            Release();
            idleSince = -1;
            return 3;
        }

        // Spent, or idle with some spent: back to a pad.
        bool idle = ai.IsIdle && !ai.IsMoving;
        if (idle && idleSince < 0)
            idleSince = Game.Frame;
        if (!idle && Pad == null)
            idleSince = -1;
        bool wantsPad = Ammo == 0 || idleSince >= 0 && Game.Frame - idleSince > 3 * LogicTime.FramesPerSecond;
        if (!wantsPad)
            return 3;
        if (Pad is not { IsEffectivelyDead: false } pad || pad.Owner != Owner.Owner)
        {
            Release();
            pad = FindPad();
            if (pad == null)
                return LogicTime.FramesPerSecond;
            Pad = pad;
            pad.FindModule<TdHelipad>()!.Claimant = Owner.Id;
            ai.MoveTo(pad.Position, Commands.CommandSource.AI);
            nextRearm = -1;
            return 3;
        }

        // Over the pad: one round each interval.
        if (MathUtil.Distance2D(Owner.Position, pad.Position) > ReachDistance)
        {
            if (!ai.IsMoving)
                ai.MoveTo(pad.Position, Commands.CommandSource.AI);
            return 3;
        }
        if (nextRearm < 0)
            nextRearm = Game.Frame + RearmFrames();
        if (Game.Frame >= nextRearm)
        {
            Ammo++;
            nextRearm = Game.Frame + RearmFrames();
            foreach (var weapon in Owner.Weapons.All)
                weapon.Reload();
            if (Ammo >= MaxAmmo)
                Release();
        }
        return 1;
    }

    // BuildingClass::Mission_Repair: 3 seconds less up to one more for the house's power.
    int RearmFrames() =>
        (int)((3f - Math.Clamp(Owner.Owner.Energy.SupplyRatio, 0, 1)) * LogicTime.FramesPerSecond);

    GameObject? FindPad() =>
        Game.Objects.Where(o => o.Owner == Owner.Owner && !o.IsEffectivelyDead && !o.IsUnderConstruction
                && o.FindModule<TdHelipad>() is { } h && h.IsFreeFor(Owner))
            .OrderBy(o => MathUtil.Distance2D(o.Position, Owner.Position)).FirstOrDefault();

    void Release()
    {
        if (Pad?.FindModule<TdHelipad>() is { } h && h.Claimant == Owner.Id)
            h.Claimant = 0;
        Pad = null;
    }
}
