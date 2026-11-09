using System.Numerics;
using OpenWestwood.Logic.Combat;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.Bodies;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Modules.Behaviors;

// OpenWESTWOOD additions for Tiberian Sun / Firestorm: the Hunter-Seeker droid and the Firestorm wall.

/// <summary>
/// Tiberian Sun's Hunter-Seeker droid: it rises to FlightHeight, picks an enemy (one of the costliest few of its targets,
/// at random), flies straight at it at Speed and blows up with Weapon once within DetonateProximity. A lost target is
/// replaced by another; with none left it blows up where it is.
/// </summary>
public sealed class TsHunterSeekerUpdate : Module, IUpdateModule
{
    uint targetId;
    bool launched;

    public uint TargetId => targetId;

    public void Launch()
    {
        launched = true;
        Owner.SetStatus(Status.Unselectable);
        PickTarget();
    }

    void PickTarget()
    {
        var me = Owner.Owner;
        var candidates = Game.Objects
            .Where(o => !o.IsEffectivelyDead && o.Owner != me && me.IsEnemyOf(o.Owner) && !o.Owner.IsNeutral && o.Body != null
                && (o.IsKindOf(Kind.Structure) || o.IsMobile) && !o.IsKindOf("WALK_ON_TOP_OF_WALL") && o.Template.BuildCost > 0)
            .OrderByDescending(o => o.Template.BuildCost).ThenBy(o => o.Id).Take(5).ToList();
        targetId = candidates.Count == 0 ? 0 : candidates[Game.Random.Int(0, candidates.Count - 1)].Id;
    }

    public int Update()
    {
        if (!launched || Owner.IsEffectivelyDead)
            return 0;
        if (Game.FindObject(targetId) is not { IsEffectivelyDead: false } target)
        {
            PickTarget();
            if (Game.FindObject(targetId) == null)
            {
                Detonate();
                return LogicTime.SleepForever;
            }
            return 0;
        }
        float speed = FloatSetting("Speed", 120) / LogicTime.FramesPerSecond;
        float height = FloatSetting("FlightHeight", 50);
        var here = Owner.Position;
        var to = new Vector2(target.Position.X - here.X, target.Position.Y - here.Y);
        float distance = to.Length();
        if (distance <= Math.Max(FloatSetting("DetonateProximity", 20), speed))
        {
            Owner.SetPosition(target.Position);
            Detonate();
            return LogicTime.SleepForever;
        }
        var step = to / distance * speed;
        var next = new Vector3(here.X + step.X, here.Y + step.Y, 0);
        // It climbs out of its building, and comes down onto the target at the end.
        float ground = Game.Terrain.GroundHeight(next);
        float wanted = ground + (distance < height * 2 ? height * distance / (height * 2) : height);
        next.Z = here.Z + Math.Clamp(wanted - here.Z, -speed, speed);
        Owner.SetPosition(next);
        Owner.SetOrientation(DMath.Atan2(to.Y, to.X));
        return 0;
    }

    void Detonate()
    {
        if (Setting("Weapon") is { Length: > 0 } weapon)
            Game.Damage.FireTemporaryWeapon(weapon, Owner, Owner.Position);
        if (!Owner.IsEffectivelyDead)
            Owner.Kill();
    }
}

/// <summary>
/// A Firestorm wall section: while its owner's firestorm burns (and the generator stands, powered), it shows ActiveFX,
/// destroys anything that crosses it (GroundFX, or AirFX for what flies) and passes the hits it takes on to the
/// generator, scaled by DamageToGenerator.
/// </summary>
public sealed class TsFirestormWall : Module, IUpdateModule, IDamageFilter
{
    int nextFx;

    GameObject? Generator => Game.FindObject(Owner.Owner.FirestormGeneratorId) is { IsEffectivelyDead: false } g && !g.IsDisabled ? g : null;

    public bool IsBurning => Game.Frame < Owner.Owner.FirestormUntilFrame && !Owner.IsUnderConstruction && Generator != null;

    public int Update()
    {
        if (!IsBurning)
            return 15;
        if (Game.Frame >= nextFx)
        {
            Game.Events.PlayFx(Setting("ActiveFX"), Owner.Position);
            nextFx = Game.Frame + LogicTime.FramesPerSecond;
        }
        float reach = Owner.Geometry.MajorRadius;
        foreach (var o in Game.ObjectsInRadius(Owner.Position, reach * 1.5f).ToList())
        {
            if (o == Owner || o.IsEffectivelyDead || o.IsKindOf(Kind.Structure) || o.Body == null && !o.IsKindOf("PROJECTILE"))
                continue;
            if (MathF.Abs(o.Position.X - Owner.Position.X) > reach || MathF.Abs(o.Position.Y - Owner.Position.Y) > reach)
                continue;
            bool flying = o.IsAirborneTarget || o.IsKindOf("PROJECTILE");
            Game.Events.PlayFx(Setting(flying ? "AirFX" : "GroundFX"), o.Position);
            if (o.IsKindOf("PROJECTILE") || o.Body == null)
                Game.DestroyObject(o);
            else
                o.Kill("BURNED");
        }
        return 0;
    }

    public bool Absorb(DamageInfo damage)
    {
        if (!IsBurning || damage.Kill)
            return false;
        // The generator takes a share of what hits the wall; the wall itself is untouched.
        if (Generator is { } generator && damage.Amount > 0 && !damage.DamageType.Equals("HEALING", StringComparison.OrdinalIgnoreCase))
            generator.Damage(new DamageInfo { Amount = damage.Amount * FloatSetting("DamageToGenerator", 0.1f), DamageType = damage.DamageType, SourceId = damage.SourceId });
        return true;
    }
}
