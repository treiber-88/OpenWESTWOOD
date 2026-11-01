using System.Numerics;
using OpenGZH.Formats.Images;
using OpenGZH.Formats.Sage;
using OpenGZH.Formats.W3d;
using Xunit;
using Xunit.Abstractions;

namespace OpenGZH.Tests;

/// <summary>Kane's Wrath compiled art: decode census and software renders (both opt-in by environment variable).</summary>
public class KwArtTests(ITestOutputHelper output)
{
    static SageArtPackage? package;

    static SageArtPackage? Package()
    {
        string big = Path.Combine(OpenGZH.Client.GameContent.FindRoot(), "content", "kaneswrath", "Core", "1.0", "StaticStream.big");
        return File.Exists(big) ? package ??= new SageArtPackage(big) : null;
    }

    [Fact]
    public void Census()
    {
        if (Environment.GetEnvironmentVariable("KW_CENSUS") == null || Package() is not { } p)
            return;
        var s = p.Stream;
        int dds = 0, textures = 0;
        foreach (var t in s.Assets.Where(a => a.TypeName == "Texture"))
        {
            textures++;
            if (SageW3d.TextureDds(s, t) != null)
                dds++;
        }
        output.WriteLine($"textures {textures}, with DDS {dds}");
        int meshes = 0, ok = 0, skins = 0, untextured = 0;
        var failed = new List<string>();
        foreach (var a in s.Assets.Where(a => a.TypeName == "W3DMesh"))
        {
            meshes++;
            var m = SageW3d.Mesh(s, a, "X", "Y");
            if (m == null || m.Indices.Length == 0)
            {
                failed.Add(a.InstanceName);
                continue;
            }
            ok++;
            if (m.IsSkin)
                skins++;
            if (m.Textures.Count == 0)
                untextured++;
        }
        output.WriteLine($"meshes {meshes}, decoded {ok}, skins {skins}, untextured {untextured}; failed e.g. {string.Join(" ", failed.Take(10))}");
        int containers = 0, full = 0;
        foreach (var a in s.Assets.Where(a => a.TypeName == "W3DContainer"))
        {
            containers++;
            if (SageW3d.Model(s, a.InstanceName)?.Meshes.Count > 0)
                full++;
        }
        output.WriteLine($"containers {containers}, with meshes {full}");
    }

    [Fact]
    public void TextureAlpha()
    {
        string names = Environment.GetEnvironmentVariable("KW_ALPHA") ?? "";
        string outDir = Environment.GetEnvironmentVariable("KW_OUT") ?? "";
        if (names.Length == 0 || Package() is not { } p)
            return;
        foreach (string n in names.Split(','))
        {
            var dds = SageW3d.TextureDds(p.Stream, p.Stream.Find("Texture", n)!)!;
            var img = DdsDecoder.Decode(dds);
            int high = 0, low = 0;
            for (int i = 3; i < img.Pixels.Length; i += 4)
                if (img.Pixels[i] > 200) high++; else if (img.Pixels[i] < 50) low++;
            int total = img.Pixels.Length / 4;
            output.WriteLine($"{n}: {img.Width}x{img.Height} fourcc {System.Text.Encoding.ASCII.GetString(dds, 84, 4)} alpha>200 {high * 100 / total}% <50 {low * 100 / total}%");
            if (outDir.Length > 0)
            {
                var a = new Image(img.Width, img.Height);
                for (int i = 0; i < img.Pixels.Length; i += 4)
                {
                    int ch = int.TryParse(Environment.GetEnvironmentVariable("KW_CHANNEL"), out int c) ? c : 3;
                    a.Pixels[i] = a.Pixels[i + 1] = a.Pixels[i + 2] = img.Pixels[i + ch];
                    a.Pixels[i + 3] = 255;
                }
                PngWriter.Save(a, Path.Combine(outDir, n + "_alpha.png"));
            }
        }
    }

    /// <summary>Software-renders models (env KW_MODELS) to PNGs in env KW_OUT, to check the decoded geometry.</summary>
    [Fact]
    public void Render()
    {
        string models = Environment.GetEnvironmentVariable("KW_MODELS") ?? "";
        string outDir = Environment.GetEnvironmentVariable("KW_OUT") ?? "";
        if (models.Length == 0 || outDir.Length == 0 || Package() is not { } p)
            return;
        var fs = new OpenGZH.Core.FileSystem.VirtualFileSystem();
        fs.Mount(p);
        var lib = new W3dLibrary(fs);
        foreach (string name in models.Split(','))
        {
            var file = lib.GetFile(SageW3d.ModelPrefix + name);
            if (file == null)
            {
                output.WriteLine($"{name}: none");
                continue;
            }
            // KW_ANIM=animation@frame poses it.
            W3dAnimation? animation = null;
            float frame = 0;
            if (Environment.GetEnvironmentVariable("KW_ANIM") is { Length: > 0 } anim)
            {
                animation = lib.GetAnimation(SageW3d.ModelPrefix + anim.Split('@')[0]);
                frame = anim.Contains('@') ? float.Parse(anim.Split('@')[1]) : 0;
                output.WriteLine($"animation {animation?.Name}: {animation?.FrameCount} frames at {animation?.FrameRate}");
            }
            RenderFile(fs, file, Path.Combine(outDir, name + (animation != null ? $"_{frame}" : "") + ".png"), out int triangles, animation, frame);
            output.WriteLine($"{name}: {file.Meshes.Count} meshes ({file.Meshes.Count(m => m.IsSkin)} skins), {triangles} triangles, "
                + $"textures {string.Join(" ", file.Meshes.SelectMany(m => m.Textures).Select(t => t.Name).Distinct())}");
        }
    }

