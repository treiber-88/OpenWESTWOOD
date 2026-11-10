using OpenWestwood.Formats.Emperor;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Emperor: Battle for Dune's files (content/emperor): what its archives hold. Set EMP_PROBE to run.</summary>
public class EmperorProbe(ITestOutputHelper output)
{
    static string Data => Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");

    [Fact]
    public void Census()
    {
        if (Environment.GetEnvironmentVariable("EMP_PROBE") is not { } want) return;
        foreach (var archive in RfhArchive.AllIn(Data))
        {
            using var a = archive;
            var files = a.FilePaths.ToList();
            output.WriteLine($"{a.Name}: {files.Count} files; " + string.Join(", ", files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count()}")));
            output.WriteLine("  folders: " + string.Join(", ", files.GroupBy(f => string.Join('/', f.Split('/').Take(Math.Max(1, f.Split('/').Length - 1)).Take(3))).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key} {g.Count()}")));
            output.WriteLine("  e.g. " + string.Join(" ", files.Where(f => want.Length == 0 || f.Contains(want, StringComparison.OrdinalIgnoreCase)).Take(25)));
        }
        if (Environment.GetEnvironmentVariable("EMP_FILE") is { } file)
            foreach (var archive in RfhArchive.AllIn(Data))
            {
                using var a = archive;
                if (!a.Contains(file)) continue;
                using var s = a.Open(file);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (Environment.GetEnvironmentVariable("EMP_OUT") is { } outDir)
                    File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(file)), bytes);
                output.WriteLine($"{file}: {bytes.Length} bytes");
                output.WriteLine(System.Text.Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, int.TryParse(Environment.GetEnvironmentVariable("EMP_CHARS"), out int n) ? n : 3000)));
            }
    }
}

public class Ra2VplProbe(ITestOutputHelper output)
{
    [Fact]
    public void DumpVpl()
    {
        if (Environment.GetEnvironmentVariable("RA2_VPL") is not { } game) return;
        var c = OpenWestwood.TiberianDawn.TdContent.Load(OpenWestwood.Client.GameContent.FindRoot(), game);
        var b = c.Files.ReadAllBytes("VOXELS.VPL");
        output.WriteLine($"{b.Length} bytes; header {string.Join(" ", Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(b, i * 4)))}");
        int sections = (int)BitConverter.ToUInt32(b, 8);
        for (int s = 0; s < sections; s++)
        {
            int at = 16 + 768 + s * 256;
            int same = Enumerable.Range(0, 256).Count(i => b[at + i] == i);
            output.WriteLine($"section {s}: identity {same}; c100->{b[at + 100]} c150->{b[at + 150]} c20->{b[at + 20]}");
        }
    }
}

