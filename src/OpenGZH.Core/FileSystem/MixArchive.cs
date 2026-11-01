using System.Buffers.Binary;
using System.Text;

namespace OpenGZH.Core.FileSystem;

/// <summary>
/// Reads Westwood's ".mix" archives as used by Tiberian Dawn (MIXFILE.CPP):
///   uint16 count, uint32 dataSize, then count entries {uint32 crc, uint32 offset, uint32 size}
///   sorted by crc, then the data block (offsets are relative to it).
/// The archive stores only a hash of each (upper-case) file name, so names come from a list of
/// known names (see <see cref="NameCrc"/>); entries nobody knows are listed as "XXXXXXXX.unknown".
/// </summary>
public sealed class MixArchive : IPackage
{
    readonly record struct Entry(uint Offset, uint Size);

    readonly FileStream stream;
    readonly bool ownsStream;
    readonly long dataStart;
    readonly Dictionary<uint, Entry> byCrc = new();
    readonly Dictionary<string, uint> names = new(PathUtil.Comparer);
    readonly string[] knownNames;
    readonly bool crc32Ids;

    public string Name { get; }

    /// <summary>Entries whose names were not in the known list.</summary>
    public IEnumerable<uint> UnknownCrcs => byCrc.Keys.Where(crc => !names.ContainsValue(crc));

    public int Count => byCrc.Count;

