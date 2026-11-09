using System.Numerics;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Art;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Infantry show a death animation when they die (standing, walking or shooting; any way of dying).</summary>
public class DeathAnimationProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void InfantryDieWithAnAnimation()
    {
        if (!content.Available) return;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        string side = Environment.GetEnvironmentVariable("DEATH_SIDE") ?? "America";
        var infantry = content.Rules!.Section("Objects").Keys
            .Where(k => content.Rules.Section("Objects")[k]!.GetString("Side") == side && content.Rules.Section("Objects")[k]!.GetString("KindOf").Contains("INFANTRY")
                && content.Rules.Section("Objects")[k]!.GetString("BuildCost").Length > 0)
            .ToList();
        var problems = new List<string>();
        foreach (string name in infantry)
            foreach (var (how, damage) in new[] { ("shot", "SMALL_ARMS"), ("blown up", "EXPLOSION"), ("burned", "FLAME"), ("lasered", "LASER") })
                foreach (string doing in new[] { "standing", "walking", "shooting" })
                {
                    var game = GameSetup.FromMap(map, content.Rules, 3,
                    [
                        new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
                        new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
                    ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
                    var mid = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
                    var soldier = game.CreateObject(name, game.Players.Find("Alice")!, mid with { Z = game.Terrain.GroundHeight(mid) });
                    if (soldier == null)
                        continue;
                    var target = game.CreateObject("ChinaTankBattleMaster", game.Players.Find("Bob")!, (mid + new Vector3(80, 0, 0)) with { Z = game.Terrain.GroundHeight(mid) })!;
                    if (doing == "walking")
                        soldier.AI?.MoveTo(mid + new Vector3(-300, 0, 0), Logic.Commands.CommandSource.Player);
                    else if (doing == "shooting")
                        soldier.AI?.AttackObject(target, Logic.Commands.CommandSource.Player);
                    for (int i = 0; i < 20; i++)
                        game.Update();
                    soldier.Damage(new Logic.Combat.DamageInfo { Amount = 10000, DamageType = damage, DeathType = damage switch { "FLAME" => "BURNED", "EXPLOSION" => "EXPLODED", "LASER" => "LASERED", _ => "NORMAL" } });
                    for (int i = 0; i < 3; i++)
                        game.Update();
                    var draw = soldier.Template.Modules.FirstOrDefault(m => m.ModuleType == "Draw" && m.ClassName.Contains("ModelDraw"));
                    if (draw == null)
                        continue;
                    var state = ModelDrawData.For(soldier.Template, draw).StateFor(soldier.ModelConditions);
                    string conditions = Flags.ModelCondition.Format(soldier.ModelConditions);
                    bool dyingState = state?.Conditions.Any(c => c.Has(Flags.ModelCondition.IndexOf("DYING"))) == true;
                    bool hasAnim = state?.Animations.Count > 0;
                    if (!dyingState || !hasAnim)
                        problems.Add($"{name} {how} while {doing}: [{conditions}] -> state '{state?.Name}' anims={state?.Animations.Count} dyingState={dyingState}");
                }
        foreach (var p in problems)
            output.WriteLine(p);
        output.WriteLine($"{infantry.Count} infantry, {problems.Count} deaths without a death animation");
    }
}