public class MapNamesProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListMaps()
    {
        if (Environment.GetEnvironmentVariable("MAP_NAMES") is not { } want) return;
        var cache = new OpenWestwood.Logic.Multiplayer.MapCache();
        OpenWestwood.Client.TiberianDawn.TdMaps.AddTo(cache);
        OpenWestwood.Client.Dune2000.D2kMaps.AddTo(cache);
        foreach (var m in cache.Maps.Where(m => want.Length == 0 || (m.NameLookupTag + m.FileName).Contains(want, StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"{m.FileName} = {m.NameLookupTag} ({m.NumPlayers}p, {m.ExtentMax.X}x{m.ExtentMax.Y}, {m.SupplyPositions.Count} supply)");
    }
}

public class KwTextureProbe(ITestOutputHelper output)
{
    [Fact]
    public void Pixels()
    {
        if (Environment.GetEnvironmentVariable("KW_TEX") is not { } name) return;
        using var content = OpenWestwood.Client.GameContent.Load();
        var img = OpenWestwood.Formats.Images.Image.Load(content.FileSystem.ReadAllBytes("kw/" + name + ".dds"), name + ".dds");
        foreach (var (x, y) in new[] { (img.Width / 2, img.Height / 2), (img.Width / 4, img.Height / 2), (2, 2), (img.Width / 2, 4) })
        {
            int i = (y * img.Width + x) * 4;
            output.WriteLine($"{name} {img.Width}x{img.Height} at {x},{y}: rgba {img.Pixels[i]} {img.Pixels[i + 1]} {img.Pixels[i + 2]} {img.Pixels[i + 3]}");
        }
    }
}

/// <summary>Emperor's XBF models: reads them all (EMP_XBF=1), dumps EMP_XBF_FILE's nodes and animations.</summary>
public class EmperorXbfProbe(ITestOutputHelper output)
{
    static string Data => Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");

    [Fact]
    public void ReadAll()
    {
        if (Environment.GetEnvironmentVariable("EMP_XBF") != "1") return;
        int ok = 0, failed = 0;
        string? want = Environment.GetEnvironmentVariable("EMP_XBF_FILE");
        foreach (var archive in RfhArchive.AllIn(Data))
        {
            using var a = archive;
            foreach (string path in a.FilePaths.Where(p => p.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase)))
            {
                using var s = a.Open(path);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                XbfFile f;
                try { f = XbfFile.Read(ms.ToArray()); ok++; }
                catch (Exception e) { failed++; if (failed < 10) output.WriteLine($"FAILED {path}: {e.Message}"); continue; }
                if (want == null || !path.Contains(want, StringComparison.OrdinalIgnoreCase))
                    continue;
                output.WriteLine($"{path}: v{f.Version}, textures [{string.Join(", ", f.Textures)}]");
                output.WriteLine("  animations: " + string.Join("; ", f.Animations.Select(kv => $"{kv.Key} {string.Join(",", kv.Value.Select(r => $"{r.Start}-{r.End}"))}")));
                foreach (var n in f.AllNodes)
                {
                    int depth = 0; for (var p = n.Parent; p != null; p = p.Parent) depth++;
                    var min = n.Positions.Length > 0 ? n.Positions.Aggregate(System.Numerics.Vector3.Min) : default;
                    var max = n.Positions.Length > 0 ? n.Positions.Aggregate(System.Numerics.Vector3.Max) : default;
                    output.WriteLine($"  {new string(' ', depth * 2)}{n.Name}: {n.Positions.Length} v, {n.Faces.Length} f, tex {string.Join(",", n.Faces.Select(x => x.Texture).Distinct())}, at {n.Transform.Translation}, bounds {min}..{max}"
                        + (n.VertexAnimation is { } va ? $", VA frames {va.FrameCount} keys {va.Keys.Length} [{string.Join(",", va.Keys.Take(12))}] scale {va.Scale} stored {va.Frames.Length}x{(va.Frames.Length > 0 ? va.Frames[0].Length : 0)} interp {va.Interpolation?.Length}" : "")
                        + (n.KeyAnimation is { } ka ? $", KA frames {ka.FrameCount} flags {ka.Flags} matrices {ka.Matrices.Length} keys {ka.Keys.Count}" : ""));
                    if (n.VertexAnimation is { Frames.Length: > 0 } v2 && n.Positions.Length > 0)
                        output.WriteLine($"     static v0 {n.Positions[0]} / frame0 v0 ({v2.Frames[0][0].X},{v2.Frames[0][0].Y},{v2.Frames[0][0].Z}) n {v2.Frames[0][0].Normal:X4}; static v1 {n.Positions[1]} / ({v2.Frames[0][1].X},{v2.Frames[0][1].Y},{v2.Frames[0][1].Z})");
                    if (n.VertexAnimation is { Interpolation: { } ip } v3)
                        output.WriteLine($"     keys {string.Join(",", v3.Keys.Take(60))} / interp {string.Join(" ", ip.Take(60).Select(x => x.ToString("X8")))}");
                    if (n.KeyAnimation is { FrameToMatrix: { } fm })
                        output.WriteLine($"     frame->matrix {string.Join(",", fm.Take(30))}");
                    if (n.KeyAnimation is { Matrices.Length: > 1 } k2 && Environment.GetEnvironmentVariable("EMP_MATRICES") == "1")
                        output.WriteLine($"     m0 {k2.Matrices[0]}\n     m1 {k2.Matrices[1]}\n     node {n.Transform}");
                }
            }
        }
        output.WriteLine($"{ok} read, {failed} failed");
    }
}

