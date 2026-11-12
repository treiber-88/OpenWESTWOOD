namespace OpenWestwood.Formats.Images;

/// <summary>
/// PCX pictures as the Westwood games keep them (Red Alert 2's and Tiberian Sun's interface art): 8 bits a pixel,
/// run-length coded, the 256-colour palette at the end of the file. Pixels of <paramref name="transparent"/> (Westwood's
/// magenta, 255,0,255) come out see-through.
/// </summary>
public static class PcxDecoder
{
    public static Image? Decode(byte[] data, (byte R, byte G, byte B)? transparent = null)
    {
        if (data.Length < 128 + 769 || data[0] != 0x0A || data[2] != 1 || data[3] != 8)
            return null;
        int xMin = BitConverter.ToUInt16(data, 4), yMin = BitConverter.ToUInt16(data, 6);
        int width = BitConverter.ToUInt16(data, 8) - xMin + 1, height = BitConverter.ToUInt16(data, 10) - yMin + 1;
        int planes = data[65], bytesPerLine = BitConverter.ToUInt16(data, 66);
        if (width <= 0 || height <= 0 || planes != 1 || data[data.Length - 769] != 0x0C)
            return null;
        int paletteAt = data.Length - 768;
        var image = new Image(width, height);
        var line = new byte[bytesPerLine];
        int at = 128;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < bytesPerLine && at < paletteAt;)
            {
                byte b = data[at++];
                int run = 1;
                if ((b & 0xC0) == 0xC0)
                {
                    run = b & 0x3F;
                    b = at < paletteAt ? data[at++] : (byte)0;
                }
                for (; run > 0 && x < bytesPerLine; run--)
                    line[x++] = b;
            }
            for (int x = 0; x < width; x++)
            {
                int p = paletteAt + line[x] * 3;
                byte r = data[p], g = data[p + 1], bl = data[p + 2];
                bool clear = transparent is { } t && r == t.R && g == t.G && bl == t.B;
                image.SetPixel(x, y, r, g, bl, clear ? (byte)0 : (byte)255);
            }
        }
        return image;
    }
}
