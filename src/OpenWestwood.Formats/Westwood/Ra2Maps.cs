using System.Text;

namespace OpenWestwood.Formats.Westwood;

/// <summary>LZO1X decompression (the reference lzo1x_decompress), used by Tiberian Sun / Red Alert 2 map IsoMapPack5 data.</summary>
public static class Lzo1x
{
    /// <summary>Decompresses <paramref name="src"/> into <paramref name="dst"/>; returns the bytes written.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0, op = 0, t;
        int mPos;
        // States of the reference implementation's gotos.
        const int Loop = 0, FirstLiteralRun = 1, Match = 2, MatchNext = 3, CopyMatch = 4, MatchDone = 5;
        int state = Loop;
        if (src[0] > 17)
        {
            t = src[ip++] - 17;
            if (t < 4)
                state = MatchNext;
            else
            {
                do dst[op++] = src[ip++]; while (--t > 0);
                state = FirstLiteralRun;
            }
        }
        else
            t = 0;
        mPos = 0;
        while (true)
        {
            switch (state)
            {
                case Loop:
                    if (ip >= src.Length)
                        return op;
                    t = src[ip++];
                    if (t >= 16)
                    {
                        state = Match;
                        break;
                    }
                    if (t == 0)
                    {
                        while (src[ip] == 0)
                        {
                            t += 255;
                            ip++;
                        }
                        t += 15 + src[ip++];
                    }
                    // t + 3 literals.
                    dst[op++] = src[ip++];
                    dst[op++] = src[ip++];
                    dst[op++] = src[ip++];
                    while (t-- > 0)
                        dst[op++] = src[ip++];
                    state = FirstLiteralRun;
                    break;
                case FirstLiteralRun:
                    t = src[ip++];
                    if (t >= 16)
                    {
                        state = Match;
                        break;
                    }
                    mPos = op - (1 + 0x0800);
                    mPos -= t >> 2;
                    mPos -= src[ip++] << 2;
                    dst[op++] = dst[mPos++];
                    dst[op++] = dst[mPos++];
                    dst[op++] = dst[mPos];
                    state = MatchDone;
                    break;
                case Match:
                    if (t >= 64)
                    {
                        mPos = op - 1;
                        mPos -= (t >> 2) & 7;
                        mPos -= src[ip++] << 3;
                        t = (t >> 5) - 1;
                        state = CopyMatch;
                    }
                    else if (t >= 32)
                    {
                        t &= 31;
                        if (t == 0)
                        {
                            while (src[ip] == 0)
                            {
                                t += 255;
                                ip++;
                            }
                            t += 31 + src[ip++];
                        }
                        mPos = op - 1;
                        mPos -= (src[ip] >> 2) + (src[ip + 1] << 6);
                        ip += 2;
                        state = CopyMatch;
                    }
                    else if (t >= 16)
                    {
                        mPos = op;
                        mPos -= (t & 8) << 11;
                        t &= 7;
                        if (t == 0)
                        {
                            while (src[ip] == 0)
                            {
                                t += 255;
                                ip++;
                            }
                            t += 7 + src[ip++];
                        }
                        mPos -= (src[ip] >> 2) + (src[ip + 1] << 6);
                        ip += 2;
                        if (mPos == op)
                            return op; // end of stream
                        mPos -= 0x4000;
                        state = CopyMatch;
                    }
                    else
                    {
                        mPos = op - 1;
                        mPos -= t >> 2;
                        mPos -= src[ip++] << 2;
                        dst[op++] = dst[mPos++];
                        dst[op++] = dst[mPos];
                        state = MatchDone;
                    }
                    break;
                case CopyMatch:
                    dst[op++] = dst[mPos++];
                    dst[op++] = dst[mPos++];
                    while (t-- > 0)
                        dst[op++] = dst[mPos++];
                    state = MatchDone;
                    break;
                case MatchDone:
                    t = src[ip - 2] & 3;
                    state = t == 0 ? Loop : MatchNext;
                    break;
                case MatchNext:
                    dst[op++] = src[ip++];
                    if (t > 1)
                    {
                        dst[op++] = src[ip++];
                        if (t > 2)
                            dst[op++] = src[ip++];
                    }
                    t = src[ip++];
                    state = Match;
                    break;
            }
        }
    }
}

