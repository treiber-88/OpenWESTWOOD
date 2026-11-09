using System.Numerics;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;

namespace OpenWestwood.Logic.Combat;

/// <summary>
/// Turns a weapon hit into damage on objects: the direct target, plus everything within the primary
/// and secondary damage radii that the weapon's RadiusDamageAffects allows (allies, enemies, neutrals,
/// self, similar units, airborne units). Mirrors the original's damage rules.
/// </summary>
public sealed class DamageSystem
{
    readonly GameLogic game;

    static readonly int AffectsSelf = Flags.WeaponAffects.IndexOf("SELF");
    static readonly int AffectsAllies = Flags.WeaponAffects.IndexOf("ALLIES");
    static readonly int AffectsEnemies = Flags.WeaponAffects.IndexOf("ENEMIES");
    static readonly int AffectsNeutrals = Flags.WeaponAffects.IndexOf("NEUTRALS");
    static readonly int KillsSelf = Flags.WeaponAffects.IndexOf("SUICIDE");
    static readonly int NotSimilar = Flags.WeaponAffects.IndexOf("NOT_SIMILAR");
    static readonly int NotAirborne = Flags.WeaponAffects.IndexOf("NOT_AIRBORNE");

    public DamageSystem(GameLogic game) => this.game = game;

    // Units taken over for a while (the Dune 2000 Deviator's ChangeOwner warhead): who owned them, and when they go back.
    readonly List<(GameObject Unit, Player Owner, int Until)> takeovers = new();

    /// <summary>Hands back units whose takeover has run out (each logic frame).</summary>
    public void UpdateTakeovers()
    {
        for (int i = takeovers.Count - 1; i >= 0; i--)
        {
            var (unit, owner, until) = takeovers[i];
            if (unit.IsDestroyed || unit.IsEffectivelyDead)
                takeovers.RemoveAt(i);
            else if (game.Frame >= until)
            {
                takeovers.RemoveAt(i);
                unit.SetOwner(owner);
                unit.AI?.Stop(Commands.CommandSource.AI);
            }
        }
    }

    /// <summary>
    /// A ChangeOwner warhead (TdChangeOwnerSeconds, TdChangeOwnerRadius): enemy and neutral vehicles within the
    /// radius serve the firer's owner for that long (not infantry, not buildings), then go back.
    /// </summary>
    void ChangeOwners(WeaponTemplate weapon, GameObject? source, Vector3 position)
    {
        float seconds = OpenWestwood.Core.Rules.RuleValue.Float(weapon.Rules.GetString("TdChangeOwnerSeconds"));
        if (seconds <= 0 || source == null || source.Owner is not { } taker)
            return;
        float radius = Math.Max(1f, OpenWestwood.Core.Rules.RuleValue.Float(weapon.Rules.GetString("TdChangeOwnerRadius")));
        foreach (var victim in game.ObjectsInRadius(position, radius))
        {
            if (victim.IsEffectivelyDead || victim.IsContained || !victim.IsMobile || victim.IsKindOf(Kind.Infantry) || victim.IsKindOf(Kind.Structure))
                continue;
            if (victim.Owner == taker || victim.Owner.IsAllyOf(taker) || victim.Owner == game.Players.Neutral && victim.AI == null)
                continue;
            int until = game.Frame + (int)(seconds * LogicTime.FramesPerSecond);
            int existing = takeovers.FindIndex(t => t.Unit == victim);
            if (existing >= 0)
                takeovers[existing] = takeovers[existing] with { Until = until }; // already turned: it stays turned longer
            else
                takeovers.Add((victim, victim.Owner, until));
            victim.SetOwner(taker);
            victim.AI?.Stop(Commands.CommandSource.AI);
        }
    }

