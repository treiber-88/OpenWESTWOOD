using OpenWestwood.Formats.Sage;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

public class KwStreamList(ITestOutputHelper output)
{
    [Fact]
    public void ListType()
    {
        string type = Environment.GetEnvironmentVariable("KW_LIST") ?? "";
        string big = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath", Environment.GetEnvironmentVariable("KW_BIG") ?? Path.Combine("Core", "1.0", "StaticStream.big"));
        if (type.Length == 0 || !File.Exists(big)) return;
        var s = new SageArtPackage(big, Environment.GetEnvironmentVariable("KW_STREAM") ?? "static_common").Stream;
        output.WriteLine("types: " + string.Join(", ", s.Assets.GroupBy(a => a.TypeName).Select(g => $"{g.Key} {g.Count()}")));
        string want = Environment.GetEnvironmentVariable("KW_NAME") ?? "";
        foreach (var a in s.Assets.Where(a => a.TypeName == type && a.InstanceName.Contains(want, StringComparison.OrdinalIgnoreCase)).Take(60))
        {
            var d = s.Data(a).ToArray();
            output.WriteLine($"{a.Name} refs [{string.Join(", ", Enumerable.Range(0, a.References.Length).Select(k => s.Reference(a, k)?.Name ?? $"?{a.References[k].TypeId:x8}:{a.References[k].InstanceId:x8}"))}] {d.Length}b src {a.SourceFile}: " + new string(d.Take(600).Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray()));
        }
    }
}
