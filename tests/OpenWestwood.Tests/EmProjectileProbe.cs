using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

public class EmProjectileProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Shells()
    {
        if (Environment.GetEnvironmentVariable("EM_PROJ") == null || !content.Available) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionEMHarkonnen", IsHuman: true, StartPosition: 1, Color: 0xFF0000),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        game.Scripts = null;
        foreach (var p in game.Players.All) game.Shroud.RevealMapForPlayerPermanently(p);
        var mid = new Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var a = game.CreateObject(Environment.GetEnvironmentVariable("EM_PROJ")!, game.Players.Find("Alice")!, (mid - new Vector3(70, 0, 0)) with { Z = game.Terrain.GroundHeight(mid) })!;
        var b = game.CreateObject("ChinaTankBattleMaster", game.Players.Find("Bob")!, (mid + new Vector3(70, 0, 0)) with { Z = game.Terrain.GroundHeight(mid) })!;
        b.SetDisabled("DISABLED_EMP");
        new AttackObjectCommand(game.Players.Find("Alice")!.Index, [a.Id], b.Id).Execute(game);
        for (int f = 0; f < 150; f++)
        {
            game.Update();
            foreach (var o in game.Objects.Where(o => o.IsKindOf(Kind.Projectile)))
                output.WriteLine($"{f} {o.Template.Name} #{o.Id} at {o.Position.X - mid.X:0},{o.Position.Y - mid.Y:0},{o.Position.Z:0} scale {o.DrawScale} hidden {o.IsHiddenInContainer} dh {o.DrawableHidden}");
        }
        output.WriteLine($"target health {b.Health}/{b.MaxHealth}");
    }
}

public class EmDrawnSizeProbe(ITestOutputHelper output)
{
    [Fact]
    public void DrawnSizes()
    {
        if (Environment.GetEnvironmentVariable("EM_DRAWN") is not { } names) return;
        var data = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        using var art = new OpenWestwood.Formats.Emperor.EmperorArtPackage(data);
        foreach (string name in names.Split(','))
        {
            var w3d = art.GetW3d(name)!;
            output.WriteLine($"{name}: from {art.XbfPath(name[(name.IndexOf('_') + 1)..])}");
            foreach (var anim in new OpenWestwood.Formats.W3d.W3dAnimation?[] { null }.Concat(w3d.Animations))
            {
                var pose = new OpenWestwood.Formats.W3d.W3dPose(w3d.Hierarchy?.Pivots.Count ?? 1);
                pose.Update(w3d.Hierarchy, Matrix4x4.Identity, anim, 0);
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                foreach (var sub in w3d.HLod!.Lods[^1])
                {
                    var mesh = w3d.Meshes.FirstOrDefault(m => m.FullName.Equals(sub.Name, StringComparison.OrdinalIgnoreCase));
                    if (mesh == null || mesh.IsHidden || !pose.Visible[sub.BoneIndex]) continue;
                    foreach (var p in mesh.Positions)
                    {
                        var v = Vector3.Transform(p, pose.Transforms[sub.BoneIndex]);
                        min = Vector3.Min(min, v); max = Vector3.Max(max, v);
                    }
                }
                output.WriteLine($"  {anim?.Name ?? "rest"}: size {max - min}");
                if (anim == null)
                    foreach (var m in w3d.Meshes)
                    {
                        var mn = m.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var mx = m.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                        output.WriteLine($"    mesh {m.FullName} flags {m.Flags} size {mx - mn} tex {string.Join(",", m.Textures.Select(t => t.Name))} shader src {string.Join(",", m.Shaders.Select(sh => sh.SrcBlend + "/" + sh.DestBlend))}");
                    }
            }
        }
    }
}

public class EmFxDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("EM_FX") is not { } names) return;
        var data = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        using var art = new OpenWestwood.Formats.Emperor.EmperorArtPackage(data);
        string outDir = Environment.GetEnvironmentVariable("EM_FX_OUT") ?? Path.GetTempPath();
        foreach (string name in names.Split(','))
        {
            var path = art.XbfPath(name)!;
            var bytes = art.ReadAllBytes(path);
            var xbf = OpenWestwood.Formats.Emperor.XbfFile.Read(bytes);
            File.WriteAllBytes(Path.Combine(outDir, name + ".fx"), xbf.FxData);
            output.WriteLine($"{name} ({path}): fx {xbf.FxData.Length} bytes, textures {string.Join(",", xbf.Textures)}, nodes {string.Join(", ", xbf.AllNodes.Select(n => $"{n.Name}[{n.Faces.Length}f{(n.VertexAnimation != null ? " va" : "")}{(n.KeyAnimation != null ? " ka" : "")} tex {string.Join("/", n.Faces.Select(f => f.Texture).Distinct())}]"))}");
        }
    }
}

public class EmTextureListProbe(ITestOutputHelper output)
{
    [Fact]
    public void List()
    {
        if (Environment.GetEnvironmentVariable("EM_TEX") is not { } filter) return;
        var data = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        using var art = new OpenWestwood.Formats.Emperor.EmperorArtPackage(data);
        foreach (var p in art.FilePaths.Where(p => filter.Split(',').Any(f => p.Contains(f, StringComparison.OrdinalIgnoreCase))))
            output.WriteLine(p);
    }
}
