using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenGZH.Formats.Images;

/// <summary>
/// A small PNG reader (8-bit greyscale, RGB, palette, grey+alpha and RGBA; not interlaced), for the
/// Dune 2000 interface art (chrome.png).
/// </summary>
public static class PngDecoder
{
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(Signature);

    public static Image Decode(byte[] data)
    {
        if (!IsPng(data))
            throw new InvalidDataException("Not a PNG file.");
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[] palette = [], transparency = [];
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    bitDepth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": transparency = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            pos += 12 + length;
            if (type == "IEND")
                break;
        }
        if (bitDepth != 8 || interlace != 0)
            throw new NotSupportedException($"PNG: only 8-bit, non-interlaced images are supported (depth {bitDepth}, interlace {interlace}).");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG colour type {colorType}.") };
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var image = new Image(width, height);
        var previous = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= channels ? line[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
                line[i] = (byte)(line[i] + filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }
            for (int x = 0; x < width; x++)
            {
                int i = x * channels;
                switch (colorType)
                {
                    case 0: image.SetPixel(x, y, line[i], line[i], line[i], 255); break;
                    case 2: image.SetPixel(x, y, line[i], line[i + 1], line[i + 2], 255); break;
                    case 3:
                    {
                        int p = line[i];
                        byte alpha = p < transparency.Length ? transparency[p] : (byte)255;
                        image.SetPixel(x, y, palette[p * 3], palette[p * 3 + 1], palette[p * 3 + 2], alpha);
                        break;
                    }
                    case 4: image.SetPixel(x, y, line[i], line[i], line[i], line[i + 1]); break;
                    default: image.SetPixel(x, y, line[i], line[i + 1], line[i + 2], line[i + 3]); break;
                }
            }
            (previous, line) = (line, previous);
        }
        return image;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
