using OpenWestwood.Core.FileSystem;

namespace OpenWestwood.Formats.Sage;

/// <summary>
/// Kane's Wrath's sounds as files: every AudioFile asset of the audio streams (global_common in GlobalStream.big, global_english
/// in EnglishAudio.big, and their patch streams) is "kwsfx/&lt;AudioFile name&gt;.wav" (EA-XAS, decoded to PCM) or ".mp3"
/// (EALayer3 rebuilt into MPEG frames), read from its .cdata ("data/&lt;stream&gt;/cdata/&lt;type id&gt;.&lt;type hash&gt;.&lt;instance
/// id&gt;.&lt;instance hash&gt;.cdata", sometimes RefPack-packed). Later archives (patches) replace earlier ones' files.
/// </summary>
public sealed class SageAudioPackage : IPackage
{
    public const string Folder = "kwsfx/";

    sealed record Entry(BigArchive Archive, string CData, byte[] StreamedHeader);

    readonly List<BigArchive> archives = new();
    readonly Dictionary<string, Entry> entries = new(PathUtil.Comparer);

    public string Name => "Kane's Wrath audio";

    public SageAudioPackage(IEnumerable<string> bigPaths)
    {
        foreach (string path in bigPaths.Where(File.Exists))
        {
            var archive = new BigArchive(path);
            int before = entries.Count;
            var files = new HashSet<string>(archive.FilePaths, PathUtil.Comparer);
            foreach (string manifest in files.Where(f => f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string stream = Path.GetFileNameWithoutExtension(manifest);
                if (!files.Contains($"data/{stream}.bin"))
                    continue;
                SageStream s;
                try
                {
                    s = SageStream.Load(name => Read(archive, name), stream);
                }
                catch
                {
                    continue;
                }
                foreach (var a in s.Assets.Where(a => a.TypeName == "AudioFile"))
                {
                    string cdata = $"data/{stream}/cdata/{a.TypeId:x8}.{a.TypeHash:x8}.{a.InstanceId:x8}.{a.InstanceHash:x8}.cdata";
                    if (!files.Contains(cdata))
                        continue;
                    var d = s.Data(a);
                    byte[] header = [];
                    bool mp3 = false;
                    if (d.Length >= 32)
                    {
                        int at = BitConverter.ToInt32(d[20..]), size = BitConverter.ToInt32(d[24..]);
                        if (size >= 8 && at > 0 && at + size <= d.Length)
                        {
                            header = d.Slice(at, size).ToArray();
                            mp3 = (header[0] & 0x0F) == SageAudio.CodecEaLayer3;
                        }
                    }
                    var entry = new Entry(archive, cdata, header);
                    entries.Remove(PathUtil.Normalize(Folder + a.InstanceName + (mp3 ? ".wav" : ".mp3")));
                    entries[PathUtil.Normalize(Folder + a.InstanceName + (mp3 ? ".mp3" : ".wav"))] = entry;
                }
            }
            if (entries.Count > before || entries.Values.Any(e => e.Archive == archive))
                archives.Add(archive);
            else
                archive.Dispose();
        }
    }

    static byte[] Read(BigArchive archive, string name)
    {
        using var s = archive.Open(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public IEnumerable<string> FilePaths => entries.Keys;

    public bool Contains(string path) => entries.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!entries.TryGetValue(PathUtil.Normalize(path), out var e))
            throw new FileNotFoundException(path);
        var data = Read(e.Archive, e.CData);
        if (data.Length > 1 && data[0] == 0x10 && data[1] == 0xFB)
            data = SageManifest.Unpack(data);
        var decoded = SageAudio.Decode(data, e.StreamedHeader) ?? throw new InvalidDataException($"{path}: unsupported EA audio codec");
        return new MemoryStream(decoded.Data, writable: false);
    }

    public void Dispose()
    {
        foreach (var a in archives)
            a.Dispose();
    }

    /// <summary>The audio archives of a Kane's Wrath install (content/kaneswrath): core then language, each base first, then patches.</summary>
    public static IEnumerable<string> ArchivesIn(string kwRoot) =>
        new[]
        {
            "Core/1.0/GlobalStream.big",
            "Core/1.1/patch1.big",
            "Core/1.2/patch2.big",
            // (The language's audio goes over the core streams, which hold short stand-ins for every voice.)
            "EnglishAudio/1.0/EnglishAudio.big",
            "EnglishAudio/1.1/patch1.big",
            "EnglishAudio/1.2/patch2.big",
        }.Select(f => OpenWestwood.Core.Installs.GameInstalls.FindFile(kwRoot, f) ?? Path.Combine(kwRoot, f));

    /// <summary>Mounts Kane's Wrath's sounds from the player's install, if the game is installed.</summary>
    public static void MountIfPresent(VirtualFileSystem fileSystem, string? kw)
    {
        if (kw == null || !Directory.Exists(kw))
            return;
        try
        {
            fileSystem.Mount(new SageAudioPackage(ArchivesIn(kw)));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not mount Kane's Wrath audio: {e.Message}");
        }
    }
}
