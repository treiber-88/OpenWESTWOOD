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
        // Everyone kept to the match's unit type (vehicles here) or aircraft.
        foreach (var p in match.Players.Where(p => p.Unit != null))
        {
            var kind = p.Faction!.Units.First(u => u.Template == p.Unit).Category;
            Assert.True(kind is SsrCategory.Vehicle or SsrCategory.Aircraft, $"{p.Player.Name} played {p.Unit} ({kind})");
        }
    }

    /// <summary>Zone Defense: three zones a team, out from the bases, and nobody comes in on one.</summary>
    [Fact]
    public void ZonesAreSpreadAndSpawnsKeepClear()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.ZoneDefense, Computers = 7, MapPlayers = 2 };
        var (game, _, match) = setup.Build(c);
        Assert.Equal(6, match.Zones.Count);
        foreach (var (team, centre) in match.Zones)
        {
            output.WriteLine($"team {team} zone at {centre.X:0},{centre.Y:0}: {MathUtil.Distance2D(centre, match.TeamBases[team]):0} from its base");
            Assert.True(MathUtil.Distance2D(centre, match.TeamBases[team]) > match.Settings.ZoneRadius + 150, "zone well out from its base");
        }
        var person = match.Players.First(p => !p.IsComputer);
        int index = match.Players.IndexOf(person);
        match.Players[index] = new SsrPlayer { Player = person.Player, Team = person.Team, IsComputer = true, Faction = person.Faction, RespawnFrame = game.Frame };
        var spawnedAt = new Dictionary<uint, System.Numerics.Vector3>();
        for (int f = 0; f < 90 * LogicTime.FramesPerSecond; f++)
        {
            game.IsGameOver = false;
            game.Update();
            foreach (var p in match.Players)
                if (match.UnitOf(p) is { } unit && !spawnedAt.ContainsKey(unit.Id))
                    spawnedAt[unit.Id] = unit.Position;
        }
        output.WriteLine($"{spawnedAt.Count} units came in; scores {match.TeamScores[0]} / {match.TeamScores[1]}");
        foreach (var at in spawnedAt.Values)
            Assert.True(match.Zones.All(z => MathUtil.Distance2D(z.Center, at) > match.Settings.ZoneRadius), $"came in on a zone at {at.X:0},{at.Y:0}");
    }

    /// <summary>A person can change unit any time: the new one stands where the old one did, with its share of health, and
    /// it is no death. The first unit comes in at the chosen start point.</summary>
    [Fact]
    public void SwitchingUnitsAndChosenStart()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 1, MapPlayers = 2, Start = 1, Faction = "FactionTDGDI" };
        var (game, _, match) = setup.Build(c);
        var me = match.Players.First(p => !p.IsComputer);
        var vehicles = me.Faction!.Units.Where(u => u.Category == SsrCategory.Vehicle).ToList();
        var first = match.Spawn(me, vehicles[0].Template)!;
        Assert.NotNull(me.Player.StartPosition);
        output.WriteLine($"came in {MathUtil.Distance2D(first.Position, me.Player.StartPosition!.Value):0} from start 2");
        Assert.True(MathUtil.Distance2D(first.Position, me.Player.StartPosition!.Value) < 200, "first unit at the chosen start");
        for (int f = 0; f < 30; f++)
            game.Update();
        first.Body!.SetHealth(first.MaxHealth / 2);
        var where = first.Position;
        var second = match.Switch(me, vehicles[^1].Template)!;
        for (int f = 0; f < 5; f++)
            game.Update();
        output.WriteLine($"switched {vehicles[0].Template} -> {vehicles[^1].Template}: {MathUtil.Distance2D(second.Position, where):0} away, health {second.Health / second.MaxHealth:0.00}");
        Assert.True(first.IsEffectivelyDead || game.FindObject(first.Id) == null, "old unit gone");
        Assert.Equal(second.Id, match.UnitOf(me)!.Id);
        Assert.True(MathUtil.Distance2D(second.Position, where) < 30, "new unit where the old one stood");
        Assert.InRange(second.Health / second.MaxHealth, 0.45f, 0.55f);
        Assert.Equal(0, me.Deaths);
    }

    /// <summary>
    /// Cover: trees between a shooter and its enemy take the shots (a rifle's instant ones and a tank's shells), the enemy
    /// behind them none; with the trees gone the enemy is hit.
    /// </summary>
    [Theory]
    [InlineData("AmericaInfantryRanger")]
    [InlineData("AmericaTankCrusader")]
    public void ObstaclesStopShots(string shooterTemplate)
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 1, MapPlayers = 2 };
        var (game, _, match) = setup.Build(c);
        var me = match.Players.First(p => !p.IsComputer);
        var them = match.Players.First(p => p.IsComputer);
        them.RespawnFrame = int.MaxValue; // its own avatar stays out; the test places its unit
        var shooter = match.Spawn(me, shooterTemplate)!;
        var at = shooter.Position;
        float ground(float x, float y) => game.Terrain.GroundHeight(x, y);
        var enemyAt = new System.Numerics.Vector3(at.X + 120, at.Y, 0);
        enemyAt.Z = ground(enemyAt.X, enemyAt.Y);
        var enemy = game.CreateObject("AmericaTankCrusader", them.Player, enemyAt, MathF.PI)!;
        // A row of trees across the way (a tank's shell leaves its barrel off to one side).
        var trees = new[] { -21f, 0f, 21f }.Select(dy =>
        {
            var treeAt = new System.Numerics.Vector3(at.X + 60, at.Y + dy, 0);
            treeAt.Z = ground(treeAt.X, treeAt.Y);
            return game.CreateObject("TD_Terrain_T01", game.Players.Neutral, treeAt)!;
        }).ToList();
        float TreeHealth() => trees.Sum(t => t.IsEffectivelyDead ? 0 : t.Health);
        shooter.SetOrientation(0);
        var weapon = shooter.Weapons.Slots[0]!;
        float enemyHealth = enemy.Health, treeHealth = TreeHealth();

        int Shoot(int frames)
        {
            int shots = 0;
            for (int f = 0; f < frames; f++)
            {
                if (weapon.IsReady && weapon.FireAt(enemy))
                    shots++;
                game.IsGameOver = false;
                game.Update();
            }
            return shots;
        }

        int fired = Shoot(90);
        output.WriteLine($"{shooterTemplate}: {fired} shots with the tree between: trees {treeHealth:0} -> {TreeHealth():0}, enemy {enemyHealth:0} -> {enemy.Health:0}");
        Assert.True(fired > 0);
        Assert.True(TreeHealth() < treeHealth, "the trees took the shots");
        Assert.Equal(enemyHealth, enemy.Health);
        Assert.NotNull(Logic.Combat.LineOfFire.FirstObstacle(game, Logic.Combat.LineOfFire.Muzzle(shooter), Logic.Combat.LineOfFire.AimPoint(enemy), shooter, enemy));

        foreach (var tree in trees.Where(t => !t.IsDestroyed))
            game.DestroyObject(tree);
        game.Update();
        Shoot(90);
        output.WriteLine($"without the tree: enemy {enemyHealth:0} -> {enemy.Health:0}");
        Assert.True(enemy.Health < enemyHealth || enemy.IsEffectivelyDead, "hit once the tree is gone");
    }

    /// <summary>Team Battle: the person's chosen number of computers with them, the chosen number against them, allies are
    /// allies; the first team to the kill limit wins.</summary>
    [Fact]
    public void TeamBattleToKillLimit()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.TeamBattle, AlliedComputers = 2, EnemyComputers = 3, MapPlayers = 2, KillLimit = 4 };
        var (game, _, match) = setup.Build(c);
        Assert.Equal(3, match.Players.Count(p => p.Team == 0));
        Assert.Equal(3, match.Players.Count(p => p.Team == 1));
        var person = match.Players.First(p => !p.IsComputer);
        Assert.Equal(0, person.Team);
        foreach (var p in match.Players.Where(p => p != person))
            Assert.Equal(p.Team == 0 ? Logic.Players.Relationship.Allies : Logic.Players.Relationship.Enemies, person.Player.RelationshipTo(p.Player));
        int index = match.Players.IndexOf(person);
        match.Players[index] = new SsrPlayer { Player = person.Player, Team = person.Team, IsComputer = true, Faction = person.Faction, RespawnFrame = game.Frame };
        for (int f = 0; f < 6 * 60 * LogicTime.FramesPerSecond && match.WinningTeam == null; f++)
        {
            game.IsGameOver = false;
            game.Update();
        }
        output.WriteLine($"team kills {match.KillsOf(0)} / {match.KillsOf(1)}, winner {match.WinningTeam} at {game.Frame / LogicTime.FramesPerSecond} s");
        Assert.NotNull(match.WinningTeam);
        Assert.True(match.KillsOf(match.WinningTeam!.Value) >= 4);
    }

    /// <summary>A time limit: when it runs out, the player with the most kills wins.</summary>
    [Fact]
    public void TimeLimitMostKillsWins()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 5, MapPlayers = 2, TimeLimitMinutes = 1 };
        var (game, _, match) = setup.Build(c);
        Assert.NotNull(match.EndFrame);
        int f = 0;
        for (; f < 2 * 60 * LogicTime.FramesPerSecond && match.WinningTeam == null; f++)
        {
            game.IsGameOver = false;
            game.Update();
        }
        output.WriteLine($"ended at {f / LogicTime.FramesPerSecond} s; kills {string.Join(", ", match.Players.Select(p => p.Kills))}; winner side {match.WinningTeam}");
        Assert.NotNull(match.WinningTeam);
        Assert.InRange(f, 60 * LogicTime.FramesPerSecond - 2, 60 * LogicTime.FramesPerSecond + 2);
        Assert.Equal(match.Players.Max(p => p.Kills), match.KillsOf(match.WinningTeam!.Value));
    }

    /// <summary>A soldier firing on the move faces its target while still walking its own way; standing, it turns to it.</summary>
    [Fact]
    public void FiringOnTheMoveFacesTheTarget()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 1, MapPlayers = 2 };
        var (game, _, match) = setup.Build(c);
        var me = match.Players.First(p => !p.IsComputer);
        match.Players.First(p => p.IsComputer).RespawnFrame = int.MaxValue;
        var mine = match.Spawn(me, "AmericaInfantryRanger")!;
        // Find a heading with open ground ahead.
        mine.SetOrientation(0);
        var start = mine.Position;
        var targetAt = start + new System.Numerics.Vector3(0, 150, 0);
        targetAt.Z = game.Terrain.GroundHeight(targetAt.X, targetAt.Y);
        var enemy = game.CreateObject("AmericaTankCrusader", match.Players.First(p => p.IsComputer).Player, targetAt)!;
        void Frames(int n) { for (int i = 0; i < n; i++) { game.IsGameOver = false; game.Update(); } }
        // Walking east (its facing), firing at the tank to the north.
        me.Move = new System.Numerics.Vector2(0, 1);
        me.AimTarget = enemy.Id;
        me.FirePrimary = true;
        Frames(45);
        var moved = mine.Position - start;
        float facing = mine.Orientation, way = mine.AI!.MoveFacing;
        output.WriteLine($"moved {moved.X:0.0},{moved.Y:0.0}; body faces {facing:0.00} rad, walks along {way:0.00} rad");
        Assert.True(MathF.Abs(MathUtil.AngleDifference(facing, MathF.PI / 2)) < 0.4f, "body faces the target (north)");
        Assert.True(MathF.Abs(MathUtil.AngleDifference(way, 0)) < 0.2f, "still walks east");
        Assert.True(moved.X > 20 && MathF.Abs(moved.Y) < moved.X * 0.4f, "moved east");
        // Standing and firing: it turns to the target for good.
        me.Move = System.Numerics.Vector2.Zero;
        Frames(20);
        me.FirePrimary = false;
        Frames(5);
        output.WriteLine($"standing: faces {mine.Orientation:0.00}");
        Assert.True(MathF.Abs(MathUtil.AngleDifference(mine.Orientation, MathF.Atan2(enemy.Position.Y - mine.Position.Y, enemy.Position.X - mine.Position.X))) < 0.3f);
    }

    /// <summary>No autofire: a person's unit next to an enemy that is shooting it doesn't shoot back until fire is pressed.</summary>
    [Fact]
    public void UnitsDontFireByThemselves()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 1, MapPlayers = 2 };
        var (game, _, match) = setup.Build(c);
        var me = match.Players.First(p => !p.IsComputer);
        var them = match.Players.First(p => p.IsComputer);
        them.RespawnFrame = int.MaxValue;
        var mine = match.Spawn(me, "AmericaTankCrusader")!;
        var at = mine.Position with { X = mine.Position.X + 90 };
        at.Z = game.Terrain.GroundHeight(at.X, at.Y);
        var enemy = game.CreateObject("AmericaTankCrusader", them.Player, at, MathF.PI)!;
        float enemyHealth = enemy.Health, myHealth = mine.Health;
        for (int f = 0; f < 120; f++)
        {
            game.IsGameOver = false;
            game.Update();
        }
        output.WriteLine($"after 4 s: mine {myHealth:0} -> {mine.Health:0}, enemy {enemyHealth:0} -> {enemy.Health:0}");
        Assert.True(mine.Health < myHealth, "the enemy (an ordinary unit) shot");
        Assert.Equal(enemyHealth, enemy.Health);
        // Fire pressed: it shoots.
        me.AimTarget = enemy.Id;
        me.FirePrimary = true;
        for (int f = 0; f < 60; f++)
        {
            game.IsGameOver = false;
            game.Update();
        }
        Assert.True(enemy.Health < enemyHealth || enemy.IsEffectivelyDead, "fires when told to");
    }

    /// <summary>An infantry match: everyone plays infantry, and nobody takes an aircraft (unlike vehicle and naval matches).</summary>
    [Fact]
    public void InfantryMatchHasNoAircraft()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 12, MapPlayers = 2, UnitType = SsrCategory.Infantry };
        var (game, _, match) = setup.Build(c);
        Assert.False(match.Settings.KindAllowed(SsrCategory.Aircraft));
        Assert.True(new SsrSettings { UnitType = SsrCategory.Vehicle }.KindAllowed(SsrCategory.Aircraft));
        var played = new HashSet<string>();
        for (int f = 0; f < 3 * 60 * LogicTime.FramesPerSecond; f++)
        {
            game.IsGameOver = false;
            game.Update();
            foreach (var p in match.Players.Where(p => p.Unit != null))
                played.Add($"{p.Faction!.Name}|{p.Unit}");
        }
        output.WriteLine(string.Join(", ", played));
        foreach (var entry in played)
        {
            var (factionName, unit) = (entry.Split('|')[0], entry.Split('|')[1]);
            var kind = match.Players.First(p => p.Faction!.Name == factionName).Faction!.Units.First(u => u.Template == unit).Category;
            Assert.Equal(SsrCategory.Infantry, kind);
        }
    }

    /// <summary>Eighty computer players: the match runs (a minute) with them all in.</summary>
    [Fact]
    public void EightyComputers()
    {
        if (Content() is not { } c || !File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 80, MapPlayers = 2 };
        var (game, _, match) = setup.Build(c);
        Assert.Equal(81, match.Players.Count);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int most = 0;
        for (int f = 0; f < 60 * LogicTime.FramesPerSecond; f++)
        {
            game.IsGameOver = false;
            game.Update();
            most = Math.Max(most, match.Players.Count(p => match.UnitOf(p) != null));
        }
        output.WriteLine($"most units in at once {most}; a minute took {clock.Elapsed.TotalSeconds:0.0}s; kills {match.Players.Sum(p => p.Kills)}");
        Assert.True(most >= 75, "the computers all came in");
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
