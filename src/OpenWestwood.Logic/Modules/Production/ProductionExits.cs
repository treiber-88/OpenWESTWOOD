using System.Numerics;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Modules.Production;

/// <summary>
/// The standard factory exit: units appear at UnitCreatePoint and drive to NaturalRallyPoint
/// (both relative to the building's facing), or to the player's rally point if one is set.
/// </summary>
public class DefaultProductionExitUpdate : Module, IProductionExit
{
    protected Vector3 CreatePoint;
    protected Vector3 NaturalRallyPoint;

    public Vector3? RallyPoint { get; set; }

    protected override void Initialize()
    {
        CreatePoint = RuleValue.Coord(Setting("UnitCreatePoint"));
        NaturalRallyPoint = RuleValue.Coord(Setting("NaturalRallyPoint"), CreatePoint + new Vector3(30, 0, 0));
    }

    public virtual bool CanExitNow => true;

    protected Vector3 ToWorld(Vector3 local) =>
        Owner.Position + Vector3.Transform(local with { Z = 0 }, DMath.RotationZ(Owner.Orientation));

    public virtual void Exit(GameObject unit)
    {
        var start = ToWorld(CreatePoint);
        // A door that opens onto ground the unit can't stand on (the building put at a cliff edge): it comes out at the
        // nearest ground it can, else it could never walk off (every unit after it piling up there too).
        if (unit.AI is { } walker && !Game.Pathfinder.IsValidMovementTerrain(start, walker.Surfaces)
            && Game.Pathfinder.NearestPassable(start, walker.Surfaces, 12) is { } open)
            start = open;
        unit.SetOrientation(Owner.Orientation);
        if (unit.AI != null)
            unit.AI.Teleport(start);
        else
            unit.SetPosition(start with { Z = Game.Terrain.LayerHeight(start.X, start.Y) });

        var destination = RallyPoint ?? ToWorld(NaturalRallyPoint);
        // A rally point on ground the unit can't stand on (a cliff, another building): the nearest it can, so the unit
        // still walks out of the door instead of staying in it with every unit made after it.
        if (unit.AI is { } ai && !Game.Pathfinder.IsValidMovementTerrain(destination, ai.Surfaces)
            && Game.Pathfinder.NearestPassable(destination, ai.Surfaces, 8) is { } free)
            destination = free;
        unit.AI?.MoveTo(destination, CommandSource.AI);
    }
}

/// <summary>Like the default exit, but units leave one at a time with ExitDelay between them (e.g. barracks).</summary>
public sealed class QueueProductionExitUpdate : DefaultProductionExitUpdate
{
    int exitDelay;
    int nextExitFrame;
    int burstRemaining;

    protected override void Initialize()
    {
        base.Initialize();
        exitDelay = FramesSetting("ExitDelay");
        burstRemaining = IntSetting("InitialBurst");
    }

    public override bool CanExitNow => Frame >= nextExitFrame;

    public override void Exit(GameObject unit)
    {
        base.Exit(unit);
        if (burstRemaining > 0)
            burstRemaining--;
        else
            nextExitFrame = Frame + exitDelay;
    }
}

/// <summary>Supply centers' exit for harvesters (can grant brief stealth to the new truck).</summary>
public sealed class SupplyCenterProductionExitUpdate : DefaultProductionExitUpdate
{
    /// <summary>A new harvester heads straight off to gather (the original forces it into wanting).</summary>
    public override void Exit(GameObject unit)
    {
        base.Exit(unit);
        if (Supply.SupplyGatherer.Of(unit) is { } gatherer)
            gatherer.ForceWanting = true;
        int stealthFrames = FramesSetting("GrantTemporaryStealth");
        if (stealthFrames > 0 && unit.FindModule<Stealth.StealthUpdate>() is { } stealth)
            stealth.ReceiveGrant(true, stealthFrames);
    }
}

/// <summary>
/// Units appear on the building's numbered SpawnPointBoneName bones (SpawnPoint01, 02...) and stay there for good
/// (the original's SpawnPointProductionExitUpdate: the Stinger Site's soldiers stand in its pits, DISABLED_HELD;
/// they still turn and shoot). Each bone holds one occupant; a dead one frees it.
/// </summary>
public sealed class SpawnPointProductionExitUpdate : DefaultProductionExitUpdate
{
    const int MaxSpawnPoints = 10;

    bool bonesInitialized;
    readonly List<(Vector3 Position, float Angle)> spawnPoints = new();
    readonly uint[] occupiers = new uint[MaxSpawnPoints];

    void InitializeBonePositions()
    {
        if (!Game.Bones.HasArt)
            return;
        // (Pristine bones, read as the building stands now; buildings don't move.)
        foreach (var bone in Game.Bones.NumberedBones(Owner, Setting("SpawnPointBoneName", "SpawnPoint"), MaxSpawnPoints))
        {
            var forward = Vector3.TransformNormal(Vector3.UnitX, bone);
            spawnPoints.Add((bone.Translation, DMath.Atan2(forward.Y, forward.X)));
        }
        bonesInitialized = true;
    }

    void RevalidateOccupiers()
    {
        for (int i = 0; i < spawnPoints.Count; i++)
            if (occupiers[i] != 0 && Game.FindObject(occupiers[i]) is not { IsEffectivelyDead: false })
                occupiers[i] = 0;
    }

    int FreeSpot()
    {
        if (!bonesInitialized)
            InitializeBonePositions();
        RevalidateOccupiers();
        for (int i = 0; i < spawnPoints.Count; i++)
            if (occupiers[i] == 0)
                return i;
        return -1;
    }

    public override bool CanExitNow => !bonesInitialized || FreeSpot() >= 0 || spawnPoints.Count == 0;

    public override void Exit(GameObject unit)
    {
        int spot = FreeSpot();
        if (spot < 0)
        {
            // No model bones (no art loaded): the plain exit.
            base.Exit(unit);
            return;
        }
        var (position, angle) = spawnPoints[spot];
        occupiers[spot] = unit.Id;
        var at = position with { Z = Game.Terrain.LayerHeight(position.X, position.Y) };
        if (unit.AI != null)
            unit.AI.Teleport(at);
        else
            unit.SetPosition(at);
        unit.SetOrientation(angle);
        // You are stuck here, little man.
        unit.SetDisabled("DISABLED_HELD");
    }
}
