using OpenGZH.Formats.Sage;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Dev probe (env KW_AUDIO=name filter): compiled AudioFile records and their .cdata files.</summary>
public class KwAudioProbe(ITestOutputHelper output)
{
    [Fact]
    public void AudioFiles()
    {
        string want = Environment.GetEnvironmentVariable("KW_AUDIO") ?? "";
        string root = Path.Combine(OpenGZH.Client.GameContent.FindRoot(), "content", "kaneswrath");
        string big = Path.Combine(root, "Core", "1.0", "GlobalStream.big");
        if (want.Length == 0 || !File.Exists(big)) return;
        var p = new SageArtPackage(big, "global_common");
        var s = p.Stream;
        using var arc = new OpenGZH.Core.FileSystem.BigArchive(big);
        var cdata = arc.FilePaths.Where(f => f.EndsWith(".cdata")).ToList();
        foreach (var a in s.Assets.Where(a => a.TypeName == "AudioFile" && a.InstanceName.Contains(want, StringComparison.OrdinalIgnoreCase)).Take(8))
        {
            var d = s.Data(a).ToArray();
            output.WriteLine($"{a.Name} type {a.TypeId:x8}/{a.TypeHash:x8} inst {a.InstanceId:x8}/{a.InstanceHash:x8} reloc [{string.Join(",", s.Relocations(a))}] imports [{string.Join(",", s.Imports(a))}]");
            output.WriteLine("  " + string.Join(" ", d.Select(b => b.ToString("x2"))));
            var match = cdata.Where(f => f.Contains(a.InstanceId.ToString("x8")) || f.Contains(a.InstanceHash.ToString("x8"))).ToList();
            output.WriteLine("  cdata: " + string.Join(" ", match));
            foreach (var f in match.Take(1))
            {
                using var st = arc.Open(f);
                var buf = new byte[96];
                int n = st.Read(buf);
                output.WriteLine($"  {f} len {st.Length}: " + string.Join(" ", buf.Take(n).Select(b => b.ToString("x2"))));
            }
        }
        output.WriteLine("cdata e.g.: " + string.Join(" ", cdata.Take(5)));
    }
}

public class KwAudioCodecCensus(ITestOutputHelper output)
{
    [Fact]
    public void Codecs()
    {
        if (Environment.GetEnvironmentVariable("KW_CODECS") == null) return;
        string root = Path.Combine(OpenGZH.Client.GameContent.FindRoot(), "content", "kaneswrath");
        foreach (string rel in new[] { "Core/1.0/GlobalStream.big", "EnglishAudio/1.0/EnglishAudio.big", "Core/1.2/patch2.big" })
        {
            using var arc = new OpenGZH.Core.FileSystem.BigArchive(Path.Combine(root, rel));
            var counts = new Dictionary<string, int>();
            int packed = 0, n = 0;
            foreach (var f in arc.FilePaths.Where(f => f.EndsWith(".cdata")))
            {
                if (n++ % 7 != 0) continue;
                using var st = arc.Open(f);
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                var d = ms.ToArray();
                if (d.Length > 1 && d[0] == 0x10 && d[1] == 0xFB) { packed++; d = SageManifest.Unpack(d); }
                string type = f.Split('/').Last()[..8];
                string k = $"{type} codec {d[0]:x2} ch {(d[1] >> 2) + 1} rate {d[2] << 8 | d[3]} loop {d[4] >> 5}";
                counts[k] = counts.GetValueOrDefault(k) + 1;
            }
            output.WriteLine($"{rel}: {n} cdata, sampled packed {packed}: " + string.Join("; ", counts.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key} x{kv.Value}")));
        }
    }
}

public class KwStreamedAudioCensus(ITestOutputHelper output)
{
    [Fact]
    public void Streamed()
    {
        if (Environment.GetEnvironmentVariable("KW_CODECS") == null) return;
        string root = Path.Combine(OpenGZH.Client.GameContent.FindRoot(), "content", "kaneswrath");
        foreach (var (rel, stream) in new[] { ("Core/1.0/GlobalStream.big", "global_common"), ("EnglishAudio/1.0/EnglishAudio.big", "global_english") })
        {
            var s = new SageArtPackage(Path.Combine(root, rel), stream).Stream;
            var counts = new Dictionary<string, int>();
            var examples = new Dictionary<string, string>();
            foreach (var a in s.Assets.Where(a => a.TypeName == "AudioFile"))
            {
                var d = s.Data(a).ToArray();
                if (d.Length < 32) { string kk = $"short {d.Length}b"; counts[kk] = counts.GetValueOrDefault(kk) + 1; examples.TryAdd(kk, a.InstanceName + " " + string.Join(" ", d.Select(b => b.ToString("x2")))); continue; }
                uint kind = BitConverter.ToUInt32(d, 28), hdr = BitConverter.ToUInt32(d, 20), hs = BitConverter.ToUInt32(d, 24);
                string k = hs > 0 && hdr + 2 <= d.Length ? $"kind {kind} codec {d[hdr]:x2} ch {(d[hdr + 1] >> 2) + 1}" : $"kind {kind} inline";
                counts[k] = counts.GetValueOrDefault(k) + 1;
                examples.TryAdd(k, a.InstanceName);
            }
            output.WriteLine($"{rel}: " + string.Join("; ", counts.Select(kv => $"{kv.Key} x{kv.Value} (e.g. {examples[kv.Key]})")));
        }
    }
}
