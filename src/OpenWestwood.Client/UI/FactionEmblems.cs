using OpenWestwood.Formats.Images;
using OpenWestwood.Formats.Westwood;

namespace OpenWestwood.Client.UI;

/// <summary>
/// OpenWESTWOOD: the faction emblems of the Westwood games that keep them inside other pictures (for the lobby's
/// flags, see GuiComboBox entries' icons):
///   "TDLOGO:frame": Tiberian Dawn's LOGOS.SHP (0 GDI's eagle, 1 Nod's scorpion), in the score screen's palette (the
///   SCRSCN1.WSA animation's, as SCORE.CPP draws it);
///   "TSEMBLEM:SIDEC01" / "TSEMBLEM:SIDEC02": Tiberian Sun's GDI / Nod emblem, the middle of the sidebar radar's first
///   frame (RADAR.SHP in that side's sidebar archive, in its SIDEBAR.PAL).
/// </summary>
public static class FactionEmblems
{
    public static Image? TdLogo(int frame)
    {
        if (TiberianDawnContent.Get() is not { } content || TiberianDawnContent.Art?.Shape("LOGOS") is not { } shape
            || !content.Files.Exists("SCRSCN1.WSA") || frame < 0 || frame >= shape.FrameCount)
            return null;
        // TD's WSA header: frames, x, y, width, height, delta size, flags (16 bits each), frames + 2 offsets, the palette.
        var wsa = content.Files.ReadAllBytes("SCRSCN1.WSA");
        int at = 14 + (BitConverter.ToUInt16(wsa, 0) + 2) * 4;
        if (at + 768 > wsa.Length)
            return null;
        var indices = shape.Frame(frame);
        var image = new Image(shape.Width, shape.Height);
        for (int i = 0; i < indices.Length && i < shape.Width * shape.Height; i++)
        {
            int c = indices[i], p = at + c * 3;
            image.SetPixel(i % shape.Width, i / shape.Width, (byte)(wsa[p] * 4), (byte)(wsa[p + 1] * 4), (byte)(wsa[p + 2] * 4), c == 0 ? (byte)0 : (byte)255);
        }
        return image;
    }

    /// <summary>A frame of a Tiberian Sun / Red Alert 2 sidebar picture from a side's sidebar archive, in its SIDEBAR.PAL
    /// (or, without one there, the game's).</summary>
    public static Image? SidebarShape(string game, string sidebarMix, string shapeName, int frame)
    {
        if ((game.Equals("TS", StringComparison.OrdinalIgnoreCase) ? RedAlert2.Ra2Art.GetTiberianSun() : RedAlert2.Ra2Art.Get()) is not { } art)
            return null;
        string file = shapeName.Contains('.') ? shapeName : shapeName + ".SHP";
        var package = art.Content.Files.Packages.FirstOrDefault(p => p.Name.Contains(sidebarMix, StringComparison.OrdinalIgnoreCase) && p.Contains(file));
        byte[]? Read(string name)
        {
            if (package != null && package.Contains(name))
            {
                using var s = package.Open(name);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return art.Content.Files.Exists(name) ? art.Content.Files.ReadAllBytes(name) : null;
        }
        if (Read(file) is not { } data || Read("SIDEBAR.PAL") is not { } palette)
            return null;
        var shape = new TsShape(data);
        return frame < shape.FrameCount ? art.Frame(shape, frame, new WestwoodPalette(palette), null) : null;
    }

    public static Image? TsEmblem(string sidebarMix)
    {
        if (RedAlert2.Ra2Art.GetTiberianSun() is not { } art)
            return null;
        var package = art.Content.Files.Packages.FirstOrDefault(p => p.Name.Contains(sidebarMix, StringComparison.OrdinalIgnoreCase) && p.Contains("RADAR.SHP"));
        if (package == null || !package.Contains("SIDEBAR.PAL"))
            return null;
        byte[] Read(string name)
        {
            using var s = package.Open(name);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        var shape = new TsShape(Read("RADAR.SHP"));
        if (art.Frame(shape, 0, new WestwoodPalette(Read("SIDEBAR.PAL")), null) is not { } frame)
            return null;
        // The emblem in the middle of the radar's frame (GDI's disc; Nod's hexagon sits further left and lower).
        bool nod = sidebarMix.EndsWith("02", StringComparison.Ordinal);
        int X = nod ? 47 : 58, Y = nod ? 32 : 36, Size = nod ? 66 : 58;
        var emblem = new Image(Size, Size);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int sx = X + x, sy = Y + y;
                if (sx >= frame.Width || sy >= frame.Height)
                    continue;
                int i = (sy * frame.Width + sx) * 4;
                emblem.SetPixel(x, y, frame.Pixels[i], frame.Pixels[i + 1], frame.Pixels[i + 2], frame.Pixels[i + 3]);
            }
        return emblem;
    }
}