public class EmperorModelProbe(ITestOutputHelper output)
{
    [Fact]
    public void Converts()
    {
        if (Environment.GetEnvironmentVariable("EMP_MODEL") is not { } name) return;
        using var content = OpenWestwood.Client.GameContent.Load();
        output.WriteLine("packages: " + string.Join(", ", content.FileSystem.Packages.Select(p => p.Name).TakeLast(4)));
        var f = content.Models.GetFile(name);
        output.WriteLine(f == null ? "null" : $"{f.Meshes.Count} meshes, {f.Hierarchy?.Pivots.Count} pivots, anims {string.Join(",", f.Animations.Select(a => a.Name + ":" + a.FrameCount))}");
        foreach (var t in f?.Meshes.SelectMany(m => m.Textures).Select(t => t.Name).Distinct() ?? [])
            output.WriteLine($"  texture {t}: {(t.Length == 0 ? "none" : content.FileSystem.Exists(t) ? "ok" : "MISSING")}");
        // Which way it faces: the bones' rest positions (muzzles out front) and the meshes' extent.
        if (f?.Hierarchy is { } h)
        {
            var world = new System.Numerics.Matrix4x4[h.Pivots.Count];
            for (int i = 0; i < h.Pivots.Count; i++)
            {
                var p = h.Pivots[i];
                var local = System.Numerics.Matrix4x4.CreateFromQuaternion(p.Rotation) * System.Numerics.Matrix4x4.CreateTranslation(p.Translation);
                world[i] = p.ParentIndex >= 0 && p.ParentIndex < i ? local * world[p.ParentIndex] : local;
                if (!p.HiddenAtRest)
                    output.WriteLine($"  bone {p.Name} at {world[i].Translation}");
            }
        }
        if ((f?.Meshes.FirstOrDefault(m => m.Positions.Length > 50 && m.Name.Contains("pose0")) ?? f?.Meshes.OrderByDescending(m => m.Positions.Length).FirstOrDefault()) is { } body)
        {
            float zMin = body.Positions.Min(v => v.Z), zMax = body.Positions.Max(v => v.Z);
            for (int band = 0; band < 6; band++)
            {
                float lo = zMin + (zMax - zMin) * band / 6, hi = zMin + (zMax - zMin) * (band + 1) / 6;
                var vs = body.Positions.Where(v => v.Z >= lo && v.Z < hi).ToList();
                if (vs.Count > 0)
                    output.WriteLine($"  {body.Name} height {band}: x {vs.Min(v => v.X):0.0}..{vs.Max(v => v.X):0.0} (mean {vs.Average(v => v.X):0.00}) y {vs.Min(v => v.Y):0.0}..{vs.Max(v => v.Y):0.0}");
            }
        }
        foreach (var m in f?.Meshes.Where(m => m.Positions.Length > 0).Take(3) ?? [])
            output.WriteLine($"  mesh {m.FullName}: x {m.Positions.Min(v => v.X):0.0}..{m.Positions.Max(v => v.X):0.0} y {m.Positions.Min(v => v.Y):0.0}..{m.Positions.Max(v => v.Y):0.0} z {m.Positions.Min(v => v.Z):0.0}..{m.Positions.Max(v => v.Z):0.0}");
    }
}

public class EmperorTextureProbe(ITestOutputHelper output)
{
    [Fact]
    public void MissingTextures()
    {
        if (Environment.GetEnvironmentVariable("EMP_TEX") != "1") return;
        var data = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        using var art = new EmperorArtPackage(data);
        var missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int models = 0;
        foreach (string path in art.FilePaths.Where(p => p.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase) && (p.Contains("/Buildings/") || p.Contains("/Units/"))))
        {
            models++;
            var xbf = XbfFile.Read(art.ReadAllBytes(path));
            foreach (string t in xbf.Textures)
                if (!art.Contains(EmperorArtPackage.Folder + "Textures/" + t))
                    missing[t] = missing.GetValueOrDefault(t) + 1;
        }
        output.WriteLine($"{models} models, {missing.Count} missing textures");
        foreach (var (t, n) in missing.OrderByDescending(x => x.Value).Take(40))
            output.WriteLine($"  {t} ({n})");
    }
}

public class EmperorSizeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Sizes()
    {
        if (Environment.GetEnvironmentVariable("EMP_SIZE") is not { } filter) return;
        var data = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        using var art = new EmperorArtPackage(data);
        foreach (string path in art.FilePaths.Where(p => p.EndsWith("_H0.xbf", StringComparison.OrdinalIgnoreCase) && p.Contains(filter, StringComparison.OrdinalIgnoreCase)).Order())
        {
            var xbf = XbfFile.Read(art.ReadAllBytes(path));
            var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
            foreach (var n in xbf.AllNodes.Where(n => !XbfToW3d.IsHelper(n.Name)))
                foreach (var p in n.Positions)
                {
                    var w = System.Numerics.Vector3.Transform(p, n.ModelTransform);
                    min = System.Numerics.Vector3.Min(min, w); max = System.Numerics.Vector3.Max(max, w);
                }
            var slct = xbf.AllNodes.FirstOrDefault(n => n.Name.StartsWith("SLCT", StringComparison.OrdinalIgnoreCase));
            static System.Numerics.Matrix4x4 Unscaled(System.Numerics.Matrix4x4 m)
            {
                var x = System.Numerics.Vector3.Normalize(new(m.M11, m.M12, m.M13)); var y = System.Numerics.Vector3.Normalize(new(m.M21, m.M22, m.M23)); var z = System.Numerics.Vector3.Normalize(new(m.M31, m.M32, m.M33));
                return new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, m.M41, m.M42, m.M43, 1);
            }
            System.Numerics.Matrix4x4 Ours(XbfNode n) => n.Parent == null ? Unscaled(n.Transform) : Unscaled(n.Transform) * Ours(n.Parent);
            var min2 = new System.Numerics.Vector3(float.MaxValue); var max2 = new System.Numerics.Vector3(float.MinValue);
            foreach (var n in xbf.AllNodes.Where(n => !XbfToW3d.IsHelper(n.Name)))
                foreach (var p in n.Positions)
                {
                    var w = System.Numerics.Vector3.Transform(p, Ours(n));
                    min2 = System.Numerics.Vector3.Min(min2, w); max2 = System.Numerics.Vector3.Max(max2, w);
                }
            output.WriteLine($"{path}: true height {(max - min).Y:F0}, ours {(max2 - min2).Y:F0}");
        }
    }
}
