using System.Numerics;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: SHELL_PROBE=minutes plays the shell battle and reports every 15 s: each house's army and buildings, and
/// how many objects are being hurt and how far from the nearest base.</summary>
public class ShellBattleProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Watch()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SHELL_PROBE"), out int minutes) || !content.Available) return;
        var battle = OpenWestwood.Client.Shell.DuneShellBattle.Create(content.Rules!, content.FileSystem!, content.Models, 7)!.Value;
        var game = battle.Game;
        for (int f = 0; f < minutes * 60 * LogicTime.FramesPerSecond; f++)
        {
            game.Update();
            if (f % (15 * LogicTime.FramesPerSecond) != 0) continue;
            var houses = game.Players.Active.Where(p => p.AI != null).ToList();
            var bases = houses.Select(p => game.ObjectsOwnedBy(p).Where(o => o.IsKindOf(Kind.Structure) && !o.IsEffectivelyDead).ToList()).ToList();
            var hurt = game.Objects.Where(o => !o.IsEffectivelyDead && o.Body is { } b && game.Frame - b.LastDamageFrame < 4 * LogicTime.FramesPerSecond && !o.Owner.IsNeutral).ToList();
            float Near(OpenWestwood.Logic.Objects.GameObject o) => bases.SelectMany(b => b).Select(s => MathUtil.Distance2D(s.Position, o.Position)).DefaultIfEmpty(9999).Min();
            output.WriteLine($"{f / LogicTime.FramesPerSecond / 60}:{f / LogicTime.FramesPerSecond % 60:00} " + string.Join(" | ", houses.Select((p, i) =>
                $"{p.Side}: bld {bases[i].Count} army {game.ObjectsOwnedBy(p).Count(o => !o.IsEffectivelyDead && o.IsMobile && o.Template.Rules.TryGet("WeaponSet", out _))} ${p.Money.Amount}"))
                + $" || hurt {hurt.Count} (near a base {hurt.Count(o => Near(o) < 300)})");
        }
    }
}
