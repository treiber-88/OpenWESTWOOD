using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace OpenGZH.Formats.Westwood;

/// <summary>
/// Tiberian Sun / Red Alert 2 ".shp" sprites: a header (0, width, height, frame count), then a 24-byte record a
/// frame (its rectangle within the full size, flags, radar colour, data offset). Flag 2: each line is RLE
/// ("format 3": a uint16 line length, then bytes where 0 is followed by a count of transparent pixels).
/// Pixels are palette indices; 0 is transparent.
/// </summary>
public sealed class TsShape
{
    readonly byte[] data;
    readonly (int X, int Y, int W, int H, uint Flags, int Offset)[] frames;

    public int Width { get; }
    public int Height { get; }
    public int FrameCount => frames.Length;

    public TsShape(byte[] data)
    {
        this.data = data;
        if (data.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0)
            throw new InvalidDataException("not a TS/RA2 shape");
        Width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        Height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (Width == 0 || Height == 0 || 8 + count * 24 > data.Length)
            throw new InvalidDataException("not a TS/RA2 shape");
        frames = new (int, int, int, int, uint, int)[count];
        for (int i = 0; i < count; i++)
        {
            var r = data.AsSpan(8 + i * 24);
            frames[i] = (BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[8..]), BinaryPrimitives.ReadInt32LittleEndian(r[20..]));
        }
    }

    /// <summary>The frame as Width x Height palette indices (its rectangle placed within the full size).</summary>
    public byte[] Frame(int index)
    {
        var pixels = new byte[Width * Height];
        if (index < 0 || index >= frames.Length)
            return pixels;
        var (fx, fy, fw, fh, flags, offset) = frames[index];
        if (offset <= 0 || fw == 0 || fh == 0)
            return pixels;
        int p = offset;
        for (int y = 0; y < fh; y++)
        {
            int row = (fy + y) * Width + fx;
            if ((flags & 2) == 0)
            {
                for (int x = 0; x < fw && p < data.Length; x++)
                    Put(row, x, fy + y, data[p++]);
                continue;
            }
            int lineEnd = p + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
            p += 2;
            int xx = 0;
            while (p < lineEnd && p < data.Length && xx < fw)
            {
                byte b = data[p++];
                if (b == 0)
                    xx += p < data.Length ? data[p++] : 0;
                else
                    Put(row, xx++, fy + y, b);
            }
            p = lineEnd;
        }
        return pixels;

        void Put(int row, int x, int y, byte c)
        {
            if (fx + x < Width && y < Height)
                pixels[row + x] = c;
        }
    }
}

/// <summary>
/// A voxel model (".vxl", Red Alert 2 / Tiberian Sun): "Voxel Animation" header, a palette, the limbs' headers,
/// their span data (per column: runs of skip, count, then colour and normal index per voxel), and their tailers
/// (the span tables' offsets, scale, transform, bounds and size).
/// </summary>
public sealed class VoxelModel
{
    public sealed class Limb
    {
        public string Name = "";
        public int SizeX, SizeY, SizeZ, NormalsMode;
        public float Scale;
        public Matrix4x4 Transform = Matrix4x4.Identity;
        public Vector3 MinBounds, MaxBounds;
        /// <summary>The voxels: (x, y, z, colour index, normal index).</summary>
        public List<(byte X, byte Y, byte Z, byte Color, byte Normal)> Voxels = new();
    }

    public List<Limb> Limbs { get; } = new();