/// <summary>
/// A Tiberian Sun / Red Alert 2 isometric terrain template (.TEM, .SNO, .URB, .UBN, .DES, .LUN): a header with the
/// template's size in cells and its cells' tile size (60x30 pixels in RA2, 48x24 in TS), then per cell an offset to a
/// tile: a 52-byte header (position, extra image, height, land type, ramp, radar colours), the diamond's pixels row
/// by row (4, 8, ... pixels wide, then narrowing), and optionally an "extra" picture (what stands up from the tile).
/// </summary>
public sealed class IsoTemplate
{
    public sealed class Tile
    {
        public int X, Y, ExtraX, ExtraY, ExtraWidth, ExtraHeight;
        public byte Height, TerrainType, RampType;
        public byte[] Pixels = []; // Width x Height, the diamond (0 outside it)
        public byte[]? Extra;
    }

    public int CellsX { get; }
    public int CellsY { get; }
    public int TileWidth { get; }
    public int TileHeight { get; }
    /// <summary>One per cell of the template (null where it has none).</summary>
    public Tile?[] Tiles { get; }

    public IsoTemplate(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        CellsX = r.ReadInt32();
        CellsY = r.ReadInt32();
        TileWidth = r.ReadInt32();
        TileHeight = r.ReadInt32();
        if (CellsX <= 0 || CellsY <= 0 || CellsX * CellsY > 4096 || TileWidth <= 0 || TileHeight <= 0)
            throw new InvalidDataException("Not an isometric terrain template.");
        var offsets = new uint[CellsX * CellsY];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = r.ReadUInt32();
        Tiles = new Tile?[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] == 0 || offsets[i] >= data.Length)
                continue;
            r.BaseStream.Position = offsets[i];
            var tile = new Tile
            {
                X = r.ReadInt32(), Y = r.ReadInt32(),
            };
            int extraOffset = r.ReadInt32();
            r.ReadInt32(); // z data
            r.ReadInt32(); // extra z data
            tile.ExtraX = r.ReadInt32();
            tile.ExtraY = r.ReadInt32();
            tile.ExtraWidth = r.ReadInt32();
            tile.ExtraHeight = r.ReadInt32();
            uint flags = r.ReadUInt32();
            tile.Height = r.ReadByte();
            tile.TerrainType = r.ReadByte();
            tile.RampType = r.ReadByte();
            r.ReadBytes(9); // radar colours, padding
            tile.Pixels = new byte[TileWidth * TileHeight];
            int width = 4;
            for (int y = 0; y < TileHeight; y++)
            {
                int start = (TileWidth - width) / 2;
                for (int x = 0; x < width && start + x < TileWidth; x++)
                    tile.Pixels[y * TileWidth + start + x] = r.ReadByte();
                width += (y < TileHeight / 2 - 1 ? 1 : -1) * 4;
            }
            if ((flags & 1) != 0 && tile.ExtraWidth > 0 && tile.ExtraHeight > 0 && offsets[i] + extraOffset + tile.ExtraWidth * tile.ExtraHeight <= data.Length)
            {
                r.BaseStream.Position = offsets[i] + extraOffset;
                tile.Extra = r.ReadBytes(tile.ExtraWidth * tile.ExtraHeight);
            }
            Tiles[i] = tile;
        }
    }
}

/// <summary>
/// A Tiberian Sun / Red Alert 2 map (.MAP / .MPR / .YRM, an INI): its theater, size, the cells' terrain tiles and
/// heights ([IsoMapPack5]: LZO blocks of 11-byte cells: X, Y, tile, sub-tile, level, ice), overlay ([OverlayPack] and
/// [OverlayDataPack]: LCW blocks, 512x512), trees ([Terrain]), buildings ([Structures]) and waypoints.
/// </summary>
public sealed class IsoMap
{
    public readonly record struct Cell(int X, int Y, int Tile, byte SubTile, byte Level);

