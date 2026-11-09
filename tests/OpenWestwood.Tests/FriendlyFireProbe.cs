using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>
/// Probe (FF_PROBE=1): two computer players fight; every hit a unit does to itself or to its own side is listed.
/// FF_MAP, FF_A, FF_B (factions), FF_MINUTES.
/// </summary>
public class FriendlyFireProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void SelfAndFriendlyHits()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("FF_PROBE") != "1") return;
        string mapName = Environment.GetEnvironmentVariable("FF_MAP") ?? "Maps/Tournament Desert/Tournament Desert.map";
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, mapName);
        var game = GameSetup.FromMap(map, content.Rules!, 7,
        [
            new PlayerSlot("A", Environment.GetEnvironmentVariable("FF_A") ?? "FactionAmerica", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("B", Environment.GetEnvironmentVariable("FF_B") ?? "FactionChina", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var hits = new Dictionary<string, (int Count, float Amount, int Kills)>();
        game.Events.OnDamaged += (victim, info) =>
        {
            if (info.SourceId == 0 && info.Amount > 0 && !victim.IsKindOf("PROJECTILE") && info.DamageType is not ("UNRESISTABLE" or "FALLING" or "HEALING"))
            {
                string none = $"NO SOURCE -> {victim.Template.Name} ({info.DamageType}, {info.DeathType})";
                var n = hits.GetValueOrDefault(none);
                hits[none] = (n.Count + 1, n.Amount + info.Amount, n.Kills + (victim.IsEffectivelyDead ? 1 : 0));
                return;
            }
            if (game.FindObject(info.SourceId) is not { } source || victim.Owner != source.Owner || info.Amount <= 0)
                return;
            if (victim.IsKindOf(Kind.Structure) && !victim.IsKindOf("VEHICLE"))
                return;
            string key = $"{source.Template.Name} -> {(victim == source ? "ITSELF" : victim.Template.Name)} ({info.DamageType})";
            if (!victim.IsKindOf("PROJECTILE") && victim != source && source.AI is { } ai)
            {
                var target = game.FindObject(ai.TargetId);
                output.WriteLine($"  frame {game.Frame}: {key} {info.Amount:F0}: victim {Logic.Core.MathUtil.Distance2D(source.Position, victim.Position):F0} away, target {(target == null ? "none" : $"{target.Template.Name} {Logic.Core.MathUtil.Distance2D(source.Position, target.Position):F0} away")}, state {ai.State}");
            }
            var h = hits.GetValueOrDefault(key);
            hits[key] = (h.Count + 1, h.Amount + info.Amount, h.Kills + (victim.IsEffectivelyDead ? 1 : 0));
        };
        // Projectiles that go off close to their shooter while their target is further away.
        var early = new Dictionary<string, (int Early, int All)>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var launcherField = typeof(Logic.Modules.Projectiles.ProjectileModule).GetField("Launcher", flags)!;
        var targetField = typeof(Logic.Modules.Projectiles.ProjectileModule).GetField("TargetPosition", flags)!;
        game.Events.OnObjectDestroyed += obj =>
        {
            if (obj.FindModule<Logic.Modules.Projectiles.ProjectileModule>() is not { } projectile || launcherField.GetValue(projectile) is not Logic.Objects.GameObject launcher)
                return;
            var aim = (System.Numerics.Vector3)targetField.GetValue(projectile)!;
            float fromShooter = Logic.Core.MathUtil.Distance2D(obj.Position, launcher.Position);
            float toAim = Logic.Core.MathUtil.Distance2D(obj.Position, aim);
            string key = $"{launcher.Template.Name} ({obj.Template.Name})";
            var e = early.GetValueOrDefault(key);
            bool isEarly = fromShooter < 40 && toAim > 60;
            early[key] = (e.Early + (isEarly ? 1 : 0), e.All + 1);
            if (isEarly && e.Early < 3)
                output.WriteLine($"  early: {key} went off {fromShooter:F0} from its shooter, {toAim:F0} short of its aim, z {obj.Position.Z - game.Terrain.GroundHeight(obj.Position):F1} above ground; near: {string.Join(", ", game.ObjectsInRadius(obj.Position, 25).Where(o => o != obj && o != launcher).Select(o => $"{o.Template.Name}{(o.Owner == launcher.Owner ? "(own)" : "")} {Logic.Core.MathUtil.Distance2D(o.Position, obj.Position):F0}"))}");
        };
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("FF_MINUTES"), out int m) ? m : 10;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
            game.Update();
        foreach (var (key, h) in hits.OrderByDescending(h => h.Value.Amount))
            output.WriteLine($"{key}: {h.Count} hits, {h.Amount:F0} damage, {h.Kills} killed");
        foreach (var (key, e) in early.Where(e => e.Value.Early > 0).OrderByDescending(e => e.Value.Early))
            output.WriteLine($"EARLY {key}: {e.Early} of {e.All}");
        output.WriteLine($"{hits.Count} kinds of friendly hits; units now A {game.ObjectsOwnedBy(game.Players.Find("A")!).Count()} B {game.ObjectsOwnedBy(game.Players.Find("B")!).Count()}");
    }
}
