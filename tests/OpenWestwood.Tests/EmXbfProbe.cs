using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Probe (EM_XBF=names;..): an Emperor model's nodes, faces, textures and effect data.</summary>
public class EmXbfProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("EM_XBF") is not { Length: > 0 } names) return;
        string data = Path.Combine(Client.GameContent.FindRoot(), "content", "emperor", "DATA");
        using var art = new Formats.Emperor.EmperorArtPackage(data);
        foreach (string name in names.Split(';'))
        {
            if (art.XbfPath(name) is not { } path) { output.WriteLine($"{name}: no xbf"); continue; }
            var x = Formats.Emperor.XbfFile.Read(art.ReadAllBytes(path));
            output.WriteLine($"{name} ({path}): textures [{string.Join(", ", x.Textures)}], fx {x.FxData.Length} bytes, anims [{string.Join(", ", x.Animations.Keys)}]");
            foreach (var n in x.AllNodes)
                output.WriteLine($"  node '{n.Name}' faces {n.Faces.Length} verts {n.Positions.Length} keyanim {n.KeyAnimation != null} vertanim {n.VertexAnimation != null}");
            if (x.FxData.Length > 0)
                output.WriteLine("  fx: " + System.Text.Encoding.Latin1.GetString(x.FxData).Replace('\0', '.')[..Math.Min(600, x.FxData.Length)]);
        }
    }
}
