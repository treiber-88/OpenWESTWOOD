using System.Numerics;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Combat;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Modules.Die;

/// <summary>Removes the object when it dies (most units have this as their final step).</summary>
public sealed class DestroyDie : DieModule
{
    protected override void Die(DamageInfo killingBlow) => Game.DestroyObject(Owner);
}

/// <summary>Runs an object creation list on death (wreckage, debris, pilots, flying turrets...).</summary>
public sealed class CreateObjectDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string ocl = Setting("CreationList");
        var created = Game.ObjectCreation.Run(ocl, Owner, Owner.Position);
        if (BoolSetting("TransferPreviousHealth"))
            foreach (var obj in created)
                obj.Body?.SetHealth(Owner.MaxHealth > 0 ? obj.MaxHealth * Owner.Body!.MaxHealth / Owner.MaxHealth : obj.MaxHealth);
    }
}

/// <summary>Plays a visual/sound effect list on death.</summary>
public sealed class FXListDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        Game.Events.PlayFx(Setting("DeathFX"), Owner.Position, BoolSetting("OrientToObject", true) ? Owner : null);
    }
}

/// <summary>Leaves the object in the world after death (as rubble), instead of removing it.</summary>
public sealed class KeepObjectDie : DieModule, IDelaysDestruction
{
    public bool IsDelayingDestruction => true;

    protected override void Die(DamageInfo killingBlow)
    {
        Owner.SetCondition(Condition.Rubble);
        Owner.SetStatus(Core.Status.Unselectable);
        Game.Pathfinder.RemoveObstacle(Owner);
    }
}

/// <summary>
/// An instant death with effects: picks one of its FX, OCL and Weapon lines at random (each may list
/// several), then the object is removed. Used for simple deaths that need no animation.
/// </summary>
public sealed class InstantDeathBehavior : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string? Pick(string key)
        {
            var options = ListSetting(key).SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            return options.Count == 0 ? null : options[Game.Random.Int(0, options.Count - 1)];
        }

        if (Pick("FX") is { } fx)
            Game.Events.PlayFx(fx, Owner.Position, Owner);
        if (Pick("OCL") is { } ocl)
            Game.ObjectCreation.Run(ocl, Owner, Owner.Position);
        if (Pick("Weapon") is { } weapon)
            Game.Damage.FireTemporaryWeapon(weapon, Owner, Owner.Position);
        Game.DestroyObject(Owner);
    }
}

/// <summary>Fires a weapon when the object dies (e.g. exploding vehicles damage what's around them).</summary>
public sealed class FireWeaponWhenDeadBehavior : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        // Some only work once upgraded (StartsActive / TriggeredBy, e.g. demolition general's suicide).
        var triggers = Setting("TriggeredBy").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool startsActive = BoolSetting("StartsActive", triggers.Length == 0);
        if (!startsActive && !triggers.Any(Owner.HasUpgrade))
            return;
        Game.Damage.FireTemporaryWeapon(Setting("DeathWeapon"), Owner, Owner.Position);
    }
}

/// <summary>Grants or removes an upgrade when the object dies (e.g. "has a radar" upgrades).</summary>
public sealed class UpgradeDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string upgrade = Setting("UpgradeToRemove").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var producer = Game.FindObject(Owner.ProducerId);
        producer?.RemoveUpgrade(upgrade);
    }
}

/// <summary>Gives the killer a crate (money, veterancy, units) when the object dies.</summary>
public sealed class CreateCrateDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        var crates = ListSetting("CrateData").SelectMany(RuleValue.Words).ToList();
        if (crates.Count > 0)
            Game.Crates.TrySpawn(crates, Owner, killingBlow);
    }
}

/// <summary>
/// The pilot bails out when the vehicle dies: AirCreationList if it was flying, GroundCreationList
/// otherwise (VoiceEject and SoundEject play).
/// </summary>
public sealed class EjectPilotDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string ocl = Owner.IsSignificantlyAboveTerrain ? Setting("AirCreationList") : Setting("GroundCreationList");
        if (ocl.Length == 0)
            return;
        EjectPilot(ocl, Owner, Game.FindObject(killingBlow.SourceId));
    }

    /// <summary>The original's static ejectPilot (also used by crashing helicopters).</summary>
    public static void EjectPilot(string ocl, GameObject dyingObject, GameObject? damageDealer)
    {
        if (ocl.Length == 0)
            return;
        var game = dyingObject.Game;
        game.ObjectCreation.Run(ocl, dyingObject, dyingObject.Position, damageDealer);
        game.Events.PlaySound(dyingObject.Template.UnitSound("VoiceEject"), dyingObject.Position, dyingObject.Owner, dyingObject);
        game.Events.PlaySound(dyingObject.Template.UnitSound("SoundEject"), dyingObject.Position, source: dyingObject);
    }
}

/// <summary>
/// Cars flattened by tanks: depending on where the crusher is (nearest of the middle, front or back
/// point), the front, back or whole car is crushed (FRONTCRUSHED / BACKCRUSHED). The car survives a
/// half-crush; crushing the other half finishes it.
/// </summary>
public sealed class CrushDie : DieModule
{
    enum Crush { Total, BackEnd, FrontEnd, None }

