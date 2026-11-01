using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

// OpenGZH additions for Red Alert 2 / Yuri's Revenge: the superweapons that aren't an object creation list
// (RULESMD.INI's SuperWeaponTypes with the [General] / [CombatDamage] values that drive them).

/// <summary>
/// A Red Alert 2 superweapon (Mode), aimed at a place:
/// <list type="bullet">
/// <item>LightningStorm: after Deferment seconds, bolts strike random spots within Spread cells of it every
/// HitDelay seconds for Duration seconds (LightningDamage with the IonWH warhead, WeatherConBolts).</item>
/// <item>IronCurtain: its owner's vehicles and buildings within Radius cells can't be hurt for Duration seconds;
/// infantry caught in it die (as in RA2).</item>
/// <item>ChronoSphere: first an area, then a place: every vehicle within Radius cells of the first is shifted
/// to the second for good; infantry don't survive the trip.</item>
/// <item>PsychicDominator: every unit within Radius cells becomes its owner's; buildings take Damage.</item>
/// <item>GeneticConverter: infantry within Radius cells turn into Unit (Brutes) for its owner.</item>
/// <item>ForceShield: its owner's buildings within Radius cells can't be hurt for Duration seconds, and its
/// owner's power goes out for Blackout seconds.</item>
/// </list>
/// </summary>
public sealed class Ra2Superweapon : SpecialPowerModule, IUpdateModule
{
    public string Mode => Setting("Mode");
    float Cells(string key, float fallback) => FloatSetting(key, fallback) * TdScale.Cell / TdScale.Size;
    int Frames(string key, float fallbackSeconds) => (int)(FloatSetting(key, fallbackSeconds) * LogicTime.FramesPerSecond);

    readonly List<(uint Id, int Until)> shielded = new();
    readonly List<(Vector3 Center, int Start, int End, int NextHit)> storms = new();

    /// <summary>ChronoSphere: the area picked, waiting for its destination.</summary>
    public Vector3? PendingArea { get; private set; }

