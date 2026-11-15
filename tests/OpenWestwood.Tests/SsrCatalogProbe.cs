using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.SoleSurvivor;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Probe (SSR_CATALOG=1): every SSR faction's units, with repeated names and self-position (suicide) weapons marked.</summary>
public class SsrCatalogProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("SSR_CATALOG") != "1") return;
        var c = Client.GameContent.Load();
        var templates = new Logic.Templates.TemplateStore(c.Rules);
        var catalog = new Logic.Templates.RulesCatalog(c.Rules);
        var lines = new List<string>();
        foreach (var f in SsrCatalog.Factions(c.Rules, templates))
        {
            lines.Add($"== {f.Name} ({f.Game}) {f.Units.Count}");
            var names = f.Units.GroupBy(u => u.ButtonImage).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var u in f.Units)
            {
                var t = templates.Find(u.Template)!;
                var weapons = new List<string>();
                if (t.Rules.TryGet("WeaponSet", out var sets))
                    foreach (var set in sets.AsList())
                        if (set.TryGet("Weapon", out var ws))
                            foreach (var e in ws.AsList())
                            {
                                var words = RuleValue.Words(e.Value ?? "");
                                if (words.Length < 2) continue;
                                var w = c.Rules.Get("Weapons", words[1]);
                                bool self = w != null && w.GetString("DamageDealtAtSelfPosition").StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                                weapons.Add(words[1] + (self ? "[SELF]" : ""));
                            }
                lines.Add($"{(names.Contains(u.ButtonImage) ? "DUP " : "    ")}{u.Template} '{u.DisplayName}' {u.Category} ${u.Cost} img={u.ButtonImage} w={string.Join(",", weapons.Distinct())}");
            }
        }
        File.WriteAllLines(Environment.GetEnvironmentVariable("SSR_OUT") ?? "ssr-catalog.txt", lines);
    }
}
