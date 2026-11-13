using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: SHELL_LANDING=Shooter,Target has the shooter fire at the target while the target drives off, and
/// prints where each of its shells ended (height above the ground there), to see misses that burst in mid-air.</summary>
public class ShellLandingProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Watch()
    {
        if (Environment.GetEnvironmentVariable("SHELL_LANDING") is not { } pair || pair.Split(',') is not [var shooterName, var targetName] || !content.Available) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 5,
        [
            new PlayerSlot("Me", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Them", "FactionAmerica", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var home = game.Players.Find("Me")!.StartPosition!.Value + new Vector3(0, -250, 0);
        var shooter = game.CreateObject(shooterName, game.Players.Find("Me")!, home, 0)!;
        var target = game.CreateObject(targetName, game.Players.Find("Them")!, home + new Vector3(170, 0, 0), 0)!;
        shooter.AI!.AttackObject(target);
        var live = new Dictionary<uint, Vector3>();
        for (int f = 0; f < 30 * 20; f++)
        {
            if (f == 40)
                target.AI?.MoveTo(target.Position + new Vector3(0, 400, 0));
            game.IsGameOver = false;
            game.Update();
            var now = game.Objects.Where(o => o.IsKindOf("PROJECTILE")).ToDictionary(o => o.Id, o => o.Position);
            foreach (var (id, pos) in live)
                if (!now.ContainsKey(id))
                    output.WriteLine($"f{f}: shell ended at {pos.X:0},{pos.Y:0} {pos.Z - game.Terrain.GroundHeight(pos):0.0} above the ground; target {MathUtil.Distance2D(pos, target.Position):0} away");
            live = now;
            if (target.IsEffectivelyDead)
            {
                output.WriteLine($"f{f}: target dead");
                break;
            }
        }
    }
}
