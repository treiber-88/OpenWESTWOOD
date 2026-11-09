using OpenWestwood.Formats.Sage;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Dev probe (env KW_AUDIO=name filter): compiled AudioFile records and their .cdata files.</summary>
public class KwAudioProbe(ITestOutputHelper output)
{
    [Fact]
    public void AudioFiles()
    {
        string want = Environment.GetEnvironmentVariable("KW_AUDIO") ?? "";
        string root = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
        string big = Path.Combine(root, "Core", "1.0", "GlobalStream.big");
        if (want.Length == 0 || !File.Exists(big)) return;
        var p = new SageArtPackage(big, "global_common");
        var s = p.Stream;
        using var arc = new OpenWestwood.Core.FileSystem.BigArchive(big);
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
        string root = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
        foreach (string rel in new[] { "Core/1.0/GlobalStream.big", "EnglishAudio/1.0/EnglishAudio.big", "Core/1.2/patch2.big" })
        {
            using var arc = new OpenWestwood.Core.FileSystem.BigArchive(Path.Combine(root, rel));
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
        string root = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
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

/// <summary>Kane's Wrath's sounds decode to audio, not noise (env KW_WAV_OUT writes them out for listening).</summary>
public class KwAudioDecodeTests(ITestOutputHelper output)
{
    /// <summary>How rough a signal is: mean |difference of neighbours| / mean |sample|; speech and music stay well under 1, noise is near 1.4.</summary>
    static double Roughness(short[] s, int channels)
    {
        double diff = 0, level = 0;
        for (int i = channels; i < s.Length; i++)
        {
            diff += Math.Abs((int)s[i] - s[i - channels]);
            level += Math.Abs((int)s[i]);
        }
        return level == 0 ? 0 : diff / level;
    }

    [Fact]
    public void SoundsAndMusicDecode()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string kw = Path.Combine(root, "content", "kaneswrath");
        if (!Directory.Exists(kw)) return;
        using var p = new SageAudioPackage(SageAudioPackage.ArchivesIn(kw));
        output.WriteLine($"{p.FilePaths.Count()} sounds, {p.FilePaths.Count(f => f.EndsWith(".mp3"))} EALayer3");
        string? outDir = Environment.GetEnvironmentVariable("KW_WAV_OUT");
        foreach (string name in new[] { "ABBuild_diea", "GUPreda_VoiSelecta", "GURifle_VoiAttacka", "TM_CNC3_AC_ANY_01_v3_EDITED" })
        {
            string path = p.FilePaths.First(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            using var st = p.Open(path);
            var bytes = new byte[st.Length];
            st.ReadExactly(bytes);
            if (outDir != null) File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(path)), bytes);
            OpenWestwood.Formats.Audio.PcmSound pcm;
            if (path.EndsWith(".mp3"))
            {
                using var mp3 = new NLayer.MpegFile(new MemoryStream(bytes));
                var samples = new List<short>();
                var buf = new float[8192];
                int read;
                while ((read = mp3.ReadSamples(buf, 0, buf.Length)) > 0 && samples.Count < 48000 * 2 * 30)
                    samples.AddRange(buf.Take(read).Select(f => (short)Math.Clamp(f * 32767, -32768, 32767)));
                pcm = new() { Samples = samples.ToArray(), Channels = mp3.Channels, SampleRate = mp3.SampleRate };
            }
            else
                pcm = OpenWestwood.Formats.Audio.WavFile.Decode(bytes);
            double rough = Roughness(pcm.Samples, pcm.Channels);
            output.WriteLine($"{path}: {pcm.Channels}ch {pcm.SampleRate}Hz {pcm.DurationSeconds:F2}s peak {pcm.Samples.Max(s => Math.Abs((int)s))} roughness {rough:F3}");
            Assert.True(pcm.DurationSeconds > 0.2, path);
            Assert.True(rough < 0.6, $"{path} sounds like noise ({rough:F2})");
        }
    }
}

public class KwAudioBlockProbe(ITestOutputHelper output)
{
    [Fact]
    public void Blocks()
    {
        string want = Environment.GetEnvironmentVariable("KW_BLOCKS") ?? "";
        if (want.Length == 0) return;
        string kw = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
        foreach (string big in SageAudioPackage.ArchivesIn(kw))
        {
            using var arc = new OpenWestwood.Core.FileSystem.BigArchive(big);
            foreach (string m in arc.FilePaths.Where(f => f.EndsWith(".manifest")))
            {
                var man = new SageManifest(ReadAll(arc, m));
                foreach (var a in man.Assets.Where(a => a.TypeName == "AudioFile" && a.InstanceName.Equals(want, StringComparison.OrdinalIgnoreCase)))
                {
                    string stream = Path.GetFileNameWithoutExtension(m);
                    string cd = $"data/{stream}/cdata/{a.TypeId:x8}.{a.TypeHash:x8}.{a.InstanceId:x8}.{a.InstanceHash:x8}.cdata";
                    if (!arc.Contains(cd)) { output.WriteLine($"{big} {m}: no cdata"); continue; }
                    var d = ReadAll(arc, cd);
                    if (d[0] == 0x10 && d[1] == 0xFB) d = SageManifest.Unpack(d);
                    output.WriteLine($"{big} {m}: {d.Length}b: " + string.Join(" ", d.Take(24).Select(b => b.ToString("x2"))));
                    int at = 8;
                    while (at + 8 <= d.Length)
                    {
                        int size = d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3];
                        output.WriteLine($"  block @{at}: flag {d[at]:x2} size {size} samples {d[at + 4] << 24 | d[at + 5] << 16 | d[at + 6] << 8 | d[at + 7]}");
                        if (size < 8) break;
                        at += size;
                    }
                }
            }
        }
    }