    public VoxelModel(byte[] data)
    {
        if (data.Length < 34 || Encoding.ASCII.GetString(data, 0, 15) != "Voxel Animation")
            throw new InvalidDataException("not a voxel model");
        int limbCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        int bodySize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
        int headers = 34 + 768;
        int body = headers + limbCount * 28;
        int tailers = body + bodySize;
        for (int i = 0; i < limbCount; i++)
        {
            var limb = new Limb { Name = Encoding.ASCII.GetString(data, headers + i * 28, 16).Split('\0')[0] };
            var t = data.AsSpan(tailers + i * 92);
            int spanStart = BinaryPrimitives.ReadInt32LittleEndian(t), spanEnd = BinaryPrimitives.ReadInt32LittleEndian(t[4..]), spanData = BinaryPrimitives.ReadInt32LittleEndian(t[8..]);
            limb.Scale = BinaryPrimitives.ReadSingleLittleEndian(t[12..]);
            int tailer = tailers + i * 92;
            float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(tailer + 16 + k * 4));
            limb.Transform = new Matrix4x4(F(0), F(4), F(8), 0, F(1), F(5), F(9), 0, F(2), F(6), F(10), 0, F(3), F(7), F(11), 1);
            limb.MinBounds = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(t[64..]), BinaryPrimitives.ReadSingleLittleEndian(t[68..]), BinaryPrimitives.ReadSingleLittleEndian(t[72..]));
            limb.MaxBounds = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(t[76..]), BinaryPrimitives.ReadSingleLittleEndian(t[80..]), BinaryPrimitives.ReadSingleLittleEndian(t[84..]));
            limb.SizeX = t[88]; limb.SizeY = t[89]; limb.SizeZ = t[90]; limb.NormalsMode = t[91];
            int columns = limb.SizeX * limb.SizeY;
            for (int c = 0; c < columns; c++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + spanStart + c * 4));
                if (start == -1)
                    continue;
                int end = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + spanEnd + c * 4));
                int p = body + spanData + start, stop = body + spanData + end;
                int z = 0;
                while (z < limb.SizeZ && p < stop && p < data.Length)
                {
                    z += data[p++];
                    int count = data[p++];
                    for (int k = 0; k < count && p + 1 < data.Length; k++, z++)
                    {
                        limb.Voxels.Add(((byte)(c % limb.SizeX), (byte)(c / limb.SizeX), (byte)z, data[p], data[p + 1]));
                        p += 2;
                    }
                    p++; // the count again
                }
            }
            Limbs.Add(limb);
        }
    }
}

/// <summary>A voxel model's limb transforms over its frames (".hva": name, frames, limbs, then a 3x4 matrix each).</summary>
public sealed class HvaAnimation
{
    public int Frames { get; }
    public string[] Limbs { get; }
    readonly Matrix4x4[] matrices;

    public HvaAnimation(byte[] data)
    {
        Frames = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        int limbs = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        Limbs = Enumerable.Range(0, limbs).Select(i => Encoding.ASCII.GetString(data, 24 + i * 16, 16).Split('\0')[0]).ToArray();
        matrices = new Matrix4x4[Frames * limbs];
        int p = 24 + limbs * 16;
        for (int i = 0; i < matrices.Length; i++, p += 48)
        {
            float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p + k * 4));
            matrices[i] = new Matrix4x4(F(0), F(4), F(8), 0, F(1), F(5), F(9), 0, F(2), F(6), F(10), 0, F(3), F(7), F(11), 1);
        }
    }

    public Matrix4x4 Transform(int frame, int limb) =>
        Frames == 0 || Limbs.Length == 0 ? Matrix4x4.Identity : matrices[(frame % Frames) * Limbs.Length + Math.Clamp(limb, 0, Limbs.Length - 1)];
}

/// <summary>A string table (".csf", "CSF "): labels (e.g. "Name:HTNK") to text, the text stored as inverted UTF-16.</summary>
public static class CsfFile
{
    public static Dictionary<string, string> Read(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.Length < 24 || Encoding.ASCII.GetString(data, 0, 4) != " FSC")
            return result;
        int labels = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int p = 24;
        for (int i = 0; i < labels && p + 12 <= data.Length; i++)
        {
            p += 4; // " LBL"
            int pairs = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
            string name = Encoding.ASCII.GetString(data, p + 8, nameLength);
            p += 8 + nameLength;
            for (int k = 0; k < pairs && p + 8 <= data.Length; k++)
            {
                bool extra = Encoding.ASCII.GetString(data, p, 4) == "WRTS"; // (" RTS" plain, "WRTS" with an extra value)
                int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
                var chars = new byte[length * 2];
                for (int c = 0; c < chars.Length; c++)
                    chars[c] = (byte)~data[p + 8 + c];
                if (k == 0)
                    result[name] = Encoding.Unicode.GetString(chars);
                p += 8 + length * 2;
                if (extra)
                    p += 4 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            }
        }
        return result;
    }
}
