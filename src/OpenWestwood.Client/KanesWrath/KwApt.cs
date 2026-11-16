using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Sage;

namespace OpenWestwood.Client.KanesWrath;

/// <summary>Kane's Wrath's interface movies (Core/1.0/Apt.big, data/aptui/&lt;name&gt;), read once each.</summary>
public static class KwApt
{
    static readonly Dictionary<string, AptMovie?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static string ArchivePath(GameContent content) => Path.Combine(content.RootFolder, "content", "kaneswrath", "Core", "1.0", "Apt.big");

    public static AptMovie? Load(GameContent content, string name)
    {
        lock (cache)
        {
            if (cache.TryGetValue(name, out var cached))
                return cached;
            AptMovie? movie = null;
            try
            {
                string path = ArchivePath(content);
                if (File.Exists(path))
                {
                    using var big = new BigArchive(path);
                    byte[] Read(string p)
                    {
                        using var s = big.Open(p);
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                    if (big.Contains($"data/aptui/{name}.manifest"))
                        movie = AptMovie.Read(SageStream.Load(Read, "aptui/" + name));
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not read Kane's Wrath's {name}: {e.Message}");
            }
            return cache[name] = movie;
        }
    }
}
