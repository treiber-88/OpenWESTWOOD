using System.Text;

namespace OpenGZH.Core.FileSystem;

/// <summary>
/// Red Alert 2's sound archives: AUDIO.IDX ("GABA", version, count; then per sound a 16-byte name and uint32
/// offset, size, sample rate, flags and chunk size) indexing the raw sound data in AUDIO.BAG. Flags: 1 stereo,
/// 2 16-bit PCM, 8 IMA ADPCM (in blocks of the chunk size). Each sound is presented as NAME.WAV, a RIFF WAVE
/// file made up on the fly, so the game's WAV reader plays it.
/// </summary>
public sealed class AudioBag : IPackage
{
    readonly MixArchive mix;
    readonly string bagName;
    readonly Dictionary<string, (uint Offset, uint Size, uint Rate, uint Flags, uint Chunk)> entries = new(PathUtil.Comparer);

    public string Name { get; }

    public AudioBag(MixArchive mix, string idxName, string bagName)
    {
        this.mix = mix;
        this.bagName = bagName;
        Name = $"{mix.Name}>{bagName}";
        using var reader = new BinaryReader(mix.Open(idxName));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "GABA")
            throw new InvalidDataException($"{idxName} is not a Red Alert 2 audio index.");
        int version = reader.ReadInt32(), count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = Encoding.ASCII.GetString(reader.ReadBytes(16)).TrimEnd('\0');
            uint offset = reader.ReadUInt32(), size = reader.ReadUInt32(), rate = reader.ReadUInt32(), flags = reader.ReadUInt32();
            uint chunk = version >= 2 ? reader.ReadUInt32() : 512;
            entries[PathUtil.Normalize(name + ".wav")] = (offset, size, rate, flags, chunk);
        }
    }

    public IEnumerable<string> FilePaths => entries.Keys;

    public bool Contains(string path) => entries.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!entries.TryGetValue(PathUtil.Normalize(path), out var e))
            throw new FileNotFoundException(path);
        var data = mix.ReadRange(bagName, e.Offset, (int)e.Size);
        short channels = (short)((e.Flags & 1) != 0 ? 2 : 1);
        bool adpcm = (e.Flags & 8) != 0;
        var output = new MemoryStream();
        using (var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            short blockAlign = adpcm ? (short)e.Chunk : (short)(2 * channels);
            short bits = adpcm ? (short)4 : (short)16;
            int fmtSize = adpcm ? 20 : 16;
            w.Write("RIFF"u8);
            w.Write(4 + 8 + fmtSize + 8 + data.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(fmtSize);
            w.Write((short)(adpcm ? 0x11 : 1));
            w.Write(channels);
            w.Write((int)e.Rate);
            w.Write(adpcm ? (int)(e.Rate * blockAlign / Math.Max(1, (blockAlign - 4 * channels) * 2 / channels + 1)) : (int)e.Rate * blockAlign);
            w.Write(blockAlign);
            w.Write(bits);
            if (adpcm)
            {
                w.Write((short)2);
                w.Write((short)((blockAlign - 4 * channels) * 8 / (4 * channels) + 1)); // samples per block
            }
            w.Write("data"u8);
            w.Write(data.Length);
            w.Write(data);
        }
        output.Position = 0;
        return output;
    }

    public void Dispose() { }
}
