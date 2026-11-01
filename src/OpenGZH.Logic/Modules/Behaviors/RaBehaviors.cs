using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

// OpenGZH additions for Red Alert's factions: the special weapons that aren't a simple object creation list
// (HouseClass::Place_Special_Blast and TechnoClass's Iron Curtain / Chronosphere effects in EA's RA source).

/// <summary>
/// A Red Alert special weapon (Mode):
/// <list type="bullet">
/// <item>GPS: the GPS satellite; once launched its owner sees the whole map for good (no target).</item>
/// <item>IronCurtain: one of its owner's vehicles or buildings can't be hurt for Duration seconds (RULES.INI
/// IronCurtain = .75 minutes).</item>
/// <item>Chronosphere: first a vehicle of its owner's is picked (the power stays ready), then a place: the vehicle
/// is shifted there, and back where it came from after Duration seconds (ChronoDuration = 3 minutes).</item>
/// </list>
/// </summary>
public sealed class RaSuperweapon : SpecialPowerModule, IUpdateModule
{
    public string Mode => Setting("Mode");
    float DurationSeconds => FloatSetting("Duration", Mode == "Chronosphere" ? 180 : 45);

    readonly List<(uint Id, int Until, Vector3 Home)> affected = new();

    /// <summary>Chronosphere: the vehicle picked, waiting for its destination.</summary>
    public uint PendingUnit { get; private set; }

    public override void DoSpecialPower(uint options)
    {
        if (Mode != "GPS" || Owner.IsDisabled)
            return;
        Game.Shroud.RevealMapForPlayerPermanently(Owner.Owner);
        TriggerSpecialPower(null);
    }

    public override void DoSpecialPowerAtObject(GameObject target, uint options) => Use(target.Position, target);

    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options) => Use(location, null);

    void Use(Vector3 location, GameObject? target)
    {
        // (Readiness is checked by whoever uses it, as for every special power.)
        if (Owner.IsDisabled)
            return;
        target ??= Game.ObjectsInRadius(location, TdScale.Cell * 0.8f)
            .Where(o => o.Owner == Owner.Owner && !o.IsEffectivelyDead && Eligible(o))
            .OrderBy(o => MathUtil.Distance2D(o.Position, location)).FirstOrDefault();
        switch (Mode)
        {
            case "IronCurtain":
                if (target == null || target.Owner != Owner.Owner || !Eligible(target) || target.Body == null)
                    return;
                target.Body.Indestructible = true;
                target.SetTint(TintStatus.Frenzy, true); // shown red, as RA's curtained units are
                affected.Add((target.Id, Game.Frame + (int)(DurationSeconds * LogicTime.FramesPerSecond), target.Position));
                Game.Events.Eva("RaIronCurtain", Owner.Owner);
                TriggerSpecialPower(location);
                break;
            case "Chronosphere":
                if (PendingUnit == 0 || Game.FindObject(PendingUnit) is not { IsEffectivelyDead: false } unit)
                {
                    // First click: the vehicle to send.
                    if (target != null && target.IsMobile && !target.IsKindOf(Kind.Infantry) && !target.IsKindOf(Kind.Aircraft))
                    {
                        PendingUnit = target.Id;
                        Game.Events.Eva("RaChronoSelectDestination", Owner.Owner);
                    }
                    return;
                }
                // Second click: there.
                if (Game.Pathfinder.NearestPassable(location, unit.AI?.Surfaces ?? Locomotion.Surfaces.Ground, 2) is not { } spot)
                    return;
                var home = unit.Position;
                Shift(unit, spot);
                affected.Add((unit.Id, Game.Frame + (int)(DurationSeconds * LogicTime.FramesPerSecond), home));
                PendingUnit = 0;
                Game.Events.Eva("RaChronoshift", Owner.Owner);
                TriggerSpecialPower(location);
                break;
        }
    }

    static bool Eligible(GameObject o) => !o.IsKindOf(Kind.Infantry) && (o.IsMobile || o.IsKindOf(Kind.Structure));

    void Shift(GameObject unit, Vector3 to)
    {
        Game.Events.PlayFx("RAFX:CHRONOBOX", unit.Position);
        // ChronoKillCargo: what it carries is lost in the shift.
        if (unit.Contain is { } hold)
            foreach (var passenger in hold.Passengers.ToList())
                passenger.Kill("NORMAL");
        if (unit.AI is { } ai)
        {
            ai.Teleport(to);
            ai.Stop(Commands.CommandSource.AI);
        }
        else
            unit.SetPosition(to with { Z = Game.Terrain.GroundHeight(to) });
        Game.Events.PlayFx("RAFX:CHRONOBOX", to);
    }

    public int Update()
    {
        for (int i = affected.Count - 1; i >= 0; i--)
        {
            var (id, until, home) = affected[i];
            if (Game.FindObject(id) is not { IsEffectivelyDead: false } obj)
            {
                affected.RemoveAt(i);
                continue;
            }
            if (Game.Frame < until)
                continue;
            affected.RemoveAt(i);
            if (Mode == "IronCurtain" && obj.Body != null)
            {
                obj.Body.Indestructible = false;
                obj.SetTint(TintStatus.Frenzy, false);
            }
            if (Mode == "Chronosphere" && Game.Pathfinder.NearestPassable(home, obj.AI?.Surfaces ?? Locomotion.Surfaces.Ground, 3) is { } back)
                Shift(obj, back);
        }
        return LogicTime.FramesPerSecond / 2;
    }
}