    static void RenderFile(OpenGZH.Core.FileSystem.VirtualFileSystem fs, W3dFile file, string path, out int triangleCount,
        W3dAnimation? animation = null, float frame = 0)
    {
        var pose = new W3dPose(file.Hierarchy?.Pivots.Count ?? 1);
        pose.Update(file.Hierarchy, Matrix4x4.Identity, animation, frame);
        const int W = 512;
        var img = new Image(W, W);
        var depth = new float[W * W];
        Array.Fill(depth, float.MinValue);
        var tris = new List<(Vector3[] P, Vector2[] T, Image? Tex)>();
        var bones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in file.HLod?.HighestLod ?? [])
            bones[sub.Name] = sub.BoneIndex;
        var textures = new Dictionary<string, Image?>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in file.Meshes.Where(m => !m.IsHidden))
        {
            Image? tex = null;
            if (mesh.Textures.Count > 0 && !textures.TryGetValue(mesh.Textures[0].Name, out tex))
                textures[mesh.Textures[0].Name] = tex = fs.Exists(mesh.Textures[0].Name) ? DdsDecoder.Decode(fs.ReadAllBytes(mesh.Textures[0].Name)) : null;
            var uv = mesh.Passes.FirstOrDefault()?.Stages.FirstOrDefault()?.TexCoords ?? [];
            int bone = bones.GetValueOrDefault(mesh.FullName);
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                var P = new Vector3[3];
                var T = new Vector2[3];
                for (int k = 0; k < 3; k++)
                {
                    int v = mesh.Indices[t + k];
                    int b = mesh.IsSkin ? mesh.BoneInfluences[v] : bone;
                    P[k] = Vector3.Transform(mesh.Positions[v], pose.Transforms[Math.Min(b, pose.Transforms.Length - 1)]);
                    T[k] = v < uv.Length ? uv[v] : default;
                }
                tris.Add((P, T, tex));
            }
        }
        triangleCount = tris.Count;
        // From the front-left and above.
        var view = Matrix4x4.CreateRotationZ(MathF.PI * 0.75f) * Matrix4x4.CreateRotationX(-MathF.PI * 0.3f);
        var all = tris.SelectMany(t => t.P).Select(v => Vector3.Transform(v, view)).ToList();
        if (all.Count == 0)
            return;
        float minX = all.Min(v => v.X), maxX = all.Max(v => v.X), minY = all.Min(v => v.Y), maxY = all.Max(v => v.Y);
        float scale = (W - 20) / Math.Max(1e-3f, Math.Max(maxX - minX, maxY - minY));
        var light = Vector3.Normalize(new(0.3f, 0.5f, 1));
        foreach (var (P0, T, tex) in tris)
        {
            var P = P0.Select(v => Vector3.Transform(v, view)).ToArray();
            var n = Vector3.Cross(P[1] - P[0], P[2] - P[0]);
            float shade = n.LengthSquared() > 0 ? 0.35f + 0.65f * Math.Abs(Vector3.Dot(Vector3.Normalize(n), light)) : 1;
            var S = P.Select(v => new Vector3(10 + (v.X - minX) * scale, W - 10 - (v.Y - minY) * scale, v.Z)).ToArray();
            int x0 = Math.Max(0, (int)S.Min(v => v.X)), x1 = Math.Min(W - 1, (int)S.Max(v => v.X) + 1);
            int y0 = Math.Max(0, (int)S.Min(v => v.Y)), y1 = Math.Min(W - 1, (int)S.Max(v => v.Y) + 1);
            float area = (S[1].X - S[0].X) * (S[2].Y - S[0].Y) - (S[2].X - S[0].X) * (S[1].Y - S[0].Y);
            if (Math.Abs(area) < 1e-6f)
                continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((S[1].X - px) * (S[2].Y - py) - (S[2].X - px) * (S[1].Y - py)) / area;
                    float w1 = ((S[2].X - px) * (S[0].Y - py) - (S[0].X - px) * (S[2].Y - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0)
                        continue;
                    float z = w0 * S[0].Z + w1 * S[1].Z + w2 * S[2].Z;
                    if (z <= depth[y * W + x])
                        continue;
                    depth[y * W + x] = z;
                    byte r = 200, g = 200, b = 200;
                    if (tex != null)
                    {
                        var uvp = w0 * T[0] + w1 * T[1] + w2 * T[2];
                        float u = uvp.X - MathF.Floor(uvp.X), vv = 1 - uvp.Y;
                        vv -= MathF.Floor(vv);
                        int tx = Math.Min(tex.Width - 1, (int)(u * tex.Width)), ty = Math.Min(tex.Height - 1, (int)(vv * tex.Height));
                        int o = (ty * tex.Width + tx) * 4;
                        r = tex.Pixels[o]; g = tex.Pixels[o + 1]; b = tex.Pixels[o + 2];
                    }
                    int at = (y * W + x) * 4;
                    img.Pixels[at] = (byte)(r * shade);
                    img.Pixels[at + 1] = (byte)(g * shade);
                    img.Pixels[at + 2] = (byte)(b * shade);
                    img.Pixels[at + 3] = 255;
                }
        }
        PngWriter.Save(img, path);
    }
}
