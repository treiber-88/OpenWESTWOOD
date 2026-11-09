using OpenWestwood.Client.RedAlert2;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

public class FlameGuyProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("FLAME_PROBE") == null || !content.Available) return;
        foreach (var art in new[] { Ra2Art.Get(), Ra2Art.GetTiberianSun() })
        {
            if (art != null && Environment.GetEnvironmentVariable("FLAME_PROBE") == "radar")
            {
                foreach (string b in art.Rules.TypeList("BuildingTypes"))
                    if (art.Rules.Bool(b, "Radar") || art.Rules.Bool(b, "SpySat"))
                        output.WriteLine($"RADAR TS={art.IsTiberianSun} {b} radar={art.Rules.Bool(b, "Radar")} owner={art.Rules.Get(b, "Owner")}");
                continue;
            }
            if (art == null) { output.WriteLine("no art"); continue; }
            foreach (var (k, v) in art.Rules.Section("AudioVisual"))
                if (k.Contains("lec", StringComparison.OrdinalIgnoreCase) || k.Contains("Infantry", StringComparison.OrdinalIgnoreCase) || v.Contains("ELECT", StringComparison.OrdinalIgnoreCase))
                    output.WriteLine($"  AV {k}={v}");
            foreach (string an in new[] { "ELECTRO", "ELECTDOG", "TESLADIE" })
                output.WriteLine($"  anim {an}: shape {art.Shape(an)?.FrameCount} art [{string.Join(" ", art.Art.Section(an).Select(e => e.Key + "=" + e.Value))}]");
            string name = art.Rules.Get("AudioVisual", "FlamingInfantry"); output.WriteLine($"  electrocuted={art.Rules.Get("AudioVisual", "InfantryElectrocuted")} shape={art.Shape(art.Rules.Get("AudioVisual", "InfantryElectrocuted"))?.FrameCount}");
            output.WriteLine($"TS={art.IsTiberianSun} FlamingInfantry={name}");
            foreach (var (k, v) in art.Art.Section(name.Length > 0 ? name : "FLAMEGUY"))
                output.WriteLine($"  art {k}={v}");
            var shape = art.Shape((name.Length > 0 ? name : "FLAMEGUY") + ".shp") ?? art.Shape(name.Length > 0 ? name : "FLAMEGUY");
            output.WriteLine($"  shape frames {shape?.FrameCount} size {shape?.Width}x{shape?.Height}");
            foreach (var key in new[] { "E1", "E2" })
            {
                string seq = art.Art.Get(key, "Sequence");
                foreach (var (k, v) in art.Art.Section(seq))
                    if (k.StartsWith("Die", StringComparison.OrdinalIgnoreCase)) output.WriteLine($"  {key} {seq} {k}={v}");
            }
            foreach (var key in new[] { "InfDeath" })
                output.WriteLine($"  Napalm {art.Rules.Get("FlameThrower", "InfDeath")} / Fire {art.Rules.Get("Fire", "InfDeath")}");
        }
    }
}
