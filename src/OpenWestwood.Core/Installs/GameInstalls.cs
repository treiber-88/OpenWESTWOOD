using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenWestwood.Core.Installs;

/// <summary>The original games OpenWESTWOOD needs the player to own for some of its content.</summary>
public enum OriginalGame
{
    ZeroHour,
    KanesWrath,
}

/// <summary>
/// Whether OpenWESTWOOD has Zero Hour's and Kane's Wrath's artwork (<see cref="Has"/>: their files are in its own content
/// folder, put there by <see cref="ContentInstaller"/>), and where the player's copies of the games are to install it
/// from (<see cref="ZeroHourSources"/>...: any copy will do, an install, a copy on another drive, a disc).
///
/// A folder counts only if the game's own data files are really in it: registry entries are often stale (a game
/// moved, a drive unplugged, an uninstall that left keys behind), so every place they name is checked on disk.
/// Places looked at, in order:
///   1. what the player set: command line (--zh-path / --kw-path), environment (OPENWESTWOOD_ZH_PATH /
///      OPENWESTWOOD_KW_PATH) or installs.ini in the user data folder;
///   2. Windows: every install path under the EA / Origin registry keys and the uninstall list;
///   3. every Steam library (libraryfolders.vdf), including Proton prefixes on Linux;
///   4. the usual EA app / Origin folders on each drive, and Wine, Lutris, Bottles, Heroic, CrossOver and Whisky
///      prefixes on Linux and macOS.
/// </summary>
public static class GameInstalls
{
    /// <summary>Zero Hour's own folder (INIZH.big...).</summary>
    public static string? ZeroHourFolder { get; private set; }

    /// <summary>The original Generals folder Zero Hour runs on (INI.big...). Part of every Zero Hour release.</summary>
    public static string? GeneralsFolder { get; private set; }

    /// <summary>Kane's Wrath's folder (Core/1.0/StaticStream.big...).</summary>
    public static string? KanesWrathFolder { get; private set; }

    /// <summary>Every Zero Hour / Generals / Kane's Wrath data folder found (the installer takes each file from the first one with it unmodified).</summary>
    public static IReadOnlyList<string> ZeroHourSources { get; private set; } = [];
    public static IReadOnlyList<string> GeneralsSources { get; private set; } = [];
    public static IReadOnlyList<string> KanesWrathSources { get; private set; } = [];

    static string? contentRoot;

    /// <summary>The OpenWESTWOOD folder (the nearest parent of the program, or the current folder, with mods/zh/mod.yaml).</summary>
    public static string ContentRoot
    {
        get
        {
            if (contentRoot != null)
                return contentRoot;
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                for (string? dir = start; dir != null; dir = Path.GetDirectoryName(dir))
                    if (File.Exists(Path.Combine(dir, "mods", "zh", "mod.yaml")))
                        return contentRoot = dir;
            return contentRoot = Directory.GetCurrentDirectory();
        }
        set => contentRoot = value;
    }

    /// <summary>OpenWESTWOOD's copy of a game's files (content/kaneswrath...), once installed.</summary>
    public static string? ContentFolder(OriginalGame game) =>
        Has(game) ? ContentInstaller.ContentFolder(ContentInstaller.FoldersOf(game)[0]) : null;

    /// <summary>What was checked and why, for --detect-installs.</summary>
    public static IReadOnlyList<string> Log => log;

    static readonly List<string> log = [];
    static bool detected;
    static readonly HashSet<OriginalGame> pretendMissing = [];

    /// <summary>The store pages for the Ultimate Collection (both games are in it).</summary>
    public const string SteamUrl = "https://store.steampowered.com/bundle/39394/";
    public const string EaUrl = "https://www.ea.com/games/command-and-conquer/command-and-conquer-the-ultimate-collection";

    /// <summary>The game's artwork is installed in OpenWESTWOOD's content folder (it unlocks the game's factions, maps...).</summary>
    public static bool Has(OriginalGame game) => !pretendMissing.Contains(game) && ContentInstaller.IsInstalled(game);

    public static string DisplayName(OriginalGame game) => game switch
    {
        OriginalGame.ZeroHour => "Command & Conquer Generals Zero Hour",
        OriginalGame.KanesWrath => "Command & Conquer 3: Kane's Wrath",
        _ => game.ToString(),
    };

    public static IEnumerable<OriginalGame> Missing() => Enum.GetValues<OriginalGame>().Where(g => !Has(g));

    /// <summary>The user data folder (settings, installs.ini).</summary>
    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenWESTWOOD");

