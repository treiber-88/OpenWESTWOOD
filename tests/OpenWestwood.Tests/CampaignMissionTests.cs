using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Every Zero Hour campaign mission loads and plays for a while without failing (as the mission menu starts them).</summary>
public class CampaignMissionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    public static IEnumerable<object[]> Missions()
    {
        foreach (string side in new[] { "USA", "GLA", "CHI" })
            for (int i = 1; i <= 5; i++)
                yield return [$"Maps/MD_{side}0{i}/MD_{side}0{i}.map"];
    }

    [Theory]
    [MemberData(nameof(Missions))]
    public void MissionPlays(string map)
    {
        if (!content.Available || !content.FileSystem!.Exists(map))
            return;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MISSION_SECONDS"), out int s) ? s : 60;
        var mapFile = MapFile.Read(content.FileSystem.ReadAllBytes(map));
        var game = GameSetup.FromMap(mapFile, content.Rules!, 1, difficulty: Difficulty.Normal, models: content.Models,
            readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        for (int i = 0; i < seconds * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"{map}: {game.Objects.Count()} objects after {seconds}s");
    }
}

/// <summary>Probe (MISSION_TRACE=Maps/...map): the client-side script actions a mission sends, with their frames.</summary>
public class MissionTraceProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Trace()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("MISSION_TRACE") is not { Length: > 0 } map)
            return;
        var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes(map));
        var game = GameSetup.FromMap(mapFile, content.Rules!, 1, difficulty: Difficulty.Normal, models: content.Models,
            readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Events.OnScriptClientAction += a =>
        {
            if (!a.Name.StartsWith("CAMERA_MOD") && !a.Name.StartsWith("MOVE_CAMERA") && a.Name != "SCREEN_SHAKE")
                output.WriteLine($"{game.Frame,6} {a.Name} {string.Join(" ", a.Parameters.Select(p => p.ToString()))}");
        };
        game.Events.OnMessage += (text, who) => output.WriteLine($"{game.Frame,6} MESSAGE {text} to {who?.Name}");
        // The scripts that end the cinematic, and what they wait for.
        foreach (var side in game.Scripts!.Sides)
            foreach (var script in side?.AllScripts ?? [])
                if (script.Actions.Concat(script.FalseActions).Any(a => a.Name is "ENABLE_INPUT" or "CAMERA_LETTERBOX_END" or "CALL_SUBROUTINE" or "ENABLE_SCRIPT")
                    || Environment.GetEnvironmentVariable("MISSION_DUMP") == "all")
                    output.WriteLine($"SCRIPT {script.Name} active={script.IsActive} oneshot={script.IsOneShot} sub={script.IsSubroutine}\n   IF {string.Join(" OR ", script.OrConditions.Select(o => string.Join(" AND ", o)))}\n   DO {string.Join("; ", script.Actions)}");
        OpenWestwood.Logic.Scripting.ScriptEngine.TraceScripts = Environment.GetEnvironmentVariable("MISSION_ACTIONS") == "1";
        var oldOutput = OpenWestwood.Logic.Log.Output;
        OpenWestwood.Logic.Log.Output = line => output.WriteLine(line);
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MISSION_SECONDS"), out int s) ? s : 180;
        output.WriteLine($"BOUNDARIES {string.Join(" ", mapFile.HeightMap.Boundaries.Select(b => $"{b.Item1 * 10}x{b.Item2 * 10}"))} active={game.Terrain.ActiveBoundary} world={game.Terrain.WorldWidth}x{game.Terrain.WorldHeight}");
        foreach (var a in game.Players.All)
            output.WriteLine($"PLAYER {a.Name} side={a.Side} -> " + string.Join(", ", game.Players.All.Where(b => b != a).Select(b => $"{b.Name}:{a.RelationshipTo(b)}")));
        foreach (var side in mapFile.Sides.Sides)
            output.WriteLine($"SIDE {string.Join(" ", side.Properties.Select(kv => kv.Key + "=" + kv.Value))}");
        foreach (var team in mapFile.Sides.Teams)
            if (Environment.GetEnvironmentVariable("MISSION_TEAMS") is { Length: > 0 } owner && team.TryGetValue("teamOwner", out var o2) && o2?.ToString() == owner)
                output.WriteLine($"TEAM {string.Join(" ", team.Where(kv => !kv.Key.StartsWith("teamUnit")).Select(kv => kv.Key + "=" + kv.Value))}");
        if (Environment.GetEnvironmentVariable("MISSION_OBJECTS") is { Length: > 0 } objectFilter)
            foreach (var mo in mapFile.Objects.Where(m => m.TemplateName.Contains(objectFilter) || m.Properties.Values.Any(v => v?.ToString()?.Contains(objectFilter) == true)))
                output.WriteLine($"MAPOBJ {mo.TemplateName} at={mo.Position:0} {string.Join(" ", mo.Properties.Select(kv => kv.Key + "=" + kv.Value))}");
        var watch = (Environment.GetEnvironmentVariable("MISSION_WATCH") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < seconds * LogicTime.FramesPerSecond && !game.IsGameOver; i++)
        {
            game.Update();
            // Experiment (MISSION_PASSIVE=player@frame): that player's units turn passive at that frame.
            if (Environment.GetEnvironmentVariable("MISSION_PASSIVE") is { } passive && passive.Split('@') is [var who, var when] && game.Frame == int.Parse(when))
                foreach (var po in game.Objects.Where(x => x.Owner.Name == who && x.AI != null))
                    po.AI!.Attitude = OpenWestwood.Logic.Players.Attitude.Passive;
            if (watch.Length > 0 && game.Frame % (int.TryParse(Environment.GetEnvironmentVariable("MISSION_WATCH_EVERY"), out int every) ? every : 30) == 0)
                foreach (string name in watch)
                    if (name.StartsWith('@'))
                    {
                        foreach (var po in game.Objects.Where(x => x.Owner.Name == name[1..] && x.AI != null && !x.IsEffectivelyDead))
                            output.WriteLine($"{game.Frame,6} OWNED {po.Template.Name} team={po.Team?.Name} at={po.Position:0} ai={po.AI!.State} victim={po.AI.CurrentVictim?.Name ?? po.AI.CurrentVictim?.Template.Name} contained={po.ContainedById}");
                    }
                    else if (name.StartsWith('#'))
                        output.WriteLine($"{game.Frame,6} COUNT {name}: {game.Objects.Count(o => o.Template.Name == name[1..])} {string.Join(" ", game.Objects.Where(o => o.Template.Name == name[1..]).Select(o => $"{o.Owner.Name}@{o.Position:0}"))}");
                    else if (game.FindObjectByName(name) is { } o)
                    {
                        var weapon = o.Weapons?.Current;
                        output.WriteLine($"{game.Frame,6} WATCH {name}: {o.Template.Name} owner={o.Owner.Name} at={o.Position:0} hp={o.Health:0}/{o.MaxHealth:0} ai={o.AI?.State} victim={o.AI?.CurrentVictim?.Name} weapon={weapon?.Template.Name} range={weapon?.Template.AttackRange} dead={o.IsEffectivelyDead} stealthed={o.HasStatus("STEALTHED")} detected={o.HasStatus("DETECTED")} powers={string.Join("/", o.FindModules<OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerModule>().Select(m => $"{m.Power?.Name}:{m.PercentReady:0.##}:{(m.IsPaused ? "P" : "")}"))} lastHitBy={(o.Body is { } b ? game.FindObject(b.LastDamageSourceId)?.Template.Name + "@" + game.FindObject(b.LastDamageSourceId)?.Owner.Name : "")}");
                    }
                    else
                        output.WriteLine($"{game.Frame,6} WATCH {name}: gone");
        }
        OpenWestwood.Logic.Scripting.ScriptEngine.TraceScripts = false;
        OpenWestwood.Logic.Log.Output = oldOutput;
        output.WriteLine($"end frame {game.Frame}, game over {game.IsGameOver}");
    }
}