    /// <param name="crc32Ids">Tiberian Sun / Red Alert 2 archives: entries are named by <see cref="Crc32Id"/>.</param>
    public MixArchive(string filePath, IEnumerable<string> knownNames, bool crc32Ids = false)
        : this(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read), true, 0, Path.GetFileName(filePath), knownNames.ToArray(), crc32Ids)
    {
    }

    /// <summary>A .mix stored inside this one (Red Alert's REDALERT.MIX holds LOCAL.MIX, MAIN.MIX holds CONQUER.MIX...).</summary>
    public MixArchive? OpenNested(string name)
    {
        if (!names.TryGetValue(PathUtil.Normalize(name), out uint crc))
            return null;
        return new MixArchive(stream, false, dataStart + byCrc[crc].Offset, $"{Name}/{name}", knownNames, crc32Ids);
    }

    MixArchive(FileStream stream, bool ownsStream, long start, string name, string[] knownNames, bool crc32Ids = false)
    {
        this.crc32Ids = crc32Ids;
        this.stream = stream;
        this.ownsStream = ownsStream;
        this.knownNames = knownNames;
        Name = name;
        lock (stream)
        {
            stream.Position = start;
            Span<byte> first = stackalloc byte[4];
            stream.ReadExactly(first);
            int count;
            byte[] table;
            if (BinaryPrimitives.ReadUInt16LittleEndian(first) != 0)
            {
                // TD's plain header: uint16 count, uint32 data size, then the entries.
                Span<byte> rest = stackalloc byte[2];
                stream.ReadExactly(rest);
                count = BinaryPrimitives.ReadUInt16LittleEndian(first);
                table = new byte[count * 12];
                stream.ReadExactly(table);
                dataStart = stream.Position;
            }
            else
            {
                // The extended header (Red Alert): uint16 0, uint16 flags (1 digest, 2 encrypted), then the header.
                int flags = BinaryPrimitives.ReadUInt16LittleEndian(first[2..]);
                if ((flags & 2) == 0)
                {
                    Span<byte> header = stackalloc byte[6];
                    stream.ReadExactly(header);
                    count = BinaryPrimitives.ReadUInt16LittleEndian(header);
                    table = new byte[count * 12];
                    stream.ReadExactly(table);
                    dataStart = stream.Position;
                }
                else
                {
                    // PKStraw: the Blowfish key under the public key, then the header in 8-byte Blowfish blocks
                    // (padded so the data starts right after the last block).
                    var encryptedKey = new byte[WestwoodCrypto.EncryptedKeyLength];
                    stream.ReadExactly(encryptedKey);
                    var blowfish = new WestwoodCrypto.Blowfish(WestwoodCrypto.DecryptKey(encryptedKey));
                    var block = new byte[8];
                    stream.ReadExactly(block);
                    blowfish.Decrypt(block);
                    count = BinaryPrimitives.ReadUInt16LittleEndian(block);
                    int headerBytes = 6 + count * 12;
                    var all = new byte[(headerBytes + 7) / 8 * 8];
                    block.CopyTo(all, 0);
                    stream.ReadExactly(all.AsSpan(8));
                    blowfish.Decrypt(all.AsSpan(8));
                    table = all.AsSpan(6, count * 12).ToArray();
                    dataStart = stream.Position;
                }
            }
            for (int i = 0; i < count; i++)
            {
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 12));
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 12 + 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 12 + 8));
                byCrc[crc] = new Entry(offset, size);
            }
        }
        foreach (var (crc, known) in Hashed(knownNames, crc32Ids))
            if (byCrc.ContainsKey(crc))
                names[known] = crc;
        foreach (uint crc in UnknownCrcs.ToList())
            names[$"{crc:X8}.unknown"] = crc;
    }

    // The name lists' hashes, worked out once a list (Red Alert 2's has a third of a million names).
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string[], List<(uint, string)>> hashedCrc32 = new(), hashedTd = new();

    static List<(uint Crc, string Name)> Hashed(string[] knownNames, bool crc32Ids) =>
        (crc32Ids ? hashedCrc32 : hashedTd).GetValue(knownNames, list =>
            list.Select(n => (crc32Ids ? Crc32Id(n) : NameCrc(n), PathUtil.Normalize(n))).ToList());

    /// <summary>
    /// Tiberian Sun's and Red Alert 2's entry id: the upper-case name, padded to a multiple of four (its length
    /// mod 4, then the first character of the last partial block repeated), then CRC-32.
    /// </summary>
    public static uint Crc32Id(string fileName)
    {
        var name = new List<byte>(Encoding.ASCII.GetBytes(fileName.ToUpperInvariant()));
        int length = name.Count, a = length >> 2 << 2;
        if ((length & 3) != 0)
        {
            name.Add((byte)(length - a));
            int pad = 3 - (length & 3);
            while (pad-- > 0)
                name.Add(name[a]);
        }
        uint crc = 0xFFFFFFFF;
        foreach (byte b in name)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>The original's Calculate_CRC of the upper-case name: rotate left one bit and add, four bytes at a time.</summary>
    public static uint NameCrc(string fileName)
    {
        var bytes = Encoding.ASCII.GetBytes(fileName.ToUpperInvariant());
        uint crc = 0;
        int i = 0;
        for (; i + 4 <= bytes.Length; i += 4)
            crc = BitOperations.RotateLeft(crc, 1) + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
        if (i < bytes.Length)
        {
            Span<byte> rest = stackalloc byte[4];
            rest.Clear();
            bytes.AsSpan(i).CopyTo(rest);
            crc = BitOperations.RotateLeft(crc, 1) + BinaryPrimitives.ReadUInt32LittleEndian(rest);
        }
        return crc;
    }

    public IEnumerable<string> FilePaths => names.Keys;

    public bool Contains(string path) => names.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!names.TryGetValue(PathUtil.Normalize(path), out uint crc))
            throw new FileNotFoundException($"{path} is not in {Name}");
        var entry = byCrc[crc];
        var data = new byte[entry.Size];
        lock (stream)
        {
            stream.Position = dataStart + entry.Offset;
            stream.ReadExactly(data);
        }
        return new MemoryStream(data, writable: false);
    }

    /// <summary>Part of an entry (a sound inside Red Alert 2's AUDIO.BAG), without reading the rest.</summary>
    public byte[] ReadRange(string path, long offset, int count)
    {
        if (!names.TryGetValue(PathUtil.Normalize(path), out uint crc))
            throw new FileNotFoundException($"{path} is not in {Name}");
        var entry = byCrc[crc];
        count = (int)Math.Clamp(entry.Size - offset, 0, count);
        var data = new byte[count];
        lock (stream)
        {
            stream.Position = dataStart + entry.Offset + offset;
            stream.ReadExactly(data);
        }
        return data;
    }

    public void Dispose()
    {
        if (ownsStream)
            stream.Dispose();
    }
}

file static class BitOperations
{
    public static uint RotateLeft(uint value, int offset) => System.Numerics.BitOperations.RotateLeft(value, offset);
}
