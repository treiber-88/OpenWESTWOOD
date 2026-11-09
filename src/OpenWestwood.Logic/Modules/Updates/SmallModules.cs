using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Combat;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Modules.Updates;

/// <summary>
/// Grants UpgradeToGrant (a player or object upgrade) when the object is finished - or at creation
/// already, if ExemptStatus includes UNDER_CONSTRUCTION and it was not built (the USA command center's radar).
/// </summary>
public sealed class GrantUpgradeCreate : Module, ICreateModule
{
    bool done;

    public void OnCreate()
    {
        var exempt = Flags.ObjectStatus.Parse(Setting("ExemptStatus"));
        if (exempt.Has(Status.UnderConstruction) && !Owner.IsUnderConstruction)
            Grant();
    }

    public void OnBuildComplete()
    {
        if (done)
            return;
        done = true;
        Grant();
    }

    void Grant()
    {
        string name = Setting("UpgradeToGrant");
        var upgrade = Game.Catalog.Upgrade(name);
        if (upgrade == null)
        {
            Log.Warning($"GrantUpgradeCreate on {Owner.Template.Name}: no upgrade '{name}'.");
            return;
        }
        if (upgrade.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase))
            Owner.GiveUpgrade(name);
        else
            Owner.Owner.CompleteUpgrade(name);
    }
}

/// <summary>Shows the PREORDER model state (the pre-order bonus flag on buildings) for players who pre-ordered: nobody here.</summary>
public sealed class PreorderCreate : Module, ICreateModule
{
    public void OnCreate() { }

    public void OnBuildComplete() => Owner.SetCondition("PREORDER", false);
}

/// <summary>Units built with ScienceRequired start at StartingLevel rank (e.g. veteran Rangers with that general's power).</summary>
public sealed class VeterancyGainCreate : Module, ICreateModule
{
    public void OnCreate()
    {
        string science = Setting("ScienceRequired");
        if (science.Length > 0 && !Owner.Owner.HasScience(science))
            return;
        if (!Owner.IsTrainable)
            return;
        if (Enum.TryParse<VeterancyLevel>(Setting("StartingLevel", "REGULAR"), true, out var level))
            Owner.SetMinVeterancyLevel(level);
    }
}

/// <summary>Locks the weapon in SlotToLock once the object is finished.</summary>
public sealed class LockWeaponCreate : Module, ICreateModule
{
    public void OnCreate() { }

    public void OnBuildComplete()
    {
        var slot = Setting("SlotToLock").ToUpperInvariant() switch
        {
            "SECONDARY" or "SECONDARY_WEAPON" => WeaponSlot.Secondary,
            "TERTIARY" or "TERTIARY_WEAPON" => WeaponSlot.Tertiary,
            _ => WeaponSlot.Primary,
        };
        Owner.Weapons.LockSlot(slot);
    }
}

/// <summary>
/// Keeps firing Weapon at its own position whenever it is ready (burning debris, toxin puddles,
/// radiation): after InitialDelay, and not within ExclusiveWeaponDelay of the object's own shots.
/// </summary>
public sealed class FireWeaponUpdate : Module, IUpdateModule, ICreateModule
{
    Weapon? weapon;
    int initialDelayFrame;
    /// <summary>OpenWESTWOOD (Kane's Wrath's FireWeaponNugget OneShot): fires once, after InitialDelay.</summary>
    bool fired;
    /// <summary>OpenWESTWOOD (Kane's Wrath's FireWeaponNugget FireDelay): fires every RepeatDelay, whatever the weapon's reload.</summary>
    int nextRepeatFrame;

    public void OnCreate()
    {
        if (Game.Catalog.Weapon(Setting("Weapon")) is { } template)
        {
            weapon = new Weapon(Owner, template, WeaponSlot.Primary);
            weapon.Reload();
        }
        initialDelayFrame = Frame + FramesSetting("InitialDelay");
    }

