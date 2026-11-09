using System.Buffers.Binary;
using OpenWestwood.Formats.Images;

namespace OpenWestwood.Formats.Westwood;

/// <summary>
/// Dune 2000's sprite files (DATA.R16, MOUSE.R16, the BLOX*.R16 tilesets): one frame after another, each
/// <code>
///   type    u8   1 = has its own palette, 2 = uses the last palette (0 bytes between frames are padding)
///   width   i32, height i32
///   x, y    i32  where the object's origin is in the frame (its hotspot)
///   image   u32  (an address in the original program; unused)
///   palette u32  0 if there is none
///   bpp     u8   8 (16-bit frames are skipped)
///   frame height u8, frame width u8, alignment u8
///   pixels  width * height palette indices
///   palette 520 bytes when type 1 and palette != 0: 8 bytes, then 256 colours as RGB555
/// </code>
/// Index 0 is clear. Indices 240-255 are the house colour (the game remaps them per player).
/// </summary>
public sealed class D2kSpriteFile
{
    public const int RemapFirst = 240, RemapLast = 255;

    public sealed class Frame
    {
        public required int Width, Height;
        /// <summary>The origin (where the object stands) inside the frame, in pixels from its top-left.</summary>
        public required int OriginX, OriginY;
        public required byte[] Pixels;
        /// <summary>The 256 colours the frame's indices refer to (RGB); null for a 16-bit frame.</summary>
        public required (byte R, byte G, byte B)[]? Palette;
        /// <summary>A 16-bit frame (the tilesets): its pixels as RGB555, 0 clear; null for an indexed frame.</summary>
        public ushort[]? Colors;
    }

    public List<Frame> Frames { get; } = new();

    public D2kSpriteFile(byte[] data)
    {
        int pos = 0;
        (byte, byte, byte)[]? palette = null;
        while (pos + 29 <= data.Length)
        {
            byte type = data[pos];
            if (type == 0)
            {
                pos++;
                continue;
            }
            int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 1));
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 5));
            int x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 9));
            int y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 13));
            uint paletteAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 21));
            int bpp = data[pos + 25];
            if (width < 0 || height < 0 || bpp is not (8 or 16))
                throw new InvalidDataException($"Bad Dune 2000 frame at {pos}.");
            int size = width * height * (bpp / 8);
            if (pos + 29 + size > data.Length)
                break;
            var pixels = data.AsSpan(pos + 29, bpp == 8 ? width * height : 0).ToArray();
            ushort[]? colors = null;
            if (bpp == 16)
            {
                colors = new ushort[width * height];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 29 + i * 2));
            }
            pos += 29 + size;
            if (type == 1 && paletteAddress != 0 && pos + 520 <= data.Length)
            {
                palette = new (byte, byte, byte)[256];
                for (int i = 0; i < 256; i++)
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 8 + i * 2));
                    palette[i] = ((byte)(((v >> 10) & 31) * 255 / 31), (byte)(((v >> 5) & 31) * 255 / 31), (byte)((v & 31) * 255 / 31));
                }
                pos += 520;
            }
            Frames.Add(new Frame { Width = width, Height = height, OriginX = x, OriginY = y, Pixels = pixels, Palette = bpp == 8 ? palette : null, Colors = colors });
        }
    }

    /// <summary>
    /// A frame as RGBA: 0 clear, 1 a half-dark shadow (as the game draws it), the house colours scaled onto
    /// <paramref name="team"/> keeping their shading (null keeps the art's own colours).
    /// </summary>
    public Image? ToImage(int index, (byte R, byte G, byte B)? team = null, bool shadowOnly = false, float alpha = 1)
    {
        if (index < 0 || index >= Frames.Count || Frames[index] is not { } frame || frame.Width == 0 || frame.Height == 0)
            return null;
        var image = new Image(frame.Width, frame.Height);
        if (frame.Colors is { } colors)
        {
            // A 16-bit frame (a map tile): RGB555, 0 clear.
            for (int i = 0; i < colors.Length; i++)
            {
                int v = colors[i];
                if (v == 0)
                    continue;
                int o = i * 4;
                image.Pixels[o] = (byte)(((v >> 10) & 31) * 255 / 31);
                image.Pixels[o + 1] = (byte)(((v >> 5) & 31) * 255 / 31);
                image.Pixels[o + 2] = (byte)((v & 31) * 255 / 31);
                image.Pixels[o + 3] = (byte)Math.Clamp(alpha * 255, 0, 255);
            }
            return image;
        }
        if (frame.Palette is not { } palette)
            return null;
        float top = 0;
        if (team != null)
            for (int c = RemapFirst; c <= RemapLast; c++)
                top = Math.Max(top, Luma(palette[c]));
        byte a = (byte)Math.Clamp(alpha * 255, 0, 255);
        for (int i = 0; i < frame.Pixels.Length; i++)
        {
            byte c = frame.Pixels[i];
            if (c == 0)
                continue;
            int o = i * 4;
            if (shadowOnly || c == 1)
            {
                image.Pixels[o + 3] = (byte)(a / 2);
                continue;
            }
            var (r, g, b) = palette[c];
            if (team is { } t && c >= RemapFirst)
            {
                float k = top > 0 ? Luma(palette[c]) / top : 1;
                (r, g, b) = ((byte)Math.Clamp(t.R * k, 0, 255), (byte)Math.Clamp(t.G * k, 0, 255), (byte)Math.Clamp(t.B * k, 0, 255));
            }
            image.Pixels[o] = r;
            image.Pixels[o + 1] = g;
            image.Pixels[o + 2] = b;
            image.Pixels[o + 3] = a;
        }
        return image;
    }

    static float Luma((byte R, byte G, byte B) c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
}
