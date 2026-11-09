using System.Numerics;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Spatial;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Logic.Modules.SpecialPowers;

/// <summary>
/// Kane's Wrath's TeleportSpecialAbilityUpdate (the Shock Troopers' Blink Packs, the Eradicator Hexapod, the Blink Mastermind,
/// the Firehawk's Stratofighter boosters): the unit stops, unpacks for UnpackTime, vanishes with SourceFX and reappears at the
/// target (no farther than MaxDistance, on the nearest free spot) with DestinationFX, then is busy for BusyDuration.
/// HeightToTeleportTo puts aircraft that high above the ground at the far end.
/// </summary>
public sealed class TeleportSpecialAbilityUpdate : Module, IUpdateModule, ISpecialPowerUpdate, ICreateModule
{
    SpecialPowerTemplate? power;
    float maxDistance;
    float height;
    int unpackFrames, packFrames, busyFrames;

    enum Phase { Idle, Unpacking, Packing, Busy }
    Phase phase;
    int phaseEnd;
    Vector3 destination;

    public bool IsPowerCurrentlyInUse => phase is Phase.Unpacking or Phase.Packing;

    protected override void Initialize()
    {
        power = Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate"));
        maxDistance = FloatSetting("MaxDistance", 1e6f);
        height = FloatSetting("HeightToTeleportTo");
        unpackFrames = FramesSetting("UnpackTime");
        packFrames = FramesSetting("PackTime");
        busyFrames = FramesSetting("BusyDuration");
    }

    public void OnCreate() => Owner.SleepUpdate(this);

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate requested, GameObject? target, Vector3? position, uint options)
    {
        if (requested != power || phase is Phase.Unpacking or Phase.Packing)
            return false;
        var to = target?.Position ?? position;
        if (to is not { } at)
            return false;
        // No farther than MaxDistance.
        var offset = new Vector2(at.X - Owner.Position.X, at.Y - Owner.Position.Y);
        if (offset.Length() > maxDistance)
            offset = Vector2.Normalize(offset) * maxDistance;
        destination = new Vector3(Owner.Position.X + offset.X, Owner.Position.Y + offset.Y, 0);
        Owner.AI?.Stop(Commands.CommandSource.AI);
        Owner.SetStatus("USING_ABILITY");
        StartPhase(Phase.Unpacking, unpackFrames);
        return true;
    }

    void StartPhase(Phase next, int frames)
    {
        phase = next;
        phaseEnd = Frame + frames;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (Owner.IsEffectivelyDead)
            return LogicTime.SleepForever;
        if (phase == Phase.Idle || Frame < phaseEnd)
            return phase == Phase.Idle ? LogicTime.SleepForever : 0;
        switch (phase)
        {
            case Phase.Unpacking:
                Teleport();
                StartPhase(Phase.Packing, packFrames);
                break;
            case Phase.Packing:
                Owner.FindModules<SpecialPowerModule>().FirstOrDefault(m => m.Power == power)?.TriggerSpecialPower(destination);
                StartPhase(Phase.Busy, busyFrames);
                break;
            case Phase.Busy:
                Owner.SetStatus("USING_ABILITY", false);
                phase = Phase.Idle;
                return LogicTime.SleepForever;
        }
        return 0;
    }

    void Teleport()
    {
        Game.Events.PlayFx(Setting("SourceFX"), Owner.Position, Owner);
        var spot = height > 0 ? destination : PositionFinder.FindPositionAround(Game, destination with { Z = Game.Terrain.GroundHeight(destination) }, 0, 80) ?? destination;
        spot.Z = Game.Terrain.GroundHeight(spot) + height;
        Owner.SetPosition(spot);
        Owner.AI?.Stop(Commands.CommandSource.AI);
        Game.Events.PlayFx(Setting("DestinationFX"), spot, Owner);
    }
}