    public int Update()
    {
        if (Frame < initialDelayFrame || weapon == null || Owner.IsUnderConstruction || fired)
            return 0;
        if (FramesSetting("RepeatDelay") is > 0 and var repeat)
        {
            if (Frame < nextRepeatFrame)
                return 0;
            nextRepeatFrame = Frame + repeat;
            Game.Damage.FireTemporaryWeapon(weapon.Template.Name, Owner, Owner.Position);
            return 0;
        }
        if (!weapon.IsReady)
            return 0;
        int exclusive = FramesSetting("ExclusiveWeaponDelay");
        if (exclusive > 0 && Frame < Owner.LastShotFiredFrame + exclusive)
            return 0;
        weapon.ForceFireAtPosition(Owner.Position);
        fired = BoolSetting("OneShot");
        return 0;
    }
}

/// <summary>
/// Kane's Wrath's StructureUnpackUpdate: a building just put down unfolds (model condition UNPACKING, its build-up model and
/// animation) for UnpackTime before it stands finished.
/// </summary>
public sealed class StructureUnpackUpdate : Module, ICreateModule, IUpdateModule
{
    int doneFrame = -1;

    public void OnCreate()
    {
        doneFrame = Frame + Math.Max(1, FramesSetting("UnpackTime"));
        Owner.SetCondition("UNPACKING");
    }

    public int Update()
    {
        if (doneFrame < 0 || Frame < doneFrame)
            return 0;
        Owner.SetCondition("UNPACKING", false);
        doneFrame = -1;
        return LogicTime.SleepForever;
    }
}

/// <summary>OpenWESTWOOD (Kane's Wrath's FXListBehavior onCreate event): plays FX where the object is made.</summary>
public sealed class FXOnCreateBehavior : Module, ICreateModule
{
    public void OnCreate() => Game.Events.PlayFx(Setting("FX"), Owner.Position);
}

/// <summary>
/// Gives nearby friendly units (and passengers inside them) of RequiredAffectKindOf a temporary
/// weapon bonus (BonusConditionType) for BonusDuration, every BonusDelay, within BonusRange.
/// </summary>
public sealed class WeaponBonusUpdate : Module, IUpdateModule
{
    public int Update()
    {
        var required = Flags.KindOf.Parse(Setting("RequiredAffectKindOf"));
        var forbidden = Flags.KindOf.Parse(Setting("ForbiddenAffectKindOf"));
        string condition = Setting("BonusConditionType");
        int duration = FramesSetting("BonusDuration");
        float range = FloatSetting("BonusRange");
        bool Fits(GameObject o) => o.KindOf.HasAll(required) && (forbidden.IsEmpty || !o.KindOf.HasAny(forbidden));
        foreach (var obj in Game.Partition.Query(Owner.Position, range))
        {
            if (obj.IsEffectivelyDead || !(obj.Owner == Owner.Owner || Owner.Owner.IsAllyOf(obj.Owner)))
                continue;
            if (MathUtil.Distance2D(obj.Position, Owner.Position) > range)
                continue;
            if (Fits(obj))
                obj.DoTempWeaponBonus(condition, duration);
            if (obj.Contain != null)
                foreach (var passenger in obj.Contain.Passengers.Where(Fits).ToList())
                    passenger.DoTempWeaponBonus(condition, duration);
        }
        return Math.Max(0, FramesSetting("BonusDelay") - 1);
    }
}

/// <summary>Shows the ENEMYNEAR model state while an enemy is within vision range (checked every ScanDelayTime).</summary>
public sealed class EnemyNearUpdate : Module, IUpdateModule, ICreateModule
{
    bool enemyNear;
    int scanDelay;

    public void OnCreate() => scanDelay = Game.Random.Int(0, FramesSetting("ScanDelayTime"));

    public int Update()
    {
        bool wasNear = enemyNear;
        if (scanDelay == 0)
        {
            scanDelay = FramesSetting("ScanDelayTime");
            float range = Owner.VisionRange;
            enemyNear = Game.Partition.Query(Owner.Position, range).Any(o =>
                !o.IsEffectivelyDead && Owner.Owner.IsEnemyOf(o.Owner) && MathUtil.Distance2D(o.Position, Owner.Position) <= range
                && !(o.HasStatus(Status.Stealthed) && !o.HasStatus(Status.Detected)));
        }
        else
            scanDelay--;
        if (enemyNear != wasNear)
            Owner.SetCondition("ENEMYNEAR", enemyNear);
        return 0;
    }
}
