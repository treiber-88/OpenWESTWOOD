using System.IO.Compression;
using System.Text;
using OpenWestwood.Core.FileSystem;

namespace OpenWestwood.Formats.Emperor;

/// <summary>
/// Emperor: Battle for Dune's archives: an index (.rfh) and its data (.rfd), e.g. DATA/3DDATA0001.RFH/.RFD.
///
/// Each .rfh entry: uint32 name length (with its terminating zero), uint32 time stamp, uint32 flags (bit 1:
/// compressed), uint32 stored size, uint32 size, uint32 offset into the .rfd, then the name ("3ddata\units\...").
/// A compressed file is stored as its uint32 size followed by a zlib stream.
/// </summary>
public sealed class RfhArchive : IPackage
{
    readonly record struct Entry(uint Flags, uint StoredSize, uint Size, uint Offset);

    readonly FileStream data;
    readonly Dictionary<string, Entry> entries = new(PathUtil.Comparer);
    readonly List<string> paths = new();
    readonly object dataLock = new();

    public string Name { get; }

    /// <param name="rfhPath">The index; its data is the .rfd beside it.</param>
    public RfhArchive(string rfhPath)
    {
        Name = Path.GetFileName(rfhPath);
        var index = File.ReadAllBytes(rfhPath);
        int at = 0;
        while (at + 24 <= index.Length)
        {
            int nameLength = BitConverter.ToInt32(index, at);
            uint flags = BitConverter.ToUInt32(index, at + 8);
            uint stored = BitConverter.ToUInt32(index, at + 12), size = BitConverter.ToUInt32(index, at + 16), offset = BitConverter.ToUInt32(index, at + 20);
            at += 24;
            if (nameLength <= 0 || at + nameLength > index.Length)
                break;
            string name = PathUtil.Normalize(Encoding.Latin1.GetString(index, at, nameLength).TrimEnd('\0'));
            at += nameLength;
            if (entries.TryAdd(name, new Entry(flags, stored, size, offset)))
                paths.Add(name);
        }
        data = new FileStream(Path.ChangeExtension(rfhPath, Path.GetExtension(rfhPath) == ".RFH" ? ".RFD" : ".rfd"), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public IEnumerable<string> FilePaths => paths;

    public bool Contains(string path) => entries.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!entries.TryGetValue(PathUtil.Normalize(path), out var entry))
            throw new FileNotFoundException($"{path} is not in {Name}.");
        var stored = new byte[entry.StoredSize];
        lock (dataLock)
        {
            data.Position = entry.Offset;
            data.ReadExactly(stored);
        }
        if ((entry.Flags & 2) == 0)
            return new MemoryStream(stored, writable: false);
        var result = new byte[entry.Size];
        using (var z = new ZLibStream(new MemoryStream(stored, 4, stored.Length - 4), CompressionMode.Decompress))
            z.ReadExactly(result);
        return new MemoryStream(result, writable: false);
    }

    public void Dispose() => data.Dispose();

    /// <summary>Every archive in a DATA folder (3DDATA0001.RFH, MAPS0001.RFH...).</summary>
    public static IEnumerable<RfhArchive> AllIn(string dataFolder) =>
        Directory.Exists(dataFolder)
            ? Directory.GetFiles(dataFolder, "*.rfh").Order(StringComparer.OrdinalIgnoreCase).Select(f => new RfhArchive(f))
            : [];
}
