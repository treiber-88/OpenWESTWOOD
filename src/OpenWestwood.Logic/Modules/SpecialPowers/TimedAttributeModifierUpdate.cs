using System.Numerics;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Logic.Modules.SpecialPowers;

/// <summary>
/// OpenWESTWOOD, for Kane's Wrath's self-buff abilities (a SpecialPower with AttributeModifierAffectsSelf: the Steel Talons'
/// Adaptive Armor): when its power is used, the unit's weapon Damage, RateOfFire and Range, its Speed and the damage it takes
/// (Armor) are multiplied by these percentages for Duration, with ModelConditions set meanwhile.
/// </summary>
public sealed class TimedAttributeModifierUpdate : Module, IUpdateModule, ISpecialPowerUpdate, ICreateModule
{
    SpecialPowerTemplate? power;
    int durationFrames;
    float damage, rateOfFire, range, speed, armor;
    string[] conditions = [];
    int endFrame = -1;

    public bool IsPowerCurrentlyInUse => endFrame >= 0;

    float Percent(string key) => Setting(key) is { Length: > 0 } v ? OpenWestwood.Core.Rules.RuleValue.Float(v.TrimEnd('%'), 100) / 100f : 1;

    protected override void Initialize()
    {
        power = Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate"));
        durationFrames = Math.Max(1, FramesSetting("Duration"));
        damage = Percent("Damage");
        rateOfFire = Percent("RateOfFire");
        range = Percent("Range");
        speed = Percent("Speed");
        armor = Percent("Armor");
        conditions = Setting("ModelConditions").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public void OnCreate() => Owner.SleepUpdate(this);

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate requested, GameObject? target, Vector3? position, uint options)
    {
        if (requested != power || endFrame >= 0 || Owner.IsEffectivelyDead)
            return false;
        Apply(on: true);
        endFrame = Frame + durationFrames;
        Owner.FindModules<SpecialPowerModule>().FirstOrDefault(m => m.Power == power)?.TriggerSpecialPower(null);
        Owner.WakeUpdate(this);
        return true;
    }

    void Apply(bool on)
    {
        float Factor(float f) => on ? f : 1 / f;
        Owner.DamageBonus *= Factor(damage);
        Owner.RateOfFireBonus *= Factor(rateOfFire);
        Owner.RangeBonus *= Factor(range);
        Owner.SpeedBonus *= Factor(speed);
        if (Owner.Body is { } body)
            body.DamageScalar *= Factor(armor);
        foreach (string c in conditions)
            Owner.SetCondition(c, on);
    }

    public int Update()
    {
        if (endFrame < 0)
            return LogicTime.SleepForever;
        if (Frame < endFrame && !Owner.IsEffectivelyDead)
            return endFrame - Frame;
        Apply(on: false);
        endFrame = -1;
        return LogicTime.SleepForever;
    }
}
