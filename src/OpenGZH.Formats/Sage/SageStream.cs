namespace OpenGZH.Formats.Sage;

/// <summary>
/// A loaded compiled asset stream (static_common, global_common...): its manifest plus the .bin (instance data),
/// .relo (where each asset's data hold pointers) and .imp (where they hold references to other assets).
/// Pointers in an asset's data are offsets from the start of that asset's data; an import slot holds the index
/// of one of the asset's references, which the game resolved to the referenced asset at load time.
/// </summary>
public sealed class SageStream
{
    public SageManifest Manifest { get; }
    readonly byte[] bin, relo, imp;
    readonly Dictionary<string, SageManifest.Asset> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(uint, uint), SageManifest.Asset> byId = new();

    public SageStream(byte[] manifest, byte[] bin, byte[] relo, byte[] imp)
    {
        Manifest = new SageManifest(manifest);
        this.bin = SageManifest.Unpack(bin);
        this.relo = SageManifest.Unpack(relo);
        this.imp = SageManifest.Unpack(imp);
        foreach (var a in Manifest.Assets)
        {
            byName.TryAdd(a.Name, a);
            byId.TryAdd((a.TypeId, a.InstanceId), a);
        }
    }

    /// <summary>Loads "data/&lt;name&gt;.manifest" and its .bin/.relo/.imp from an archive's files.</summary>
    public static SageStream Load(Func<string, byte[]> read, string name) =>
        new(read($"data/{name}.manifest"), read($"data/{name}.bin"), read($"data/{name}.relo"), read($"data/{name}.imp"));

    public IEnumerable<SageManifest.Asset> Assets => Manifest.Assets;

    /// <summary>An asset by "Type:Instance" name (case-insensitive).</summary>
    public SageManifest.Asset? Find(string name) => byName.GetValueOrDefault(name);

    public SageManifest.Asset? Find(string type, string instance) => Find($"{type}:{instance}");

    /// <summary>An asset's instance data.</summary>
    public ReadOnlySpan<byte> Data(SageManifest.Asset a) =>
        a.InstanceOffset + a.InstanceDataSize <= bin.Length ? bin.AsSpan((int)a.InstanceOffset, a.InstanceDataSize) : default;

    /// <summary>The asset its index'th reference names, if it is in this stream.</summary>
    public SageManifest.Asset? Reference(SageManifest.Asset a, int index) =>
        index >= 0 && index < a.References.Length ? byId.GetValueOrDefault(a.References[index]) : null;

    static List<uint> List(byte[] data, long offset, int size)
    {
        var list = new List<uint>();
        for (int k = 0; k + 4 <= size; k += 4)
        {
            uint v = BitConverter.ToUInt32(data, (int)offset + k);
            if (v == uint.MaxValue)
                break;
            list.Add(v);
        }
        return list;
    }

    /// <summary>Offsets in the asset's data that hold pointers.</summary>
    public List<uint> Relocations(SageManifest.Asset a) => List(relo, a.RelocationOffset, a.RelocationDataSize);

    /// <summary>Offsets in the asset's data that hold references to other assets.</summary>
    public List<uint> Imports(SageManifest.Asset a) => List(imp, a.ImportsOffset, a.ImportsDataSize);
}
