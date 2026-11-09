using OpenWestwood.Formats.Sage;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

public class KwArtProbe(ITestOutputHelper output)
{
    [Fact]
    public void MeshHeaders()
    {
        string want = Environment.GetEnvironmentVariable("KW_PROBE") ?? "";
        string big = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath", "Core", "1.0", "StaticStream.big");
        if (want.Length == 0 || !File.Exists(big)) return;
        var p = new SageArtPackage(big);
        var s = p.Stream;
        foreach (var a in s.Assets.Where(a => a.TypeName == "W3DMesh" && a.InstanceName.StartsWith(want, StringComparison.OrdinalIgnoreCase)).Take(30))
        {
            var d = s.Data(a).ToArray();
            uint U(int o) => BitConverter.ToUInt32(d, o);
            int vb = (int)U(4);
            if (U(vb + 20) > 0) output.WriteLine("  palette: " + string.Join(" ", Enumerable.Range(0, Math.Min(48, d.Length - (int)U(vb + 24))).Select(k => d[(int)U(vb + 24) + k].ToString("x2"))) + " decl: " + string.Join(" ", Enumerable.Range(0, (int)U(vb + 12)).Select(k => d[(int)U(vb + 16) + k].ToString("x2"))));
            output.WriteLine($"{a.InstanceName}: +0 {U(0):x8} +8 {U(8):x8} +54 {U(0x54):x8} +58 {U(0x58):x8} +5c {U(0x5c):x8} shader {System.Text.Encoding.ASCII.GetString(d, (int)U(0x40), (int)U(0x3c))} vb+20 {U(vb + 20)} vb+24 {U(vb + 24):x} size {d.Length}");
        }
        output.WriteLine("anims: " + string.Join(" ", s.Assets.Where(a => a.TypeName == "W3DAnimation" && a.InstanceName.StartsWith(want.Split('.')[0], StringComparison.OrdinalIgnoreCase)).Select(a => a.InstanceName)));
        var hashes = new Dictionary<uint, int>();
        foreach (var a in s.Assets.Where(a => a.TypeName == "W3DAnimation"))
        {
            var d = s.Data(a).ToArray();
            int count = (int)BitConverter.ToUInt32(d, 16), list = (int)BitConverter.ToUInt32(d, 20);
            for (int i = 0; i < count; i++)
            {
                uint h = BitConverter.ToUInt32(d, (int)BitConverter.ToUInt32(d, list + i * 4));
                hashes[h] = hashes.GetValueOrDefault(h) + 1;
            }
        }
        output.WriteLine("channel kinds: " + string.Join(" ", hashes.Select(kv => $"{kv.Key:x8}x{kv.Value}")));
        var skins = s.Assets.Where(a => a.TypeName == "W3DContainer" && a.InstanceName.Contains("_SKN")).Select(a => a.InstanceName).Take(400);
        output.WriteLine("SKN: " + string.Join(" ", skins));
    }
}
