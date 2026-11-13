using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Debug: UNIT_MOTION=Template[,Template...] sends each unit on a walk/drive with a turn in it and prints, every
/// few frames, its facing against the way it is actually going, its speed and whether it counts as walking.</summary>
public class UnitMotionProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Watch()
    {
        if (Environment.GetEnvironmentVariable("UNIT_MOTION") is not { } names || !content.Available) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 5,
        [
            new PlayerSlot("Me", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Idle", "FactionAmerica", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var me = game.Players.Find("Me")!;
        var home = me.StartPosition!.Value;
        foreach (string name in names.Split(','))
        {
            var unit = game.CreateObject(name.Trim(), me, home + new Vector3(0, -200, 0), 0)!;
            unit.AI!.MoveTo(unit.Position + new Vector3(0, -300, 0));
            output.WriteLine($"== {name} loco {unit.AI.CurrentLocomotor?.Appearance} speed {unit.AI.CurrentLocomotor?.MaxSpeedFor(unit):0.00}/frame");
            var last = unit.Position;
            for (int f = 0; f < 150; f++)
            {
                if (f == 75)
                    unit.AI.MoveTo(unit.Position + new Vector3(300, 0, 0));
                game.IsGameOver = false;
                game.Update();
                var step = unit.Position - last;
                last = unit.Position;
                if (f % 6 == 0 || f is 76 or 77 or 78 or 80)
                {
                    float heading = step.LengthSquared() > 1e-6f ? MathF.Atan2(step.Y, step.X) : float.NaN;
                    output.WriteLine($"f{f,3} facing {unit.Orientation * 180 / MathF.PI,6:0} going {heading * 180 / MathF.PI,6:0} step {new Vector2(step.X, step.Y).Length(),5:0.00} walking {unit.AI.IsWalking} state {unit.AI.State}");
                }
            }
            game.DestroyObject(unit);
        }
    }
}
