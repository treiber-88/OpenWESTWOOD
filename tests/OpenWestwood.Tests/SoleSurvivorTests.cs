using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.SoleSurvivor;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Sole Survivor Remix: each mode played a few minutes by computer players (the person's slot made a computer
/// too): units come in, fight, earn credits, come back after dying, and Zone Defense / Capture the Flag score.</summary>
public class SoleSurvivorTests(ITestOutputHelper output)
{
    static Client.GameContent? content;
    static Client.GameContent? Content()
    {
        try { return content ??= Client.GameContent.Load(); }
        catch (DirectoryNotFoundException) { return null; }
    }

    [Theory]
    [InlineData(SsrMode.Freeplay)]
    [InlineData(SsrMode.ZoneDefense)]
    [InlineData(SsrMode.CaptureTheFlag)]
    [InlineData(SsrMode.Classic)]
    public void ComputersPlayTheMode(SsrMode mode)
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = mode, Computers = 3, MapPlayers = 4 };
        var (game, _, match) = setup.Build(c);
        // The person's slot played by the computer too (the test has no one at the keys).
        var person = match.Players.First(p => !p.IsComputer);
        int index = match.Players.IndexOf(person);
        match.Players[index] = new SsrPlayer { Player = person.Player, Team = person.Team, IsComputer = true, Faction = person.Faction, RespawnFrame = game.Frame };
        var catalog = SsrCatalog.Factions(c.Rules, game.Templates);
        output.WriteLine($"{catalog.Count} factions, {catalog.Sum(f => f.Units.Count)} units; players: " +
            string.Join(", ", match.Players.Select(p => $"{p.Player.Name} {p.Faction?.Name} team {p.Team}")));
        int minutes = 5;
        for (int f = 0; f < minutes * 60 * LogicTime.FramesPerSecond && match.WinningTeam == null; f++)
        {
            game.IsGameOver = false;
            game.Update();
        }
        foreach (var p in match.Players)
            output.WriteLine($"{p.Player.Name}: unit {p.Unit} kills {p.Kills} deaths {p.Deaths} credits {p.Credits} upgrades {p.WeaponLevel}/{p.ArmorLevel}/{p.SpeedLevel}");
        output.WriteLine($"team scores {match.TeamScores[0]} / {match.TeamScores[1]}, winner {match.WinningTeam}");
        Assert.All(match.Players, p => Assert.NotNull(p.Unit));
        if (mode != SsrMode.Classic)
            Assert.True(match.Players.Sum(p => p.Credits) > 0, "kills paid credits");
        output.WriteLine($"flag pick-ups {match.FlagTakes}");
        if (mode == SsrMode.ZoneDefense)
            Assert.True(match.TeamScores.Sum() > 0, "zone points scored");
        if (mode == SsrMode.CaptureTheFlag)
            Assert.True(match.FlagTakes > 0, "a flag was taken");
    }

    /// <summary>The keys drive tank-style whichever way the unit faces: W forward along its facing, S back (still facing
    /// the same way), A turns left (anticlockwise from above), D right. Tried on a vehicle and a soldier of each game's
    /// first faction.</summary>
    [Theory]
    [InlineData("FactionAmerica")]
    [InlineData("FactionTDGDI")]
    [InlineData("FactionRAEngland")]
    [InlineData("FactionTSNod")]
    [InlineData("FactionRA2Russians")]
    [InlineData("FactionD2KAtreides")]
    [InlineData("FactionEMAtreides")]
    [InlineData("FactionKWGDI")]
    public void KeysDriveTankStyle(string faction)
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var catalog = SsrCatalog.Factions(c.Rules, new Logic.Templates.TemplateStore(c.Rules));
        var side = catalog.FirstOrDefault(f => f.Name == faction);
        Assert.NotNull(side);
        foreach (var category in new[] { SsrCategory.Vehicle, SsrCategory.Infantry })
        {
            if (side!.Units.FirstOrDefault(u => u.Category == category) is not { } choice)
                continue;
            var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 0, MapPlayers = 2, Faction = faction };
            var (game, _, match) = setup.Build(c);
            var me = match.Players.First(p => !p.IsComputer);
            var unit = match.Spawn(me, choice.Template);
            Assert.NotNull(unit);
            void Hold(float turn, float throttle, int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    game.Commands.Enqueue(new SsrInputCommand(me.Player.Index, turn, throttle, unit!.Position, 0, false, false));
                    game.IsGameOver = false;
                    game.Update();
                }
            }
            // Forward (from a random spawn point: if a cliff or the water is just ahead, face another way and try again).
            float facing = 0, along = 0;
            var moved = System.Numerics.Vector3.Zero;
            for (int heading = 0; heading < 8 && along <= 10; heading++)
            {
                unit!.SetOrientation(heading * MathF.PI / 4);
                Hold(0, 0, 15);
                facing = unit.Orientation;
                var start = unit.Position;
                Hold(0, 1, 45);
                var forward = MathUtil.Forward(facing);
                moved = unit.Position - start;
                along = moved.X * forward.X + moved.Y * forward.Y;
            }
            output.WriteLine($"{faction} {choice.Template}: W moved {along:0.0} along its facing ({moved.Length():0.0} in all)");
            Assert.True(along > 10 && along > 0.8f * new System.Numerics.Vector2(moved.X, moved.Y).Length(), "W drives forward along the facing");
            Hold(0, 0, 30);
            facing = unit!.Orientation;
            var from = unit.Position;
            Hold(0, -1, 45);
            var ahead = MathUtil.Forward(facing);
            moved = unit.Position - from;
            along = moved.X * ahead.X + moved.Y * ahead.Y;
            output.WriteLine($"  S moved {along:0.0} along its facing, facing changed {MathUtil.NormalizeAngle(unit.Orientation - facing):0.00}");
            Assert.True(along < -5, "S backs up");
            Assert.True(MathF.Abs(MathUtil.NormalizeAngle(unit.Orientation - facing)) < 0.05f, "S keeps the facing");
            Hold(0, 0, 30);
            facing = unit.Orientation;
            Hold(1, 0, 10);
            float left = MathUtil.NormalizeAngle(unit.Orientation - facing);
            facing = unit.Orientation;
            Hold(-1, 0, 10);
            float right = MathUtil.NormalizeAngle(unit.Orientation - facing);
            output.WriteLine($"  A turned {left:0.00}, D turned {right:0.00}");
            Assert.True(left > 0.2f, "A turns left");
            Assert.True(right < -0.2f, "D turns right");
        }
    }
}
