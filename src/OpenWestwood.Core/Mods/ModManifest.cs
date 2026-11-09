using OpenWestwood.Core.FileSystem;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenWestwood.Core.Mods;

/// <summary>
/// Describes a mod: which packages to mount (in priority order) and which rules files to load.
/// Loaded from "mods/&lt;id&gt;/mod.yaml". The base game itself is the mod "zh".
/// </summary>
public sealed class ModManifest
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>Another mod this one builds on. Its packages and rules load after (i.e. below) this mod's.</summary>
    public string? Inherits { get; set; }

    /// <summary>Which game's engine runs this mod: "ZeroHour" (default) or "TiberianDawn".</summary>
    public string Game { get; set; } = "ZeroHour";

    /// <summary>
    /// Packages in priority order: the FIRST entry wins when two packages contain the same file.
    /// Paths are relative to the OpenWESTWOOD root folder. Entries ending in ".big" are archives;
    /// anything else is a folder. "folder@Prefix" mounts a folder under a virtual directory.
    /// </summary>
    public List<string> Packages { get; set; } = new();

    /// <summary>Rules files (YAML), loaded in order. Later files may override or extend earlier ones.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>
    /// For Westwood ".mix" packages (which store only hashes of their file names): a text file
    /// listing the names to look for, one per line ("{mod}" is this mod's folder).
    /// </summary>
    public string? MixNames { get; set; }

    /// <summary>How the ".mix" packages name their entries: "Crc32" for Tiberian Sun and Red Alert 2, else Westwood's older hash.</summary>
    public string? MixHash { get; set; }

    /// <summary>The folder this manifest was loaded from. Set by the loader.</summary>
    [YamlIgnore] public string Folder { get; set; } = "";

    public static ModManifest Load(string manifestPath)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var manifest = deserializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath));
        manifest.Folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        return manifest;
    }

    /// <summary>Opens every package listed by this manifest (and the mods it inherits) into a file system.</summary>
    public VirtualFileSystem MountPackages(string gameRoot, Func<string, ModManifest>? findMod = null)
    {
        var fileSystem = new VirtualFileSystem();
        foreach (var mod in InheritanceChain(findMod))
            foreach (string entry in mod.Packages)
                MountEntry(fileSystem, gameRoot, mod, entry);
        return fileSystem;
    }

    /// <summary>This mod first, then its parent, then the parent's parent...</summary>
    public IEnumerable<ModManifest> InheritanceChain(Func<string, ModManifest>? findMod)
    {
        var current = this;
        var seen = new HashSet<string>();
        while (true)
        {
            if (!seen.Add(current.Id))
                throw new InvalidDataException($"Mod inheritance loop at '{current.Id}'.");
            yield return current;
            if (current.Inherits == null || findMod == null)
                yield break;
            current = findMod(current.Inherits);
        }
    }

    // The name lists, read once (every archive of a mod shares the same array, so its hashes are worked out once).
    static readonly Dictionary<string, string[]> nameLists = new(StringComparer.OrdinalIgnoreCase);

    static string[] NameList(ModManifest mod, string gameRoot)
    {
        string key = (mod.MixNames ?? "") + "|" + mod.Folder + "|" + gameRoot;
        lock (nameLists)
        {
            if (!nameLists.TryGetValue(key, out var names))
                nameLists[key] = names = (mod.MixNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(n => n.Replace("{mod}", mod.Folder))
                    .Select(n => Path.IsPathRooted(n) ? n : Path.Combine(gameRoot, n))
                    .Where(File.Exists)
                    .SelectMany(File.ReadAllLines).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToArray();
            return names;
        }
    }

    static void MountEntry(VirtualFileSystem fileSystem, string gameRoot, ModManifest mod, string entry)
    {
        string spec = entry.Replace("{mod}", mod.Folder);
        // "crc32:...": a Tiberian Sun / Red Alert 2 archive (CRC-32 ids) mounted in a mod that otherwise hashes the older way.
        bool crc32 = string.Equals(mod.MixHash, "Crc32", StringComparison.OrdinalIgnoreCase);
        if (spec.StartsWith("crc32:", StringComparison.OrdinalIgnoreCase))
        {
            crc32 = true;
            spec = spec[6..];
        }
        string prefix = "";
        int at = spec.IndexOf('@');
        if (at >= 0)
        {
            prefix = spec[(at + 1)..];
            spec = spec[..at];
        }

        string fullPath = Path.IsPathRooted(spec) ? spec : Path.Combine(gameRoot, spec);
        bool optional = false;
        if (fullPath.EndsWith('?'))
        {
            optional = true;
            fullPath = fullPath[..^1];
        }

        // "...MIX>AUDIO.BAG": Red Alert 2's sound archive (its .IDX beside it) in that archive, its sounds as .WAV files.
        string? bag = null;
        if (fullPath.EndsWith(".bag", StringComparison.OrdinalIgnoreCase) && fullPath.LastIndexOf('>') is > 0 and var cut)
        {
            bag = fullPath[(cut + 1)..];
            fullPath = fullPath[..cut];
        }

        // (An archive inside a .MIX may be known only by its id: "OUTER.MIX>B904E366.unknown".)
        if (fullPath.EndsWith(".mix", StringComparison.OrdinalIgnoreCase) || fullPath.Contains(".mix>", StringComparison.OrdinalIgnoreCase))
        {
            // "OUTER.MIX>INNER.MIX>...": an archive inside another (Red Alert's REDALERT.MIX>LOCAL.MIX).
            var chain = fullPath.Split('>');
            if (File.Exists(chain[0]))
            {
                // MixNames: one or more name lists (";"), e.g. Tiberian Dawn's and Red Alert's.
                var names = NameList(mod, gameRoot);
                var mix = new MixArchive(chain[0], names, crc32);
                foreach (string inner in chain.Skip(1))
                {
                    var nested = mix.OpenNested(inner);
                    if (nested == null)
                    {
                        if (!optional)
                            throw new FileNotFoundException($"Mod '{mod.Id}' needs '{inner}' inside '{mix.Name}', which is missing.");
                        return;
                    }
                    mix = nested; // the outer archive's stream stays open for it
                }
                IPackage package = mix;
                if (bag != null)
                {
                    string idx = Path.ChangeExtension(bag, ".IDX");
                    if (!mix.Contains(bag) || !mix.Contains(idx))
                    {
                        if (!optional)
                            throw new FileNotFoundException($"Mod '{mod.Id}' needs '{bag}' and '{idx}' inside '{mix.Name}'.");
                        return;
                    }
                    package = new AudioBag(mix, idx, bag);
                }
                // "...@Prefix": its files under a folder.
                fileSystem.Mount(prefix.Length > 0 ? new PrefixedPackage(package, prefix) : package);
            }
            else if (!optional)
                throw new FileNotFoundException($"Mod '{mod.Id}' needs package '{entry}', which is missing.", fullPath);
            return;
        }
        // Dune 2000's resource archives (SOUND.RS).
        if (fullPath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(fullPath))
                fileSystem.Mount(new RsArchive(fullPath));
            else if (!optional)
                throw new FileNotFoundException($"Mod '{mod.Id}' needs package '{entry}', which is missing.", fullPath);
            return;
        }
        if (fullPath.EndsWith(".big", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(fullPath))
                fileSystem.Mount(new BigArchive(fullPath));
            else if (!optional)
                throw new FileNotFoundException($"Mod '{mod.Id}' needs package '{entry}', which is missing. Run the content importer first.", fullPath);
        }
        else
        {
            if (Directory.Exists(fullPath))
                fileSystem.Mount(new FolderPackage(fullPath, prefix));
            else if (!optional)
                throw new DirectoryNotFoundException($"Mod '{mod.Id}' needs folder '{entry}', which is missing.");
        }
    }
}
