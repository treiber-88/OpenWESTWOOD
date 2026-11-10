using System.Numerics;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Combat;

/// <summary>
/// OpenWESTWOOD: Red Alert 2's leaping attackers (WeaponTemplate.LimboLaunch) and parasites (WeaponTemplate.Parasite).
/// An attack dog pounces: it is out of sight while its leap (the weapon's projectile, drawn as the leaping dog) flies,
/// lands where it hits, and a soldier it lands on dies (the bite kills). A terror drone jumps into a vehicle and stays
/// inside it, out of reach, doing its weapon's damage every DelayBetweenShots until the vehicle is destroyed; then it
/// comes out where the wreck is. (The original's FootClass LimboLaunch and ParasiteClass.)
/// State is kept in plain dictionaries, so a saved game keeps it.
/// </summary>
public sealed class ParasiteSystem(GameLogic game)
{
    // A leap in the air: firer -> its projectile, what it was fired at, the weapon, where the projectile last was.
    public Dictionary<uint, uint> LeapProjectile { get; } = new();
    public Dictionary<uint, uint> LeapTarget { get; } = new();
    public Dictionary<uint, string> LeapWeapon { get; } = new();
    public Dictionary<uint, Vector3> LeapPosition { get; } = new();

    // A parasite inside its host: parasite -> host, weapon, next frame it does damage.
    public Dictionary<uint, uint> Host { get; } = new();
    public Dictionary<uint, string> HostWeapon { get; } = new();
    public Dictionary<uint, int> NextBite { get; } = new();

    /// <summary>In the air or inside a host: it can't fire (nor be seen, hit or ordered about).</summary>
    public bool IsBusy(GameObject obj) => LeapProjectile.ContainsKey(obj.Id) || Host.ContainsKey(obj.Id);

    static void SetOutOfWorld(GameLogic game, GameObject obj, bool outOfWorld)
    {
        obj.IsHiddenInContainer = outOfWorld;
        if (outOfWorld)
            game.Partition.Remove(obj);
        else
            game.Partition.Add(obj);
        obj.SetDisabled("DISABLED_HELD", outOfWorld);
    }

    /// <summary>The weapon just launched its projectile: the firer leaps with it.</summary>
    public void BeginLeap(GameObject firer, GameObject projectile, GameObject? target, WeaponTemplate weapon)
    {
        LeapProjectile[firer.Id] = projectile.Id;
        LeapTarget[firer.Id] = target?.Id ?? 0;
        LeapWeapon[firer.Id] = weapon.Name;
        LeapPosition[firer.Id] = projectile.Position;
        firer.AI?.Stop(Commands.CommandSource.AI);
        SetOutOfWorld(game, firer, true);
    }

    public void Update()
    {
        if (LeapProjectile.Count == 0 && Host.Count == 0)
            return;
        foreach (uint id in LeapProjectile.Keys.ToList())
        {
            var firer = game.FindObject(id);
            if (game.FindObject(LeapProjectile[id]) is { IsDestroyed: false } projectile)
            {
                LeapPosition[id] = projectile.Position;
                if (firer != null)
                    firer.SetPosition(projectile.Position with { Z = game.Terrain.LayerHeight(projectile.Position.X, projectile.Position.Y) });
                continue;
            }
            // Landed.
            var at = LeapPosition[id];
            var target = game.FindObject(LeapTarget[id]);
            var weapon = game.Catalog.Weapon(LeapWeapon[id]);
            LeapProjectile.Remove(id);
            LeapTarget.Remove(id);
            LeapWeapon.Remove(id);
            LeapPosition.Remove(id);
            if (firer == null || firer.IsDestroyed)
                continue;
            bool reached = target is { IsEffectivelyDead: false } && MathUtil.Distance2D(target.Position, at) < target.Radius + 20;
            if (weapon is { Parasite: true } && reached && target!.IsKindOf(Kind.Infantry))
            {
                firer.ScoreTheKill(target);
                target.Kill("NORMAL", firer.Id);
            }
            else if (weapon is { Parasite: true } && reached && !target!.IsKindOf(Kind.Infantry))
            {
                // Inside the vehicle.
                Host[id] = target.Id;
                HostWeapon[id] = weapon.Name;
                NextBite[id] = game.Frame + Math.Max(1, weapon.MinDelayBetweenShots);
                firer.SetPosition(target.Position);
                continue;
            }
            Land(firer, at);
        }

        foreach (uint id in Host.Keys.ToList())
        {
            var parasite = game.FindObject(id);
            var host = game.FindObject(Host[id]);
            if (parasite == null || parasite.IsDestroyed)
            {
                Release(id);
                continue;
            }
            if (host == null || host.IsEffectivelyDead || host.IsDestroyed)
            {
                var where = host?.Position ?? parasite.Position;
                Release(id);
                Land(parasite, where);
                continue;
            }
            parasite.SetPosition(host.Position);
            if (game.Frame < NextBite[id] || game.Catalog.Weapon(HostWeapon[id]) is not { } weapon)
                continue;
            NextBite[id] = game.Frame + Math.Max(1, weapon.MinDelayBetweenShots);
            game.Damage.DealWeaponDamage(weapon, parasite, host, host.Position, WeaponBonus.None);
            game.Events.PlaySound(weapon.FireSound, host.Position, source: host);
        }
    }

    void Release(uint id)
    {
        Host.Remove(id);
        HostWeapon.Remove(id);
        NextBite.Remove(id);
    }

    void Land(GameObject obj, Vector3 at)
    {
        var spot = Spatial.PositionFinder.FindPositionAround(game, at with { Z = game.Terrain.GroundHeight(at) }, 0, 60) ?? at;
        obj.SetPosition(spot with { Z = game.Terrain.LayerHeight(spot.X, spot.Y) });
        SetOutOfWorld(game, obj, false);
    }
}
