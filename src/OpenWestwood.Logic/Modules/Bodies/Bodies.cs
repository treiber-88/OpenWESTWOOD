using OpenWestwood.Logic.Combat;
using OpenWestwood.Logic.Core;

namespace OpenWestwood.Logic.Modules.Bodies;

/// <summary>The standard body: health, armor, damage states.</summary>
public class ActiveBody : Body, IUpdateModule
{
    public virtual int Update()
    {
        HealSubdual();
        return SubdualDamage > 0 ? 0 : 30;
    }
}

/// <summary>A building's body. Remembers which dozer/worker built it (for repairs and rebuilding).</summary>
public class StructureBody : ActiveBody
{
    public uint ConstructorId { get; set; }
}

/// <summary>Takes damage but never drops below 1 health (scripted heroes, some props).</summary>
public sealed class ImmortalBody : ActiveBody
{
    protected override float AdjustIncomingDamage(float amount, DamageInfo damage) =>
        damage.Kill ? amount : Math.Min(amount, Health - 1);
}

/// <summary>Like immortal, except that "unresistable" damage (scripts, sell, self-destruct) can still kill.</summary>
public sealed class HighlanderBody : ActiveBody
{
    protected override float AdjustIncomingDamage(float amount, DamageInfo damage) =>
        damage.Kill || damage.DamageType.Equals("UNRESISTABLE", StringComparison.OrdinalIgnoreCase)
            ? amount
            : Math.Min(amount, Health - 1);
}

/// <summary>For objects without real health (props, effects): any lethal or killing hit simply kills them.</summary>
public sealed class InactiveBody : Body
{
    protected override void Initialize()
    {
        base.Initialize();
        MaxHealth = 1;
        Health = 1;
    }

    public override void AttemptDamage(DamageInfo damage)
    {
        if (damage.Kill || damage.DamageType.Equals("UNRESISTABLE", StringComparison.OrdinalIgnoreCase))
            base.AttemptDamage(damage);
    }
}

/// <summary>
/// Zero Hour's "second life" body (e.g. the Battle Bus): the first death is not fatal. Instead the
/// object heals to full and switches to its SECOND_LIFE armor/weapon sets and model.
/// </summary>
public sealed class UndeadBody : ActiveBody
{
    bool usedSecondLife;
    float secondLifeMaxHealth;

    protected override void Initialize()
    {
        base.Initialize();
        secondLifeMaxHealth = FloatSetting("SecondLifeMaxHealth", MaxHealth);
    }

    protected override float AdjustIncomingDamage(float amount, DamageInfo damage)
    {
        if (usedSecondLife || amount < Health || damage.DamageType.Equals("UNRESISTABLE", StringComparison.OrdinalIgnoreCase))
            return amount;

        // Survive with 1 health, then start the second life.
        usedSecondLife = true;
        StartSecondLife(damage);
        return 0;
    }

    /// <summary>The original's startSecondLife: full health on the new maximum, the second-life armor, and a slow death begun without dying (the battle bus's flip).</summary>
    void StartSecondLife(DamageInfo damage)
    {
        SetMaxHealth(secondLifeMaxHealth, MaxHealthChange.FullyHeal);
        Owner.SetArmorSetFlag("SECOND_LIFE");
        var deaths = Owner.FindModules<SlowDeath.SlowDeathBehavior>().Where(s => s.IsApplicable(damage)).ToList();
        int total = deaths.Sum(s => s.ProbabilityFor(damage));
        int roll = Game.Random.Int(1, Math.Max(1, total));
        foreach (var death in deaths)
        {
            roll -= death.ProbabilityFor(damage);
            if (roll <= 0)
            {
                death.BeginSlowDeath(damage);
                return;
            }
        }
    }
}

/// <summary>A structure that shares incoming damage with its "slaves" (GLA's hive structures: e.g. Stinger Sites).</summary>
public sealed class HiveStructureBody : StructureBody
{
    string[] propagateTypes = [];
    string[] swallowTypes = [];

