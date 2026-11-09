using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Combat;

/// <summary>
/// An object's weapons. The template lists several WeaponSets, each with Conditions; the set whose
/// conditions best match the object's current weapon-set flags (veterancy, upgrades, riders...) is used.
/// </summary>
public sealed class WeaponSet
{
    readonly GameObject owner;
    sealed record SetOption(FlagSet Conditions, RuleNode Node);

    readonly List<SetOption> sets = new();
    RuleNode? currentSet;

    public Weapon?[] Slots { get; } = new Weapon?[3];

    /// <summary>The slot chosen for the current attack (or the default).</summary>
    public WeaponSlot CurrentSlot { get; private set; } = WeaponSlot.Primary;

    /// <summary>Weapon slot locked by a player command (e.g. "switch to flashbangs"), or null.</summary>
    public WeaponSlot? LockedSlot { get; private set; }

    public WeaponSet(GameObject owner) => this.owner = owner;

    public bool HasAnyWeapon => Slots.Any(w => w != null);

    /// <summary>Every weapon is out of ammo (and none reload by themselves).</summary>
    public bool IsOutOfAmmo => HasAnyWeapon && All.All(w => w.Status == WeaponStatus.OutOfAmmo);
    public IEnumerable<Weapon> All => Slots.Where(w => w != null)!;
    public Weapon? Current => Slots[(int)CurrentSlot] ?? All.FirstOrDefault();

    internal void LoadFromTemplate()
    {
        sets.Clear();
        if (owner.Template.Rules.TryGet("WeaponSet", out var node))
            foreach (var set in node.AsList())
                sets.Add(new SetOption(Flags.WeaponSet.Parse(set.GetString("Conditions")), set));
        UpdateFromFlags();
    }

    /// <summary>Re-picks the active set after the object's weapon-set flags change.</summary>
    public void UpdateFromFlags()
    {
        var best = SparseMatch.Best(sets, set => [set.Conditions], owner.WeaponSetFlags)?.Node;

        if (best == currentSet)
            return;
        currentSet = best;

        var old = Slots.ToArray();
        Array.Clear(Slots);
        if (best == null)
            return;

        if (best.TryGet("Weapon", out var weapons))
        {
            foreach (var entry in weapons.AsList())
            {
                string[] words = RuleValue.Words(entry.Value);
                if (words.Length < 2 || !Enum.TryParse<WeaponSlot>(words[0], ignoreCase: true, out var slot))
                    continue;
                var template = owner.Game.Catalog.Weapon(words[1]);
                if (template == null)
                    continue;
                // Keep ammo and reload state when the same weapon stays in the slot.
                Slots[(int)slot] = old[(int)slot]?.Template == template ? old[(int)slot] : new Weapon(owner, template, slot);
            }
        }

        owner.SetStatus("CAN_ATTACK", HasAnyWeapon);
    }

    /// <summary>The lock lasts only for the current attack (the original's LOCKED_TEMPORARILY).</summary>
    public bool LockIsTemporary { get; private set; }

    /// <param name="temporarily">Only for the attack about to be ordered: released when it ends (superweapon volleys).</param>
    public void LockSlot(WeaponSlot slot, bool temporarily = false)
    {
        // A temporary lock never replaces a lasting one (the original's setWeaponLock).
        if (temporarily && LockedSlot != null && !LockIsTemporary)
            return;
        LockedSlot = slot;
        CurrentSlot = slot;
        LockIsTemporary = temporarily;
    }

    public void ReleaseLock()
    {
        LockedSlot = null;
        LockIsTemporary = false;
    }

    public void ReleaseTemporaryLock()
    {
        if (LockIsTemporary)
            ReleaseLock();
    }

    /// <summary>Instantly refills every weapon's clip (e.g. FireWeaponPower before its volley).</summary>
    public void ReloadAll()
    {
        foreach (var weapon in All)
            weapon.Reload();
    }

