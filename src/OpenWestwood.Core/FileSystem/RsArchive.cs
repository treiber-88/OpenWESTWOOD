namespace OpenWestwood.Core.FileSystem;

/// <summary>
/// Dune 2000's .RS resource archives (SOUND.RS: the weapon and interface .WAV sounds): a uint32 header
/// length, then for each file its zero-terminated name, uint32 offset and uint32 length.
/// </summary>
public sealed class RsArchive : IPackage
{
    readonly string filePath;
    readonly Dictionary<string, (uint Offset, uint Length)> entries = new(PathUtil.Comparer);

    public string Name { get; }

    public RsArchive(string filePath)
    {
        this.filePath = filePath;
        Name = Path.GetFileName(filePath);
        using var reader = new BinaryReader(File.OpenRead(filePath));
        uint headerLength = reader.ReadUInt32();
        while (reader.BaseStream.Position < headerLength + 4)
        {
            var name = new List<byte>();
            for (byte b; (b = reader.ReadByte()) != 0;)
                name.Add(b);
            uint offset = reader.ReadUInt32(), length = reader.ReadUInt32();
            entries[PathUtil.Normalize(System.Text.Encoding.ASCII.GetString(name.ToArray()))] = (offset, length);
        }
    }

    public IEnumerable<string> FilePaths => entries.Keys;

    public bool Contains(string path) => entries.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!entries.TryGetValue(PathUtil.Normalize(path), out var entry))
            throw new FileNotFoundException(path);
        using var file = File.OpenRead(filePath);
        file.Position = entry.Offset;
        var data = new byte[entry.Length];
        file.ReadExactly(data);
        return new MemoryStream(data, writable: false);
    }

    public void Dispose() { }
}