    protected override void Initialize()
    {
        base.Initialize();
        propagateTypes = Setting("PropagateDamageTypesToSlavesWhenExisting").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        swallowTypes = Setting("SwallowDamageTypesIfSlavesNotExisting").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public override void AttemptDamage(DamageInfo damage)
    {
        var slaves = Game.Objects.Where(o => o.ProducerId == Owner.Id && o.FindModule<Updates.SlavedUpdate>() != null && !o.IsEffectivelyDead).ToList();
        bool typeMatches(string[] list) => list.Any(t => t.Equals(damage.DamageType, StringComparison.OrdinalIgnoreCase) || t.Equals("ALL", StringComparison.OrdinalIgnoreCase));

        if (slaves.Count > 0 && typeMatches(propagateTypes))
        {
            var target = slaves[Game.Random.Int(0, slaves.Count - 1)];
            target.Damage(damage);
            return;
        }
        if (slaves.Count == 0 && typeMatches(swallowTypes))
            return;
        base.AttemptDamage(damage);
    }
}

/// <summary>
/// Kane's Wrath's ShieldBody (the Scrin Forcefield Generator): once the object's SHIELDBODY_ENABLED status is set (by a
/// StatusBitsUpgrade), a shield of ShieldAmount soaks up damage first, taking it through ShieldArmor. While the shield holds,
/// the object shows ShieldActiveModelCondition and uses its SHIELDBODY_ENABLED armor set. A spent shield comes back in full
/// after ShieldRechargeIdleTime without being hit.
/// </summary>
public sealed class ShieldBody : ActiveBody
{
    float shieldMax;
    int rechargeIdleFrames;
    ArmorTemplate shieldArmor = ArmorTemplate.None;
    bool enabled;

    /// <summary>What is left of the shield (0 when it is down or not yet enabled).</summary>
    public float Shield { get; private set; }
    public float ShieldMax => enabled ? shieldMax : 0;

    protected override void Initialize()
    {
        base.Initialize();
        shieldMax = FloatSetting("ShieldAmount");
        rechargeIdleFrames = FramesSetting("ShieldRechargeIdleTime");
    }

    public override void OnCreate()
    {
        base.OnCreate();
        shieldArmor = Game.Catalog.Armor(Setting("ShieldArmor"));
    }

    public override int Update()
    {
        base.Update();
        if (!enabled && Owner.HasStatus("SHIELDBODY_ENABLED") && shieldMax > 0)
        {
            enabled = true;
            Owner.SetArmorSetFlag("SHIELDBODY_ENABLED");
            SetShield(shieldMax, Setting("ShieldEnabledFX"));
        }
        else if (enabled && Shield < shieldMax && Frame - LastDamageFrame >= rechargeIdleFrames)
            SetShield(shieldMax, Setting("ShieldRechargeEndFX"));
        return enabled ? 1 : 15;
    }

    protected override float AdjustIncomingDamage(float amount, DamageInfo damage)
    {
        if (!enabled || Shield <= 0 || damage.Kill || damage.DamageType.Equals("UNRESISTABLE", StringComparison.OrdinalIgnoreCase))
            return amount;
        // The shield takes the hit through its own armor; whatever it can't hold goes through to the body.
        float toShield = shieldArmor.AdjustDamage(damage.DamageType, damage.Amount);
        if (toShield <= 0)
            return 0;
        if (toShield < Shield)
        {
            SetShield(Shield - toShield, Setting("ShieldTakeDamageFX"));
            return 0;
        }
        float leftover = amount * (1 - Shield / toShield);
        SetShield(0, Setting("ShieldDepleteFX"));
        return leftover;
    }

    void SetShield(float value, string fx)
    {
        bool wasUp = Shield > 0;
        Shield = value;
        if (fx.Length > 0)
            Game.Events.PlayFx(fx, Owner.Position, Owner);
        if (wasUp != Shield > 0 && Setting("ShieldActiveModelCondition") is { Length: > 0 } condition && Flags.ModelCondition.IndexOf(condition) >= 0)
            Owner.SetCondition(condition, Shield > 0);
    }
}