    /// <summary>
    /// Reads the command line once: --zh-path DIR, --kw-path DIR (a folder to use), --pretend-missing zh,kw
    /// (to try the game as a player without them). Call before anything asks <see cref="Has"/>.
    /// </summary>
    public static void Configure(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--zh-path": overrideZh = args[i + 1]; break;
                case "--kw-path": overrideKw = args[i + 1]; break;
                case "--pretend-missing":
                    foreach (var part in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (part.Equals("zh", StringComparison.OrdinalIgnoreCase) || part.Equals("all", StringComparison.OrdinalIgnoreCase))
                            pretendMissing.Add(OriginalGame.ZeroHour);
                        if (part.Equals("kw", StringComparison.OrdinalIgnoreCase) || part.Equals("all", StringComparison.OrdinalIgnoreCase))
                            pretendMissing.Add(OriginalGame.KanesWrath);
                    }
                    break;
            }
        }
        Reset();
    }

    /// <summary>Uses these folders instead of searching (tests and tools). Null leaves a game to the search.</summary>
    public static void Override(string? zeroHour = null, string? kanesWrath = null)
    {
        if (zeroHour != null) overrideZh = zeroHour;
        if (kanesWrath != null) overrideKw = kanesWrath;
        Reset();
    }

    static string? overrideZh, overrideKw;

    /// <summary>Back to searching for both games.</summary>
    public static void ClearOverrides()
    {
        overrideZh = overrideKw = null;
        Reset();
    }

    static void Reset()
    {
        lock (log)
        {
            detected = false;
            ZeroHourFolder = GeneralsFolder = KanesWrathFolder = null;
            ZeroHourSources = GeneralsSources = KanesWrathSources = [];
            log.Clear();
        }
    }

    /// <summary>Searches again (a disc put in, a drive plugged in).</summary>
    public static void Rescan()
    {
        Reset();
        Detect();
    }

    /// <summary>Searches once (later calls return at once).</summary>
    public static void Detect()
    {
        lock (log)
        {
            if (detected)
                return;
            detected = true;
            try
            {
                Search();
            }
            catch (Exception e)
            {
                log.Add($"search failed: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- validation

    /// <summary>Zero Hour's data: its own archives.</summary>
    public static bool IsZeroHourFolder(string dir) =>
        HasFiles(dir, "INIZH.big", "W3DZH.big", "WindowZH.big", "MapsZH.big");

    /// <summary>The original Generals data Zero Hour needs.</summary>
    public static bool IsGeneralsFolder(string dir) =>
        HasFiles(dir, "INI.big", "W3D.big", "Window.big", "Textures.big");

    /// <summary>
    /// Kane's Wrath's data. Tiberium Wars has the same folder layout, so the folder must also be Kane's Wrath's: its
    /// program (cnc3ep1.exe) is there, or the folder is named for it (every release's folder says "Kane").
    /// </summary>
    public static bool IsKanesWrathFolder(string dir)
    {
        if (!HasFiles(dir, "Core/1.0/StaticStream.big", "Core/1.0/GlobalStream.big", "Core/1.0/Terrain.big", "Meta/1.0/maps.big"))
            return false;
        if (FindFile(dir, "cnc3ep1.exe") != null)
            return true;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        return name.Contains("kane", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasFiles(string dir, params string[] files)
    {
        try
        {
            return Directory.Exists(dir) && files.All(f => FindFile(dir, f) != null);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A file below a folder by a relative path, matching each part's name in any case (Linux and macOS file systems
    /// can be case-sensitive; Wine and Steam keep the original names, which vary between releases).
    /// </summary>
    public static string? FindFile(string dir, string relative)
    {
        string current = dir;
        var parts = relative.Split('/', '\\');
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            string exact = Path.Combine(current, parts[i]);
            if (last ? File.Exists(exact) : Directory.Exists(exact))
            {
                current = exact;
                continue;
            }
            if (!Directory.Exists(current))
                return null;
            string? match = null;
            try
            {
                var entries = last ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current);
                match = entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
            }
            if (match == null)
                return null;
            current = match;
        }
        return current;
    }

    // ---------------------------------------------------------------- search

    static void Search()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = path.Trim().Trim('"');
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }
            if (seen.Add(Path.TrimEndingDirectorySeparator(path)))
            {
                candidates.Add(path);
                log.Add($"candidate ({source}): {path}{(Directory.Exists(path) ? "" : "  [missing]")}");
            }
        }

        // 1. The player's own settings: these win, and are not mixed with the search.
        var settings = ReadSettingsFile();
        string? zhSetting = overrideZh ?? Environment.GetEnvironmentVariable("OPENWESTWOOD_ZH_PATH") ?? settings.GetValueOrDefault("ZeroHour");
        string? kwSetting = overrideKw ?? Environment.GetEnvironmentVariable("OPENWESTWOOD_KW_PATH") ?? settings.GetValueOrDefault("KanesWrath");

        // 2-4. Everywhere else.
        if (OperatingSystem.IsWindows())
            AddRegistryCandidates(Add);
        foreach (string library in SteamLibraries())
        {
            foreach (string game in SafeDirectories(Path.Combine(library, "steamapps", "common")))
                Add(game, "Steam");
            // Proton prefixes (the EA app run through Steam installs its games inside one).
            foreach (string prefix in SafeDirectories(Path.Combine(library, "steamapps", "compatdata")))
                foreach (string folder in StoreFolders(Path.Combine(prefix, "pfx", "drive_c")))
                    Add(folder, "Proton prefix");
        }
        foreach (string drive in DriveRoots())
            foreach (string folder in StoreFolders(drive))
                Add(folder, "store folder");
        foreach (string prefix in WinePrefixes())
            foreach (string folder in StoreFolders(Path.Combine(prefix, "drive_c")))
                Add(folder, "Wine prefix");

        // 5. Any copy anywhere: a disc, a USB stick, a mounted image, a copy on another drive. Disc, removable and
        // network drives are looked through deeper; a hard drive down to its first two levels of folders.
        var deep = new List<string>();
        foreach (var (root, removable) in AllDriveRoots())
            if (removable)
                deep.Add(root);
            else
                Add(root, "drive");

        if (zhSetting != null)
            log.Add($"Zero Hour folder set by the player: {zhSetting}");
        var zhPlaces = zhSetting != null ? new List<string> { zhSetting } : candidates;
        ZeroHourSources = FindAllBelow(zhPlaces, IsZeroHourFolder, 2).Concat(FindAllBelow(deep, IsZeroHourFolder, 4)).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var generals = new List<string>();
        foreach (string zh in ZeroHourSources)
            if (FindGenerals(zh, zhPlaces) is { } g)
                generals.Add(g);
        GeneralsSources = generals.Concat(FindAllBelow(zhPlaces, IsGeneralsFolder, 2)).Concat(FindAllBelow(deep, IsGeneralsFolder, 4)).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        KanesWrathSources = (kwSetting != null ? FindAllBelow([kwSetting], IsKanesWrathFolder, 2) : FindAllBelow(candidates, IsKanesWrathFolder, 2))
            .Concat(FindAllBelow(deep, IsKanesWrathFolder, 4)).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ZeroHourFolder = ZeroHourSources.FirstOrDefault();
        GeneralsFolder = GeneralsSources.FirstOrDefault();
        KanesWrathFolder = KanesWrathSources.FirstOrDefault();
        foreach (string f in ZeroHourSources.Skip(1).Concat(GeneralsSources.Skip(1)).Concat(KanesWrathSources.Skip(1)))
            log.Add($"another copy: {f}");

        log.Add($"Zero Hour: {ZeroHourFolder ?? "not found"}");
        log.Add($"Generals (for Zero Hour): {GeneralsFolder ?? "not found"}");
        log.Add($"Kane's Wrath: {KanesWrathFolder ?? "not found"}");
    }

    /// <summary>Generals' data for a Zero Hour folder: in it, beside it, or anywhere else found.</summary>
    static string? FindGenerals(string zeroHour, IEnumerable<string> candidates)
    {
        var near = new List<string> { zeroHour };
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(zeroHour)) is { } parent)
            near.Add(parent);
        return FindBelow(near, IsGeneralsFolder, 1) ?? FindBelow(candidates, IsGeneralsFolder, 2);
    }

    /// <summary>Every folder (each candidate, then its sub-folders down to a depth) that passes the test.</summary>
    static List<string> FindAllBelow(IEnumerable<string> roots, Func<string, bool> test, int depth)
    {
        var found = new List<string>();
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            var level = new List<string> { root };
            for (int d = 0; d <= depth && level.Count > 0; d++)
            {
                var next = new List<string>();
                foreach (string dir in level)
                {
                    if (test(dir))
                    {
                        string clean = Path.TrimEndingDirectorySeparator(dir);
                        if (!found.Contains(clean, StringComparer.OrdinalIgnoreCase))
                            found.Add(clean);
                        continue;
                    }
                    next.AddRange(SafeDirectories(dir).Where(sub => !SkippedFolders.Contains(Path.GetFileName(sub))));
                }
                level = next;
            }
        }
        return found;
    }

    /// <summary>System folders not worth looking through for a game.</summary>
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "$WinREAgent", "proc", "sys", "dev",
        "node_modules", ".git",
    };

    /// <summary>Every drive: its root, and whether it is a disc, removable or network drive (looked through deeper).</summary>
    static IEnumerable<(string Root, bool Removable)> AllDriveRoots()
    {
        if (!OperatingSystem.IsWindows())
            return DriveRoots().Select(r => (r, true));
        try
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => (d.RootDirectory.FullName, d.DriveType is DriveType.CDRom or DriveType.Removable or DriveType.Network))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The first folder (each candidate, then its sub-folders down to a depth) that passes the test.</summary>
    static string? FindBelow(IEnumerable<string> roots, Func<string, bool> test, int depth)
    {
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            var level = new List<string> { root };
            for (int d = 0; d <= depth && level.Count > 0; d++)
            {
                foreach (string dir in level)
                    if (test(dir))
                        return dir;
                level = level.SelectMany(SafeDirectories).ToList();
            }
        }
        return null;
    }

    static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>installs.ini in the user data folder: "ZeroHour = C:\Games\..." and "KanesWrath = ...".</summary>
    static Dictionary<string, string> ReadSettingsFile()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string file = Path.Combine(UserDataFolder, "installs.ini");
        try
        {
            if (!File.Exists(file))
                return values;
            foreach (string line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.TrimStart().StartsWith(';'))
                    values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
            }
        }
        catch (Exception e)
        {
            log.Add($"could not read {file}: {e.Message}");
        }
        return values;
    }

    // ---------------------------------------------------------------- Windows registry

    [SupportedOSPlatform("windows")]
    static void AddRegistryCandidates(Action<string?, string> add)
    {
        // The keys EA's installers (retail, The First Decade, Origin, the EA app) write: any install path below them.
        string[] trees =
        [
            @"SOFTWARE\Electronic Arts",
            @"SOFTWARE\EA Games",
            @"SOFTWARE\Origin Games",
        ];
        string[] valueNames = ["InstallPath", "Install Dir", "InstallLocation", "Install Path", "InstallDir"];
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (string tree in trees)
                        using (var key = baseKey.OpenSubKey(tree))
                            if (key != null)
                                WalkKey(key, 0, k =>
                                {
                                    foreach (string name in valueNames)
                                        if (k.GetValue(name) is string path)
                                            add(path, $"registry {k.Name}");
                                });
                    // The uninstall list: Steam ("Steam App 2732960") and EA app entries say where a game is.
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall != null)
                        foreach (string sub in uninstall.GetSubKeyNames())
                        {
                            using var entry = uninstall.OpenSubKey(sub);
                            string display = entry?.GetValue("DisplayName") as string ?? "";
                            if (Regex.IsMatch(display, "zero hour|generals|kane|tiberium wars|command (&|and) conquer", RegexOptions.IgnoreCase))
                                add(entry?.GetValue("InstallLocation") as string, $"uninstall entry \"{display}\"");
                        }
                }
                catch (Exception e)
                {
                    log.Add($"registry {hive}/{view}: {e.Message}");
                }
            }
    }

    [SupportedOSPlatform("windows")]
    static void WalkKey(RegistryKey key, int depth, Action<RegistryKey> visit)
    {
        visit(key);
        if (depth >= 4)
            return;
        foreach (string name in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(name);
                if (sub != null)
                    WalkKey(sub, depth + 1, visit);
            }
            catch (Exception)
            {
            }
        }
    }

    // ---------------------------------------------------------------- Steam, drives, Wine

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, path, value) in new[]
            {
                (RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(path);
                    if (key?.GetValue(value) is string s)
                        roots.Add(s.Replace('/', '\\'));
                }
                catch (Exception)
                {
                }
            }
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (pf86.Length > 0)
                roots.Add(Path.Combine(pf86, "Steam"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            roots.Add(Path.Combine(Home, "Library", "Application Support", "Steam"));
        }
        else
        {
            roots.Add(Path.Combine(Home, ".steam", "steam"));
            roots.Add(Path.Combine(Home, ".steam", "root"));
            roots.Add(Path.Combine(Home, ".local", "share", "Steam"));
            roots.Add(Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            roots.Add(Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam"));
        }
        return roots.Where(Directory.Exists);
    }

    /// <summary>Every Steam library: each Steam folder and the libraries its libraryfolders.vdf lists.</summary>
    static IEnumerable<string> SteamLibraries()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var libraries = new List<string>();
        foreach (string root in SteamRoots().Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).Distinct(comparer))
        {
            libraries.Add(root);
            foreach (string vdf in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
            {
                try
                {
                    if (!File.Exists(vdf))
                        continue;
                    string text = File.ReadAllText(vdf);
                    var paths = Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"");
                    foreach (Match m in paths)
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    // Old format (no "path" keys): "1"  "D:\\SteamLibrary"
                    if (paths.Count == 0)
                        foreach (Match m in Regex.Matches(text, "^\\s*\"\\d+\"\\s+\"([^\"]+)\"", RegexOptions.Multiline))
                            libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (Exception e)
                {
                    log.Add($"could not read {vdf}: {e.Message}");
                }
            }
        }
        libraries = libraries.Select(l => Path.TrimEndingDirectorySeparator(l)).Distinct(comparer).ToList();
        foreach (string library in libraries)
            log.Add($"Steam library: {library}{(Directory.Exists(library) ? "" : "  [missing]")}");
        return libraries.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Drive roots to look in for store folders: each ready local drive on Windows; on Linux the mounted drives (a
    /// Windows partition of a dual-boot PC under /mnt, /media/USER or /run/media/USER); on macOS /Volumes.
    /// </summary>
    static IEnumerable<string> DriveRoots()
    {
        if (OperatingSystem.IsMacOS())
            return SafeDirectories("/Volumes");
        if (!OperatingSystem.IsWindows())
        {
            string user = Environment.UserName;
            return SafeDirectories("/mnt")
                .Concat(SafeDirectories(Path.Combine("/media", user)))
                .Concat(SafeDirectories(Path.Combine("/run/media", user)))
                .Concat(SafeDirectories("/media"))
                .ToList();
        }
        try
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The folders EA's stores and installers put games in, below a drive root (a Windows drive or a Wine prefix's
    /// drive_c): each game folder in them is a candidate.
    /// </summary>
    static IEnumerable<string> StoreFolders(string driveRoot)
    {
        string[] parents =
        [
            @"Program Files (x86)\Origin Games",
            @"Program Files\Origin Games",
            @"Program Files\EA Games",
            @"Program Files (x86)\EA Games",
            @"Program Files (x86)\Electronic Arts",
            @"Program Files\Electronic Arts",
            @"Origin Games",
            @"EA Games",
            @"Games",
            @"SteamLibrary\steamapps\common",
            @"Program Files (x86)\Steam\steamapps\common",
        ];
        foreach (string parent in parents)
        {
            string? dir = FindDirectory(driveRoot, parent);
            if (dir == null)
                continue;
            foreach (string game in SafeDirectories(dir))
                yield return game;
        }
    }

    static string? FindDirectory(string root, string relative)
    {
        string current = root;
        foreach (string part in relative.Split('\\'))
        {
            string exact = Path.Combine(current, part);
            if (Directory.Exists(exact))
            {
                current = exact;
                continue;
            }
            string? match = SafeDirectories(current).FirstOrDefault(d => string.Equals(Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                return null;
            current = match;
        }
        return current;
    }

    /// <summary>Wine prefixes on Linux and macOS (default, Lutris, Bottles, Heroic, CrossOver, Whisky).</summary>
    static IEnumerable<string> WinePrefixes()
    {
        if (OperatingSystem.IsWindows())
            yield break;
        string? winePrefix = Environment.GetEnvironmentVariable("WINEPREFIX");
        if (!string.IsNullOrEmpty(winePrefix))
            yield return winePrefix;
        yield return Path.Combine(Home, ".wine");
        foreach (string parent in new[]
        {
            Path.Combine(Home, "Games"),                                                        // Lutris
            Path.Combine(Home, "Games", "Heroic", "Prefixes"),
            Path.Combine(Home, ".local", "share", "bottles", "bottles"),
            Path.Combine(Home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles"),
            Path.Combine(Home, ".local", "share", "lutris", "prefixes"),
            Path.Combine(Home, "Library", "Application Support", "CrossOver", "Bottles"),
            Path.Combine(Home, "Library", "Containers", "com.isaacmarovitz.Whisky", "Bottles"),
        })
        {
            foreach (string prefix in SafeDirectories(parent))
            {
                yield return prefix;
                // Lutris keeps a game's prefix one level further down at times.
                foreach (string inner in SafeDirectories(prefix))
                    if (Directory.Exists(Path.Combine(inner, "drive_c")))
                        yield return inner;
            }
        }
    }
}