    /// <summary>KindOf preferences per slot ("PreferredAgainst: PRIMARY INFANTRY").</summary>
    IEnumerable<(WeaponSlot Slot, string[] Kinds)> Preferences()
    {
        if (currentSet == null || !currentSet.TryGet("PreferredAgainst", out var node))
            yield break;
        foreach (var entry in node.AsList())
        {
            string[] words = RuleValue.Words(entry.Value);
            if (words.Length >= 2 && Enum.TryParse<WeaponSlot>(words[0], true, out var slot))
                yield return (slot, words[1..]);
        }
    }

    /// <summary>
    /// AutoChooseSources ("PRIMARY FROM_SCRIPT FROM_AI"): may this slot be chosen for an order from this source?
    /// Every source by default. As in the original's chooseBestWeaponForTarget, a slot that doesn't list the source is
    /// still allowed when its mask has the bit numbered CMD_DEFAULT_SWITCH_WEAPON (4) - which is FROM_AI's bit, so a
    /// slot listing FROM_AI may be chosen for any order; a slot with NONE is never chosen (only used when locked).
    /// </summary>
    bool MayChoose(WeaponSlot slot, Commands.CommandSource source)
    {
        if (currentSet == null || !currentSet.TryGet("AutoChooseSources", out var node))
            return true;
        foreach (var entry in node.AsList())
        {
            string[] words = RuleValue.Words(entry.Value);
            if (words.Length == 0 || !Enum.TryParse<WeaponSlot>(words[0], true, out var listed) || listed != slot)
                continue;
            int mask = 0;
            foreach (string word in words[1..])
                mask |= word.ToUpperInvariant() switch
                {
                    "FROM_PLAYER" => 1 << 0,
                    "FROM_SCRIPT" => 1 << 1,
                    "FROM_AI" => 1 << 2,
                    "FROM_DOZER" => 1 << 3,
                    "DEFAULT_SWITCH_WEAPON" => 1 << 4,
                    _ => 0,
                };
            int bit = source switch
            {
                Commands.CommandSource.Player => 1 << 0,
                Commands.CommandSource.Script => 1 << 1,
                Commands.CommandSource.AI => 1 << 2,
                _ => 1 << 3,
            };
            return (mask & bit) != 0 || (mask & 4) != 0;
        }
        return true;
    }

    /// <summary>
    /// Picks the best weapon to use against a target, and makes it current. Returns null if none can hit it.
    /// With a <paramref name="source"/>, only weapons that may be chosen for that source's orders (AutoChooseSources).
    /// </summary>
    public Weapon? ChooseFor(GameObject target, Commands.CommandSource? source = null)
    {
        if (LockedSlot is { } locked)
            return Slots[(int)locked] is { } lockedWeapon && lockedWeapon.CanTarget(target) ? lockedWeapon : null;

        bool Allowed(Weapon w) => source is not { } from || MayChoose(w.Slot, from);
        foreach (var (slot, kinds) in Preferences())
        {
            if (Slots[(int)slot] is { } preferred && Allowed(preferred) && preferred.CanTarget(target) && kinds.Any(target.IsKindOf))
            {
                CurrentSlot = slot;
                return preferred;
            }
        }

        // Otherwise the first weapon that can hit the target, preferring one already in range.
        Weapon? choice = All.Where(w => Allowed(w) && w.CanTarget(target)).OrderByDescending(w => w.IsInRange(target)).ThenBy(w => w.Slot).FirstOrDefault();
        if (choice != null)
            CurrentSlot = choice.Slot;
        return choice;
    }

    /// <summary>The longest range of any weapon that can attack this target (for approaching it).</summary>
    public float MaxRangeAgainst(GameObject target) =>
        All.Where(w => w.CanTarget(target)).Select(w => w.AttackRange).DefaultIfEmpty(0).Max();

    public float MaxRange => All.Select(w => w.AttackRange).DefaultIfEmpty(0).Max();
}
