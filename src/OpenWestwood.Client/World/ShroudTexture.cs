using OpenWestwood.Logic;
using OpenWestwood.Logic.Vision;
using Silk.NET.OpenGL;

namespace OpenWestwood.Client.World;

/// <summary>
/// The local player's fog of war as a one-texel-per-cell texture, like the original's W3DShroud:
/// ClearAlpha, FogAlpha and ShroudAlpha (GameData) for clear, fogged and shrouded cells, filtered so
/// the edges are soft. Terrain and models multiply their colour by it.
/// </summary>
public sealed class ShroudTexture : IDisposable
{
    readonly GL gl;
    readonly ShroudManager shroud;
    readonly byte[] texels;
    readonly float[] values, scratch;
    readonly byte clearAlpha, fogAlpha, shroudAlpha;
    bool dirty = true;

    public uint Handle { get; }
    public int PlayerIndex { get; set; }

    /// <summary>World units covered by the texture (cells x cell size).</summary>
    public float WorldWidth => shroud.CellsX * shroud.CellSize;
    public float WorldHeight => shroud.CellsY * shroud.CellSize;

    public unsafe ShroudTexture(GL gl, GameLogic game, int playerIndex)
    {
        this.gl = gl;
        shroud = game.Shroud;
        PlayerIndex = playerIndex;
        var data = game.Catalog.GameData;
        clearAlpha = (byte)data.ClearAlpha;
        fogAlpha = (byte)data.FogAlpha;
        shroudAlpha = (byte)data.ShroudAlpha;
        texels = new byte[shroud.CellsX * shroud.CellsY];
        values = new float[texels.Length];
        scratch = new float[texels.Length];
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = texels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)shroud.CellsX, (uint)shroud.CellsY, 0, PixelFormat.Red, PixelType.UnsignedByte, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        shroud.CellChanged += (player, _, _, _) => { if (player == PlayerIndex) dirty = true; };
    }

    /// <summary>Re-uploads the texture if anything changed for the local player.</summary>
    public unsafe void Refresh()
    {
        if (!dirty)
            return;
        dirty = false;
        int w = shroud.CellsX, h = shroud.CellsY;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                values[y * w + x] = shroud.CellStatus(PlayerIndex, x, y) switch
                {
                    CellShroud.Clear => clearAlpha,
                    CellShroud.Fogged => fogAlpha,
                    _ => shroudAlpha,
                };
        // OpenWESTWOOD: blurred over a few cells (two box passes, about a Gaussian), so the edge of what has been seen is a
        // smooth curve rather than the cell grid's staircase.
        for (int pass = 0; pass < 2; pass++)
        {
            BoxBlur(values, scratch, w, h, 1, w);
            BoxBlur(scratch, values, h, w, w, 1);
        }
        for (int i = 0; i < texels.Length; i++)
            texels[i] = (byte)Math.Clamp(MathF.Round(values[i]), 0, 255);
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = texels)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)shroud.CellsX, (uint)shroud.CellsY, PixelFormat.Red, PixelType.UnsignedByte, p);
    }

    const int BlurRadius = 2;

    /// <summary>
    /// A running-sum box blur along one axis: <paramref name="lines"/> lines of <paramref name="length"/> values,
    /// <paramref name="step"/> apart along a line and <paramref name="lineStep"/> apart between lines (edges clamped).
    /// </summary>
    static void BoxBlur(float[] from, float[] to, int length, int lines, int step, int lineStep)
    {
        const int r = BlurRadius;
        for (int line = 0; line < lines; line++)
        {
            int origin = line * lineStep;
            float At(int i) => from[origin + Math.Clamp(i, 0, length - 1) * step];
            float sum = 0;
            for (int i = -r; i <= r; i++)
                sum += At(i);
            for (int i = 0; i < length; i++)
            {
                to[origin + i * step] = sum / (2 * r + 1);
                sum += At(i + r + 1) - At(i - r);
            }
        }
    }

    public void Bind(int unit)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose() => gl.DeleteTexture(Handle);
}
