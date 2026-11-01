using System.Text;

namespace OpenGZH.Formats.Sage;

/// <summary>
/// A compiled SAGE asset stream's index (Command &amp; Conquer 3 / Kane's Wrath .manifest): the stream's header, then one
/// entry per asset (type id, instance id, their hashes, its references, name, source file, and the sizes of its
/// instance data, relocations and imports in the matching .bin / .relo / .imp), then the reference, referenced-manifest,
/// name and source-file buffers. An asset's name is "Type:Instance" (e.g. "GameObject:GDIPredator"); its instance
/// data are the .bin's bytes in entry order.
/// </summary>
public sealed class SageManifest
{
    public sealed record Asset(uint TypeId, uint InstanceId, uint TypeHash, uint InstanceHash, string Name, string SourceFile,
        int InstanceDataSize, int RelocationDataSize, int ImportsDataSize, (uint TypeId, uint InstanceId)[] References)
    {
        /// <summary>Its type's name ("GameObject"), from its name.</summary>
        public string TypeName => Name.IndexOf(':') is > 0 and var c ? Name[..c] : "";
        public string InstanceName => Name.IndexOf(':') is > 0 and var c ? Name[(c + 1)..] : Name;
        /// <summary>Where its instance data start in the .bin (assets in order).</summary>
        public long InstanceOffset { get; internal set; }
        public long RelocationOffset { get; internal set; }
        public long ImportsOffset { get; internal set; }
    }

    /// <summary>A stream file (.manifest, .bin, .relo, .imp) unpacked if it is RefPack-compressed.</summary>
    public static byte[] Unpack(byte[] data) =>
        data.Length > 5 && data[1] == 0xFB && (data[0] & 0x3E) == 0x10 ? Compression.RefPack.Decompress(data) : data;

    public bool IsBigEndian { get; }
    public bool IsLinked { get; }
    public int Version { get; }
    public uint StreamChecksum { get; }
    public uint AllTypesHash { get; }
    public List<Asset> Assets { get; } = new();
    /// <summary>Other streams this one uses (e.g. static_common for global_common).</summary>
    public List<string> ReferencedManifests { get; } = new();

    public SageManifest(byte[] data)
    {
        // (Stored RefPack-compressed: "10 FB", the size, then the packed stream.)
        data = Unpack(data);
        using var r = new BinaryReader(new MemoryStream(data));
        IsBigEndian = r.ReadByte() != 0;
        IsLinked = r.ReadByte() != 0;
        Version = r.ReadUInt16();
        StreamChecksum = r.ReadUInt32();
        AllTypesHash = r.ReadUInt32();
        int assetCount = r.ReadInt32();
        r.ReadUInt32(); // total instance data size
        r.ReadUInt32(); // max instance chunk size
        r.ReadUInt32(); // max relocation chunk size
        r.ReadUInt32(); // max imports chunk size
        int referenceBufferSize = r.ReadInt32();
        int referencedManifestBufferSize = r.ReadInt32();
        int nameBufferSize = r.ReadInt32();
        int sourceBufferSize = r.ReadInt32();
        var raw = new (uint TypeId, uint InstanceId, uint TypeHash, uint InstanceHash, int RefOffset, int RefCount, int NameOffset, int SourceOffset, int Instance, int Relocation, int Imports)[assetCount];
        for (int i = 0; i < assetCount; i++)
            raw[i] = (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var references = r.ReadBytes(referenceBufferSize);
        var manifests = r.ReadBytes(referencedManifestBufferSize);
        var names = r.ReadBytes(nameBufferSize);
        var sources = r.ReadBytes(sourceBufferSize);
        static string Text(byte[] buffer, int offset)
        {
            if (offset < 0 || offset >= buffer.Length)
                return "";
            int end = Array.IndexOf(buffer, (byte)0, offset);
            return Encoding.ASCII.GetString(buffer, offset, (end < 0 ? buffer.Length : end) - offset);
        }
        // Referenced manifests: each a type character ('p' patch, 'r' reference...) then its name.
        for (int p = 0; p < manifests.Length;)
        {
            int end = Array.IndexOf(manifests, (byte)0, p + 1);
            if (end < 0)
                end = manifests.Length;
            if (end > p + 1)
                ReferencedManifests.Add(Encoding.ASCII.GetString(manifests, p + 1, end - p - 1));
            p = end + 1;
        }
        // (Each of .bin, .relo and .imp starts with a 4-byte header.)
        long instance = 4, relocation = 4, imports = 4;
        foreach (var a in raw)
        {
            var refs = new (uint, uint)[Math.Max(0, a.RefCount)];
            for (int k = 0; k < refs.Length && a.RefOffset + k * 8 + 8 <= references.Length; k++)
                refs[k] = (BitConverter.ToUInt32(references, a.RefOffset + k * 8), BitConverter.ToUInt32(references, a.RefOffset + k * 8 + 4));
            var asset = new Asset(a.TypeId, a.InstanceId, a.TypeHash, a.InstanceHash, Text(names, a.NameOffset), Text(sources, a.SourceOffset),
                a.Instance, a.Relocation, a.Imports, refs)
            {
                InstanceOffset = instance, RelocationOffset = relocation, ImportsOffset = imports,
            };
            instance += a.Instance;
            relocation += a.Relocation;
            imports += a.Imports;
            Assets.Add(asset);
        }
    }
}