    static byte[] ReadAll(OpenWestwood.Core.FileSystem.BigArchive arc, string f)
    {
        using var s = arc.Open(f);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

public class KwLoadTimes(ITestOutputHelper output)
{
    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("KW_TIMES") == null) return;
        string kw = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var audio = new SageAudioPackage(SageAudioPackage.ArchivesIn(kw));
        output.WriteLine($"audio index: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        using var art = new SageArtPackage(Path.Combine(kw, "Core", "1.0", "StaticStream.big"));
        output.WriteLine($"art manifest: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        _ = art.Stream;
        output.WriteLine($"art stream: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        _ = art.GetW3d("KW_GUPredTank_SKN");
        output.WriteLine($"first model: {sw.ElapsedMilliseconds} ms");
    }
}

public class KwMapProbe(ITestOutputHelper output)
{
    [Fact]
    public void ReadMaps()
    {
        if (Environment.GetEnvironmentVariable("KW_MAPS") is not { } filter) return;
        string dir = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath", "sdk", "Xml", "Maps", "official");
        foreach (string f in Directory.GetFiles(dir, "*.map", SearchOption.AllDirectories).Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(6))
        {
            var bytes = File.ReadAllBytes(f);
            if (bytes[0] == 'E' && bytes[1] == 'A' && bytes[2] == 'R') bytes = SageManifest.Unpack(bytes[8..]);
            output.WriteLine($"{Path.GetFileName(f)}: {bytes.Length}b {System.Text.Encoding.ASCII.GetString(bytes, 0, 4)}");
            if (Environment.GetEnvironmentVariable("KW_DUMP") is { } dumpDir) File.WriteAllBytes(Path.Combine(dumpDir, Path.GetFileName(f)), bytes);
            try
            {
                var m = OpenWestwood.Formats.Maps.MapFile.Read(bytes);
                output.WriteLine($"  read OK: {m.HeightMap?.Width}x{m.HeightMap?.Height}, {m.Objects.Count} objects, sides {string.Join(",", m.Sides?.Sides.Select(s => s.Name + "/" + s.Faction) ?? [])}, teams {m.Sides?.Teams.Count}, blend tiles {m.BlendTiles != null}, unread {string.Join(" ", m.UnreadChunks)}");
                output.WriteLine($"  heights {m.HeightMap?.Heights.Min()}..{m.HeightMap?.Heights.Max()} border {m.HeightMap?.BorderSize}; textures {string.Join(" ", m.BlendTiles?.TextureClasses.Select(c => c.Name) ?? [])}");
                output.WriteLine("  waypoints/specials: " + string.Join(" ", m.Objects.Where(o => o.TemplateName.Contains("Waypoint", StringComparison.OrdinalIgnoreCase) || o.TemplateName.Contains("Tiberium", StringComparison.OrdinalIgnoreCase)).GroupBy(o => o.TemplateName).Select(g => $"{g.Key}x{g.Count()}")));
                output.WriteLine("  field props: " + string.Join(" | ", m.Objects.Where(o => o.TemplateName.StartsWith("Tiberium")).Take(3).Select(o => o.TemplateName + " " + o.Position + " " + string.Join(",", o.Properties.Select(kv => kv.Key + "=" + kv.Value)))));
                output.WriteLine("  waypoint names: " + string.Join(" ", m.Objects.Select(o => o.Properties.TryGetValue("waypointName", out var w) ? w.ToString() : null).Where(w => w != null).Take(20)));
                output.WriteLine("  object kinds: " + string.Join(" ", m.Objects.GroupBy(o => o.TemplateName).OrderByDescending(g => g.Count()).Take(25).Select(g => $"{g.Key}x{g.Count()}")));
            }
            catch (Exception e) { output.WriteLine($"  {e.GetType().Name}: {e.Message} {string.Join(" / ", (e.StackTrace ?? "").Split('\n').Take(4).Select(s => s.Trim()))}"); }
            if (Environment.GetEnvironmentVariable("KW_BLEND") != null)
            {
                var cr = new OpenWestwood.Formats.Maps.DataChunkReader(bytes);
                foreach (var c in cr.Chunks())
                {
                    if (c.Label is not ("BlendTileData" or "HeightMapData")) continue;
                    var r = cr.Reader;
                    output.WriteLine($"  {c.Label} v{c.Version} start {c.Start} size {c.Size}");
                    long p0 = r.BaseStream.Position;
                    var head = new List<string>();
                    for (int i = 0; i < 12; i++) head.Add(r.ReadInt32().ToString());
                    output.WriteLine("   head ints: " + string.Join(" ", head));
                    if (c.Label == "BlendTileData")
                    {
                        r.BaseStream.Position = p0;
                        int count = r.ReadInt32();
                        // Find ints that look like section boundaries: scan for markers after count shorts.
                        for (int k = 0; k < 8; k++)
                        {
                            long at2 = p0 + 4 + (long)count * 2 * k;
                            if (at2 + 32 > c.End) break;
                            r.BaseStream.Position = at2;
                            var vals = new List<string>();
                            for (int i = 0; i < 8; i++) vals.Add(r.ReadInt32().ToString("X8"));
                            output.WriteLine($"   after {k} arrays (@{at2 - p0}): {string.Join(" ", vals)}");
                        }
                        // locate first ascii texture name: look for a short-length-prefixed string of letters
                        var span = new ArraySegment<byte>(bytes, (int)p0, c.Size).ToArray();
                        for (int i = 4; i + 12 < span.Length; i++)
                        {
                            int len = span[i] | span[i + 1] << 8;
                            if (len is >= 5 and < 40 && i + 2 + len <= span.Length && System.Text.Encoding.ASCII.GetString(span.AsSpan(i + 2, len)).All(char.IsLetterOrDigit))
                            {
                                var ints = new List<string>();
                                for (int j = -36; j < 0; j += 4) ints.Add(BitConverter.ToInt32(span.AsSpan(i + j, 4)).ToString());
                                output.WriteLine($"   first name '{System.Text.Encoding.ASCII.GetString(span.AsSpan(i + 2, len))}' at +{i} (count*2={count * 2}); ints before: {string.Join(" ", ints)}");
                                break;
                            }
                        }
                        output.WriteLine($"   tail ints: {string.Join(" ", Enumerable.Range(0, 16).Select(i => BitConverter.ToInt32(span.AsSpan(span.Length - 64 + i * 4, 4)).ToString("X")))}");
                    }
                    r.BaseStream.Position = c.End;
                }
            }
            // Chunk names
            int n = BitConverter.ToInt32(bytes, 4), at = 8;
            var names = new List<string>();
            for (int i = 0; i < n; i++) { int len = bytes[at]; names.Add(System.Text.Encoding.ASCII.GetString(bytes, at + 1, len)); at += 1 + len + 4; }
            output.WriteLine("  chunks: " + string.Join(" ", names.Take(60)));
        }
    }
}

public class KwMapStreamProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListMapStream()
    {
        if (Environment.GetEnvironmentVariable("KW_MAPSTREAM") is not { } map) return;
        string root = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
        using var big = new OpenWestwood.Core.FileSystem.BigArchive(Path.Combine(root, @"Meta\1.0\maps.big"));
        byte[] Read(string n) { using var s = big.Open(n); using var ms = new MemoryStream(); s.CopyTo(ms); return SageManifest.Unpack(ms.ToArray()); }
        string dir = $"data/maps/official/{map}/";
        var m = new SageManifest(Read(dir + "map.manifest"));
        var bin = Read(dir + "map.bin");
        output.WriteLine($"{m.Assets.Count} assets, bin {bin.Length}; refs {string.Join(" ", m.ReferencedManifests)}");
        output.WriteLine("ids: " + string.Join(" ", m.Assets.Take(8).Select(a => $"{a.Name}={a.InstanceId}/{a.TypeId}/{a.InstanceHash}/{a.TypeHash}")));
        output.WriteLine("types: " + string.Join(", ", m.Assets.GroupBy(a => a.TypeName).Select(g => $"{g.Key} {g.Count()}")));
        string want = Environment.GetEnvironmentVariable("KW_TYPES") ?? "";
        output.WriteLine("textures: " + string.Join(" ", m.Assets.Where(a => a.TypeName == "Texture").Select(a => a.InstanceName + "(" + a.InstanceDataSize + ")").Take(100)));
        foreach (var a in m.Assets.Where(a => want.Split(',').Contains(a.TypeName)).Take(3))
        {
            output.WriteLine($"== {a.Name} data {a.InstanceDataSize} @{a.InstanceOffset} relo {a.RelocationDataSize} imp {a.ImportsDataSize} refs {a.References.Length} src {a.SourceFile}");
            var data = bin.AsSpan((int)a.InstanceOffset, a.InstanceDataSize).ToArray();
            if (Environment.GetEnvironmentVariable("KW_OUT") is { } outDir) File.WriteAllBytes(Path.Combine(outDir, a.TypeName + "_" + a.InstanceName + ".bin"), data);
            for (int row = 0; row < Math.Min(int.TryParse(Environment.GetEnvironmentVariable("KW_ROWS"), out int rows) ? rows : 12, (data.Length + 15) / 16); row++)
            {
                int o = row * 16;
                var bytes = data.Skip(o).Take(16).ToArray();
                output.WriteLine($"  {o:x4}: {string.Join(" ", bytes.Select(x => x.ToString("x2"))),-48} {new string(bytes.Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray())}");
            }
        }
    }
}