    public override void DoSpecialPowerAtObject(GameObject target, uint options) => Use(target.Position);

    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options) => Use(location);

    IEnumerable<GameObject> Around(Vector3 center, float radius) =>
        Game.ObjectsInRadius(center, radius).Where(o => !o.IsEffectivelyDead && !o.IsUnderConstruction);

    void Use(Vector3 location)
    {
        // (Readiness is checked by whoever uses it, as for every special power.)
        if (Owner.IsDisabled)
            return;
        var player = Owner.Owner;
        float radius = Cells("Radius", 1.5f);
        switch (Mode)
        {
            case "LightningStorm":
            {
                int start = Game.Frame + Frames("Deferment", 16);
                storms.Add((location, start, start + Frames("Duration", 12), start));
                Game.Events.PlayFx(Setting("CloudFX"), location);
                Game.Events.Eva("Ra2LightningStorm", player);
                break;
            }
            case "IronCurtain":
            {
                Game.Events.PlayFx(Setting("FX"), location);
                foreach (var o in Around(location, radius).ToList())
                {
                    if (o.IsKindOf(Kind.Infantry))
                        o.Kill("NORMAL");
                    else if (o.Body != null && (o.IsMobile || o.IsKindOf(Kind.Structure)))
                        Shield(o, Frames("Duration", 50), TintStatus.Frenzy);
                }
                Game.Events.Eva("Ra2IronCurtain", player);
                break;
            }
            case "ForceShield":
            {
                Game.Events.PlayFx(Setting("FX"), location);
                foreach (var o in Around(location, radius).Where(o => o.Owner == player && o.IsKindOf(Kind.Structure)).ToList())
                    Shield(o, Frames("Duration", 33), TintStatus.Frenzy);
                player.Energy.SabotagedUntilFrame = Math.Max(player.Energy.SabotagedUntilFrame, Game.Frame + Frames("Blackout", 66));
                break;
            }
            case "ChronoSphere":
            {
                if (PendingArea is not { } from)
                {
                    // First click: the area to send (the power stays ready).
                    PendingArea = location;
                    Game.Events.Eva("Ra2ChronoSelectDestination", player);
                    return;
                }
                PendingArea = null;
                Game.Events.PlayFx(Setting("FX"), from);
                Game.Events.PlayFx(Setting("ArriveFX"), location);
                foreach (var o in Around(from, radius).Where(o => o.IsMobile && !o.IsKindOf(Kind.Aircraft)).ToList())
                {
                    if (o.IsKindOf(Kind.Infantry))
                    {
                        o.Kill("NORMAL");
                        continue;
                    }
                    var to = location + (o.Position - from) with { Z = 0 };
                    if (Game.Pathfinder.NearestPassable(to, o.AI?.Surfaces ?? Locomotion.Surfaces.Ground, 2) is not { } spot)
                    {
                        o.Kill("NORMAL"); // nowhere it can be (water for a tank)
                        continue;
                    }
                    if (o.Contain is { } hold)
                        foreach (var passenger in hold.Passengers.ToList())
                            passenger.Kill("NORMAL");
                    if (o.AI is { } ai)
                    {
                        ai.Teleport(spot);
                        ai.Stop(Commands.CommandSource.AI);
                    }
                }
                Game.Events.Eva("Ra2Chronoshift", player);
                break;
            }
            case "PsychicDominator":
            {
                Game.Events.PlayFx(Setting("FX"), location);
                float damage = FloatSetting("Damage", 1000);
                foreach (var o in Around(location, radius).ToList())
                {
                    if (o.IsKindOf(Kind.Structure))
                        o.Body?.AttemptDamage(new Combat.DamageInfo { Amount = damage, DamageType = "UNRESISTABLE", SourceId = Owner.Id });
                    else if (o.IsMobile && o.Owner != player && !o.IsKindOf("IMMUNE_TO_PSIONICS"))
                        o.Defect(player.DefaultTeam, 0);
                }
                Game.Events.Eva("Ra2PsychicDominator", player);
                break;
            }
            case "GeneticConverter":
            {
                Game.Events.PlayFx(Setting("FX"), location);
                string brute = Setting("Unit");
                foreach (var o in Around(location, radius).Where(o => o.IsKindOf(Kind.Infantry) && !o.IsKindOf("IMMUNE_TO_PSIONICS")).ToList())
                {
                    var at = o.Position;
                    float facing = o.Orientation;
                    Game.DestroyObject(o);
                    if (brute.Length > 0)
                        Game.CreateObject(brute, player, at, facing);
                }
                Game.Events.Eva("Ra2GeneticMutator", player);
                break;
            }
            default:
                return;
        }
        TriggerSpecialPower(location);
    }

    void Shield(GameObject o, int frames, TintStatus tint)
    {
        o.Body!.Indestructible = true;
        o.SetTint(tint, true);
        shielded.Add((o.Id, Game.Frame + frames));
    }

    public int Update()
    {
        for (int i = shielded.Count - 1; i >= 0; i--)
        {
            var (id, until) = shielded[i];
            if (Game.FindObject(id) is not { } o)
            {
                shielded.RemoveAt(i);
                continue;
            }
            if (Game.Frame < until)
                continue;
            shielded.RemoveAt(i);
            if (o.Body != null)
                o.Body.Indestructible = false;
            o.SetTint(TintStatus.Frenzy, false);
        }
        for (int i = storms.Count - 1; i >= 0; i--)
        {
            var (center, start, end, nextHit) = storms[i];
            if (Game.Frame >= end)
            {
                storms.RemoveAt(i);
                continue;
            }
            if (Game.Frame < nextHit)
                continue;
            // A bolt somewhere in the storm (LightningCellSpread), LightningHitDelay apart.
            float spread = Cells("Spread", 10) * MathF.Sqrt(Game.Random.Float(0, 1));
            float angle = Game.Random.Float(0, 1) * MathF.Tau;
            var spot = center + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * spread;
            spot = spot with { Z = Game.Terrain.GroundHeight(spot) };
            Game.Damage.FireTemporaryWeapon(Setting("Weapon"), null, spot);
            storms[i] = (center, start, end, Game.Frame + Math.Max(1, Frames("HitDelay", 0.67f)));
        }
        return 1;
    }
}