    public Ra2Ini Ini { get; } = new();
    public string Theater { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>LocalSize: the playable part (x, y, width, height).</summary>
    public (int X, int Y, int W, int H) Local { get; }
    public List<Cell> Cells { get; } = new();
    public byte[] Overlay { get; } = Enumerable.Repeat((byte)0xFF, 512 * 512).ToArray();
    public byte[] OverlayData { get; } = new byte[512 * 512];
    /// <summary>Waypoint number to cell (x, y).</summary>
    public Dictionary<int, (int X, int Y)> Waypoints { get; } = new();
    /// <summary>[Terrain]: cell (x, y) and object type (trees).</summary>
    public List<(int X, int Y, string Type)> Terrain { get; } = new();
    /// <summary>[Structures]: owner, type, health (of 256), cell, facing.</summary>
    public List<(string Owner, string Type, int Health, int X, int Y, int Facing)> Structures { get; } = new();

    public IsoMap(string text) : this(text, headerOnly: false) { }

    /// <param name="headerOnly">Only the map's size, theater and waypoints (the map list's needs): its packed cells and
    /// overlay are left unread, which is most of the work.</param>
    public IsoMap(string text, bool headerOnly)
    {
        Ini.Read(text);
        Theater = Ini.Get("Map", "Theater", "TEMPERATE").ToUpperInvariant();
        var size = Ini.Get("Map", "Size").Split(',').Select(s => int.TryParse(s.Trim(), out int v) ? v : 0).ToArray();
        Width = size.Length > 2 ? size[2] : 0;
        Height = size.Length > 3 ? size[3] : 0;
        var local = Ini.Get("Map", "LocalSize").Split(',').Select(s => int.TryParse(s.Trim(), out int v) ? v : 0).ToArray();
        Local = local.Length > 3 ? (local[0], local[1], local[2], local[3]) : (0, 0, Width, Height);

        if (headerOnly)
        {
            foreach (var (key, value) in Ini.Section("Waypoints"))
                if (int.TryParse(key, out int n) && int.TryParse(value, out int pos))
                    Waypoints[n] = (pos % 1000, pos / 1000);
            return;
        }
        var iso = Unpack("IsoMapPack5", lzo: true);
        for (int i = 0; i + 11 <= iso.Length; i += 11)
        {
            int x = BitConverter.ToUInt16(iso, i), y = BitConverter.ToUInt16(iso, i + 2);
            int tile = BitConverter.ToInt32(iso, i + 4);
            if (x == 0 && y == 0 && tile == 0 && iso[i + 8] == 0)
                continue;
            Cells.Add(new Cell(x, y, tile, iso[i + 8], iso[i + 9]));
        }
        var overlay = Unpack("OverlayPack", lzo: false);
        if (overlay.Length >= Overlay.Length)
            Array.Copy(overlay, Overlay, Overlay.Length);
        var overlayData = Unpack("OverlayDataPack", lzo: false);
        if (overlayData.Length >= OverlayData.Length)
            Array.Copy(overlayData, OverlayData, OverlayData.Length);

        foreach (var (key, value) in Ini.Section("Waypoints"))
            if (int.TryParse(key, out int n) && int.TryParse(value, out int pos))
                Waypoints[n] = (pos % 1000, pos / 1000);
        foreach (var (key, value) in Ini.Section("Terrain"))
            if (int.TryParse(key, out int pos))
                Terrain.Add((pos % 1000, pos / 1000, value.Trim()));
        foreach (var (_, value) in Ini.Section("Structures"))
        {
            var p = value.Split(',');
            if (p.Length > 5 && int.TryParse(p[2], out int health) && int.TryParse(p[3], out int x) && int.TryParse(p[4], out int y) && int.TryParse(p[5], out int facing))
                Structures.Add((p[0], p[1], health, x, y, facing));
        }
    }

    /// <summary>A packed section: its lines' base64 joined, then blocks (uint16 compressed size, uint16 size) of LZO or LCW.</summary>
    byte[] Unpack(string section, bool lzo)
    {
        var lines = Ini.Section(section);
        if (lines.Count == 0)
            return [];
        var text = new StringBuilder();
        foreach (var (key, value) in lines.OrderBy(kv => int.TryParse(kv.Key, out int n) ? n : int.MaxValue))
            text.Append(value.Trim());
        byte[] packed;
        try { packed = Convert.FromBase64String(text.ToString()); }
        catch (FormatException) { return []; }
        var output = new MemoryStream();
        int p = 0;
        while (p + 4 <= packed.Length)
        {
            int compressed = BitConverter.ToUInt16(packed, p), size = BitConverter.ToUInt16(packed, p + 2);
            p += 4;
            if (compressed == 0 || p + compressed > packed.Length)
                break;
            var block = new byte[size];
            if (lzo)
                Lzo1x.Decompress(packed.AsSpan(p, compressed), block);
            else
                WestwoodCompression.LcwUncompress(packed.AsSpan(p, compressed), block);
            output.Write(block);
            p += compressed;
        }
        return output.ToArray();
    }
}
