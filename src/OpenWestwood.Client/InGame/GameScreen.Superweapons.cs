using System.Numerics;
using OpenWestwood.Client.UI;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.SpecialPowers;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Client.InGame;

/// <summary>
/// The superweapon countdowns (the original's InGameUI superweapon timers): every player's finished superweapon
/// buildings with a PublicTimer power, "Scud Storm: 4:59" in the owner's colour, top right, for everyone to see;
/// bold and flashing once ready. Eva announces each one becoming ready (own, ally's or enemy's).
/// </summary>
public sealed partial class GameScreen
{
    readonly record struct SuperweaponTimer(GameObject Source, SpecialPowerModule Module);

    readonly List<SuperweaponTimer> superweaponTimers = new();
    readonly HashSet<(uint, string)> superweaponReadyAnnounced = new();

    /// <summary>The timers to show, in player order; a shared (synced) power once per player.</summary>
    void UpdateSuperweaponTimers()
    {
        superweaponTimers.Clear();
        foreach (var owner in Game.Players.All)
        {
            if (owner.IsNeutral)
                continue;
            var shared = new HashSet<string>();
            foreach (var obj in Game.ObjectsOwnedBy(owner))
            {
                if (obj.IsEffectivelyDead || obj.IsUnderConstruction || !obj.IsKindOf(Kind.Structure))
                    continue;
                foreach (var module in obj.FindModules<SpecialPowerModule>())
                {
                    if (module.Power is not { PublicTimer: true } power)
                        continue;
                    if (power.RequiredScience.Length > 0 && !owner.HasScience(power.RequiredScience))
                        continue;
                    if (power.SharedSyncedTimer && !shared.Add(power.Name))
                        continue;
                    superweaponTimers.Add(new SuperweaponTimer(obj, module));
                    AnnounceSuperweaponReady(obj, module);
                }
            }
        }
    }

    void AnnounceSuperweaponReady(GameObject source, SpecialPowerModule module)
    {
        var key = (source.Id, module.Power!.Name);
        if (!module.IsReady)
        {
            superweaponReadyAnnounced.Remove(key);
            return;
        }
        if (!superweaponReadyAnnounced.Add(key) || Game.Frame == 0)
            return;
        string? kind = module.Power.Type switch
        {
            "SPECIAL_PARTICLE_UPLINK_CANNON" or "SUPW_SPECIAL_PARTICLE_UPLINK_CANNON" or "LAZR_SPECIAL_PARTICLE_UPLINK_CANNON" => "ParticleCannon",
            "SPECIAL_NEUTRON_MISSILE" or "NUKE_SPECIAL_NEUTRON_MISSILE" or "SUPW_SPECIAL_NEUTRON_MISSILE" => "Nuke",
            "SPECIAL_SCUD_STORM" => "ScudStorm",
            _ => null,
        };
        if (kind == null)
            return;
        string whose = source.Owner == player ? "Own" : player.RelationshipTo(source.Owner) != Logic.Players.Relationship.Enemies ? "Ally" : "Enemy";
        Game.Events.Eva($"SuperweaponReady_{whose}_{kind}", player);
    }

    void DrawSuperweaponTimers(SpriteBatch batch)
    {
        if (!director.SpecialPowerDisplayEnabled || superweaponTimers.Count == 0)
            return;
        var settings = Game.Rules.Section("InGameUI");
        var position = RuleValue.Coord(settings.GetString("SuperweaponCountdownPosition", "X:0.9 Y:0.01"));
        var normal = ui.Fonts.Get(settings.GetString("SuperweaponCountdownNormalFont", "Arial"),
            RuleValue.Float(settings.GetString("SuperweaponCountdownNormalPointSize"), 10), RuleValue.Bool(settings.GetString("SuperweaponCountdownNormalBold")));
        var ready = ui.Fonts.Get(settings.GetString("SuperweaponCountdownReadyFont", "Arial"),
            RuleValue.Float(settings.GetString("SuperweaponCountdownReadyPointSize"), 10), RuleValue.Bool(settings.GetString("SuperweaponCountdownReadyBold", "Yes")));
        var flashColor = RuleColor.Parse(settings.GetString("SuperweaponCountdownFlashColor"), Vector4.One);
        int flashFrames = Math.Max(1, LogicTime.Frames(RuleValue.Float(settings.GetString("SuperweaponCountdownFlashDuration"), 500)));
        bool flashOn = Game.Frame / flashFrames % 2 == 0;
        var shadow = new Vector4(0, 0, 0, 1);

        float x = position.X * width, y = position.Y * height;
        float bottom = height * 0.82f;
        foreach (var (source, module) in superweaponTimers)
        {
            if (director.HiddenPowerTimers.Contains(source.Id))
                continue;
            bool isReady = module.IsReady;
            var font = isReady ? ready : normal;
            if (y >= bottom)
            {
                font.DrawWithShadow(batch, "...", new Vector2(x, y), flashColor, shadow);
                break;
            }
            int seconds = module.ReadyFrame < Game.Frame ? 0 : (module.ReadyFrame - Game.Frame) / LogicTime.FramesPerSecond;
            string name = ui.Strings.Get("GUI:" + module.Power!.Name) + ": ";
            string time = $"{seconds / 60}:{seconds % 60:00}";
            var color = isReady && flashOn ? flashColor : RuleColor.FromRgb(source.Owner.Color);
            // The name ends where the time begins (the original draws the name right-aligned to x).
            font.DrawWithShadow(batch, name, new Vector2(x - font.Measure(name), y), color, shadow);
            font.DrawWithShadow(batch, time, new Vector2(x, y), color, shadow);
            y += font.LineHeight;
        }
    }
}