    /// <summary>Applies a weapon's damage at a position (and to <paramref name="primaryVictim"/>, if any).</summary>
    public void DealWeaponDamage(WeaponTemplate weapon, GameObject? source, GameObject? primaryVictim, Vector3 position, WeaponBonus bonus)
    {
        if (primaryVictim != null && !primaryVictim.IsDestroyed)
            position = primaryVictim.Position;

        if (weapon.HistoricBonusCount > 0)
            game.HistoricDamage.Record(weapon, source, position);

        float primaryRadius = weapon.PrimaryDamageRadius * bonus.Radius;
        float secondaryRadius = weapon.SecondaryDamageRadius * bonus.Radius;
        float primaryDamage = weapon.PrimaryDamage * bonus.Damage;
        float secondaryDamage = weapon.SecondaryDamage * bonus.Damage;
        var affects = weapon.RadiusDamageAffects;
        float radius = Math.Max(primaryRadius, secondaryRadius);
        ChangeOwners(weapon, source, primaryVictim is { IsDestroyed: false } pv ? pv.Position : position);
        // OpenWESTWOOD (Kane's Wrath's Cultist): the unit it hits joins the attacker's side.
        if (weapon.MindControl && source != null && primaryVictim is { IsEffectivelyDead: false } controlled && controlled.IsMobile
            && !controlled.IsKindOf(Core.Kind.Structure) && controlled.Owner != source.Owner && !controlled.IsKindOf("IMMUNE_TO_PSIONICS"))
            controlled.Defect(source.Owner.DefaultTeam, 0);

        if (radius <= 0)
        {
            if (affects.Has(KillsSelf) && source != null)
            {
                source.Damage(new DamageInfo { Amount = 1e9f, DamageType = weapon.DamageType, DeathType = weapon.DeathType, SourceId = source.Id });
                return;
            }
            if (primaryVictim != null)
                Apply(primaryVictim, primaryDamage, weapon, source);
            return;
        }

        foreach (var victim in game.ObjectsInRadius(position, radius))
        {
            if (victim.IsEffectivelyDead || victim.IsContained)
                continue;

            bool killSelf = false;
            if (source != null && victim != primaryVictim)
            {
                if (affects.Has(KillsSelf) && victim == source)
                {
                    killSelf = true;
                }
                else
                {
                    if (!affects.Has(AffectsSelf) && (victim == source || source.ProducerId == victim.Id))
                        continue;
                    if (affects.Has(NotSimilar) && victim.Template == source.Template && victim.Owner.IsAllyOf(source.Owner))
                        continue;
                    if (affects.Has(NotAirborne) && victim.IsAirborneTarget)
                        continue;

                    var relation = victim.Owner.RelationshipTo(source.Owner);
                    int required = relation switch
                    {
                        Relationship.Allies => AffectsAllies,
                        Relationship.Enemies => AffectsEnemies,
                        _ => AffectsNeutrals,
                    };
                    if (!affects.Has(required))
                        continue;
                }
            }

            // Directional damage (flamethrowers, some blasts) only hits in front of the shooter.
            if (weapon.RadiusDamageAngle < MathF.PI - 0.001f && source != null)
            {
                var toVictim = victim.Position - source.Position;
                if (toVictim.LengthSquared() > 0.0001f &&
                    Vector3.Dot(Vector3.Normalize(toVictim with { Z = 0 }), MathUtil.Forward(source.Orientation)) < DMath.Cos(weapon.RadiusDamageAngle))
                    continue;
            }

            float distance = MathUtil.Distance2D(victim.Position, position) - victim.Radius;
            float amount = killSelf ? 1e9f : distance <= primaryRadius ? primaryDamage : secondaryDamage;
            Apply(victim, amount, weapon, source);
        }
    }

    void Apply(GameObject victim, float amount, WeaponTemplate weapon, GameObject? source)
    {
        victim.Damage(new DamageInfo
        {
            Amount = amount,
            DamageType = weapon.DamageType,
            DeathType = weapon.DeathType,
            DamageStatusType = weapon.DamageStatusType.Length > 0 ? weapon.DamageStatusType : null,
            SourceId = source?.Id ?? 0,
            SourcePosition = source?.Position,
        });
    }

    /// <summary>Fires a weapon template once from a position with no shooting object (e.g. OCL "FireWeapon", historic bonuses).</summary>
    public void FireTemporaryWeapon(string weaponName, GameObject? source, Vector3 position)
    {
        var weapon = game.Catalog.Weapon(weaponName);
        if (weapon == null)
            return;
        // The original's createAndFireTempWeapon: a real weapon fired from the source (projectiles fly).
        if (source != null)
        {
            Weapon.FireTemporary(weapon, source, position);
            return;
        }
        var vet = (int)(source?.Veterancy ?? VeterancyLevel.Regular);
        game.Events.PlayFx(weapon.FireFX[vet], position);
        game.Events.PlayFx(weapon.ProjectileDetonationFX[vet], position);
        DealWeaponDamage(weapon, source, null, position, WeaponBonus.None);
    }
}

/// <summary>
/// Weapons with a "historic bonus" trigger an extra weapon when hit several times in one area within a
/// short time (e.g. several napalm strikes in one spot become a firestorm).
/// </summary>
public sealed class HistoricDamageTracker
{
    readonly GameLogic game;
    readonly Dictionary<WeaponTemplate, List<(int Frame, Vector3 Position)>> history = new();

    public HistoricDamageTracker(GameLogic game) => this.game = game;

    public void Record(WeaponTemplate weapon, GameObject? source, Vector3 position)
    {
        if (!history.TryGetValue(weapon, out var hits))
            history[weapon] = hits = new();

        int oldest = game.Frame - weapon.HistoricBonusTime;
        hits.RemoveAll(h => h.Frame < oldest);
        float radiusSquared = weapon.HistoricBonusRadius * weapon.HistoricBonusRadius;
        int nearby = hits.Count(h => MathUtil.DistanceSquared2D(h.Position, position) < radiusSquared);

        if (nearby >= weapon.HistoricBonusCount - 1 && !weapon.HistoricBonusWeapon.Equals(weapon.Name, StringComparison.OrdinalIgnoreCase))
        {
            hits.Clear();
            game.Damage.FireTemporaryWeapon(weapon.HistoricBonusWeapon, source, position);
        }
        else
        {
            hits.Add((game.Frame, position));
        }
    }
}
