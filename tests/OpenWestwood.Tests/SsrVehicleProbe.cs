using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.SoleSurvivor;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Probe (SSR_VEHICLES=1, SSR_OUT=file): every SSR map's objects that belong to nobody in the match and move
/// (or look like vehicles), and whether a soldier could drive each, and why not.</summary>
public class SsrVehicleProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListMapVehicles()
    {
        if (Environment.GetEnvironmentVariable("SSR_VEHICLES") != "1") return;
        var c = Client.GameContent.Load();
        var cache = Client.Shell.ShellScreen.BuildMapCache(c);
        var only = Environment.GetEnvironmentVariable("SSR_MAP");
        var maps = cache.Maps.Where(m => m.IsMultiplayer && m.NumPlayers >= 2 && m.NameLookupTag.Length > 0)
            .Where(m => only == null || m.FileName.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        var lines = new List<string>();
        var templates = new Dictionary<string, string>();
        foreach (var m in maps)
        {
            try
            {
                var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 1, MapPlayers = m.NumPlayers, UnitType = SsrCategory.Infantry, Map = m.FileName, MapInfo = m };
                var (game, _, match) = setup.Build(c);
                var others = game.Objects.Where(o => match.Of(o.Owner) == null && !o.IsKindOf(Kind.Structure)
                    && (o.AI != null || o.IsKindOf("VEHICLE") || o.IsKindOf("CAN_BE_REPULSED") || o.Template.Name.Contains("Car", StringComparison.OrdinalIgnoreCase)
                        || o.Template.Name.Contains("Truck", StringComparison.OrdinalIgnoreCase) || o.Template.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))).ToList();
                int drivable = others.Count(match.IsDrivable);
                lines.Add($"{m.FileName}: {others.Count} candidates, {drivable} drivable");
                foreach (var g in others.GroupBy(o => o.Template.Name))
                {
                    var o = g.First();
                    string why = match.IsDrivable(o) ? "OK" : string.Join(",", new[]
                    {
                        o.AI == null ? "noAI" : null, !o.IsKindOf("VEHICLE") ? "notVEHICLE" : null, o.IsKindOf(Kind.Aircraft) ? "aircraft" : null,
                        o.IsContained ? "contained" : null, o.IsEffectivelyDead ? "dead" : null,
                    }.Where(s => s != null));
                    string kinds = string.Join(" ", o.Template.KindOf.Take(12));
                    string line = $"  {g.Key} x{g.Count()} owner={o.Owner.Name} {why} [{kinds}]";
                    lines.Add(line);
                    templates.TryAdd(g.Key, $"{why} owner={o.Owner.Name} first={m.FileName} [{kinds}]");
                }
            }
            catch (Exception e) { lines.Add($"{m.FileName}: FAILED {e.GetType().Name} {e.Message}"); }
        }
        lines.Add("");
        lines.Add("=== templates ===");
        lines.AddRange(templates.OrderBy(t => t.Key).Select(t => $"{t.Key}: {t.Value}"));
        var outFile = Environment.GetEnvironmentVariable("SSR_OUT");
        if (outFile != null) File.WriteAllLines(outFile, lines);
        else foreach (var l in lines) output.WriteLine(l);
    }
}