    protected override void Die(DamageInfo killingBlow)
    {
        if (!killingBlow.DamageType.Equals("CRUSH", StringComparison.OrdinalIgnoreCase))
            return;
        var crusher = Game.FindObject(killingBlow.SourceId);
        var crush = crusher != null ? CrushLocation(crusher) : Crush.Total;
        if (crush == Crush.None)
            return;
        string key = crush switch { Crush.Total => "TotalCrush", Crush.BackEnd => "BackEndCrush", _ => "FrontEndCrush" };
        string sound = Setting(key + "Sound");
        if (sound.Length > 0 && Game.Random.Int(0, 99) < IntSetting(key + "SoundPercent"))
            Game.Events.PlaySound(sound, Owner.Position, source: Owner);
        bool front = crush is Crush.Total or Crush.FrontEnd;
        bool back = crush is Crush.Total or Crush.BackEnd;
        if (Owner.Body is { } body)
        {
            body.FrontCrushed = front;
            body.BackCrushed = back;
        }
        Owner.SetCondition("FRONTCRUSHED", front);
        Owner.SetCondition("BACKCRUSHED", back);
    }

    Crush CrushLocation(GameObject crusher)
    {
        bool frontCrushed = Owner.Body?.FrontCrushed ?? false, backCrushed = Owner.Body?.BackCrushed ?? false;
        var dir = new Vector2(DMath.Cos(Owner.Orientation), DMath.Sin(Owner.Orientation));
        var offset = dir * (Owner.Geometry.MajorRadius * 0.5f);
        var pos = new Vector2(crusher.Position.X, crusher.Position.Y);
        var center = new Vector2(Owner.Position.X, Owner.Position.Y);
        var result = Crush.None;
        float best = 99999;
        if (!frontCrushed && !backCrushed)
        {
            best = Vector2.DistanceSquared(center, pos);
            result = Crush.Total;
        }
        if (!frontCrushed)
        {
            float dist = Vector2.DistanceSquared(center + offset, pos);
            if (dist < best)
            {
                result = backCrushed ? Crush.Total : Crush.FrontEnd;
                best = dist;
            }
        }
        if (!backCrushed)
        {
            float dist = Vector2.DistanceSquared(center - offset, pos);
            if (dist < best)
                result = frontCrushed ? Crush.Total : Crush.BackEnd;
        }
        return result;
    }
}

/// <summary>
/// OpenWESTWOOD: the burnt-out husk a Westwood game's vehicle (Tiberian Dawn, Red Alert, Dune 2000, Tiberian Sun, Red
/// Alert 2) leaves where it died, as Zero Hour's tanks leave theirs: the Husk object (the vehicle's own art, drawn burnt,
/// on fire; it fades away after a while), facing the way the vehicle did. A tank's turret is often blown off
/// (TurretOffChance, 50% by default): it is thrown clear as its own burnt piece (TurretPiece). None for one dying in the
/// air, on water, or inside something (a terror drone in its victim).
/// </summary>
public sealed class WestwoodHuskDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        if (Owner.IsKindOf(Kind.Aircraft) || Owner.ContainedById != 0 || Owner.IsOffMap)
            return;
        var p = Owner.Position;
        if (Game.Terrain.IsUnderwater(p.X, p.Y, out _) || p.Z > Game.Terrain.GroundHeight(p) + 10)
            return;
        // (The neutral player's, so it never counts as anyone's unit.)
        var husk = Game.CreateObject(Setting("Husk"), Game.Players.Neutral, p, Owner.Orientation);
        if (husk == null || Setting("TurretPiece") is not { Length: > 0 } piece)
            return;
        float chance = Definition.Settings.Has("TurretOffChance") ? RuleValue.Float(Setting("TurretOffChance")) / 100f : 0.5f;
        if (!Game.Random.Chance(chance))
            return;
        float turretAngle = Owner.Orientation + (Owner.AI is { Turrets.Count: > 0 } ai ? ai.Turrets[0].Angle : 0);
        if (Game.CreateObject(piece, Game.Players.Neutral, p + new Vector3(0, 0, 8), turretAngle) is not { } turret)
            return;
        if (husk.FindModule<WestwoodHusk>() is { } body)
            body.TurretGone = true;
        // Thrown up and off to one side, tumbling.
        if (turret.FindModule<Updates.PhysicsBehavior>() is { } physics)
        {
            physics.AllowBouncing = true;
            // (Gravity is about 0.075 a frame each frame: a hop of 15 to 25 units, landing 20 to 40 away.)
            float away = Game.Random.Float(0, MathF.Tau), push = Game.Random.Float(0.4f, 0.8f);
            physics.ApplyForce(new Vector3(MathF.Cos(away) * push, MathF.Sin(away) * push, Game.Random.Float(1.5f, 1.9f)));
            physics.YawRate = Game.Random.Float(-0.15f, 0.15f);
        }
    }
}

/// <summary>
/// OpenWESTWOOD: a Westwood vehicle's husk (or its blown-off turret): it burns (FireParticleSystem, SmolderingFire by
/// default; "None" for none) until its time is up, and remembers whether its turret was blown off (then drawn without).
/// </summary>
public sealed class WestwoodHusk : Module, ICreateModule, IDieModule
{
    public bool TurretGone;
    uint fire;

    public void OnCreate()
    {
        string system = Definition.Settings.Has("FireParticleSystem") ? Setting("FireParticleSystem") : "SmolderingFire";
        if (system.Length > 0 && !system.Equals("None", StringComparison.OrdinalIgnoreCase))
            fire = Game.Events.AttachParticleSystem(system, new Vector3(0, 0, 4), Owner);
    }

    public void OnDie(DamageInfo killingBlow)
    {
        if (fire != 0)
            Game.Events.StopParticleSystem(fire);
        fire = 0;
    }
}
