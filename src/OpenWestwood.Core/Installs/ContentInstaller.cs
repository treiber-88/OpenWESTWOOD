using System.Security.Cryptography;

namespace OpenWestwood.Core.Installs;

/// <summary>One original game file OpenWESTWOOD uses: where it goes (content/Folder/Path), its size and SHA-256.</summary>
public sealed record OriginalFile(string Folder, string Path, long Size, string Sha256);

/// <summary>How an install went.</summary>
public sealed record InstallResult(bool Success, string Message, IReadOnlyList<string> Problems);

/// <summary>
/// Copies the original games' artwork OpenWESTWOOD needs into its own content folder (content/zerohour and
/// content/generals for Zero Hour, content/kaneswrath for Kane's Wrath), from the player's own copy of the game,
/// wherever on the machine it is: an EA app / Origin / Steam install, a copy on another drive, a disc or a mounted
/// image. A game counts as there (<see cref="GameInstalls.Has"/>) once its files are in the content folder; the
/// original isn't needed after that.
///
/// What is copied is mods/zh/original-files.txt: each file with its size and SHA-256, taken from verified original
/// files. Only matching files are copied, so a modified file (a community patch's) is never brought in; each file
/// comes from whichever copy found has it unmodified.
/// </summary>
public static class ContentInstaller
{
    static List<OriginalFile>? manifest;

    /// <summary>The list of original files (mods/zh/original-files.txt).</summary>
    public static IReadOnlyList<OriginalFile> Manifest => manifest ??= Load();

    static List<OriginalFile> Load()
    {
        var list = new List<OriginalFile>();
        string file = System.IO.Path.Combine(GameInstalls.ContentRoot, "mods", "zh", "original-files.txt");
        if (!File.Exists(file))
            return list;
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith('#') || line.Split('|') is not [var folder, var path, var size, var sha] || !long.TryParse(size, out long bytes))
                continue;
            list.Add(new OriginalFile(folder, path, bytes, sha.Trim()));
        }
        return list;
    }

    /// <summary>The content folders a game's files go into.</summary>
    public static string[] FoldersOf(OriginalGame game) => game switch
    {
        OriginalGame.ZeroHour => ["zerohour", "generals"],
        OriginalGame.KanesWrath => ["kaneswrath"],
        _ => [],
    };

    public static IEnumerable<OriginalFile> FilesOf(OriginalGame game) => Manifest.Where(f => FoldersOf(game).Contains(f.Folder));

    /// <summary>OpenWESTWOOD's own copy of a content folder (content/zerohour...).</summary>
    public static string ContentFolder(string folder) => System.IO.Path.Combine(GameInstalls.ContentRoot, "content", folder);

    static string Target(OriginalFile f) => System.IO.Path.Combine(ContentFolder(f.Folder), f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    static bool IsInPlace(OriginalFile f) =>
        (GameInstalls.FindFile(ContentFolder(f.Folder), f.Path) ?? Target(f)) is var path && File.Exists(path) && new FileInfo(path).Length == f.Size;

    /// <summary>All of a game's files are in OpenWESTWOOD's content folder (checked by size: quick enough for every start).</summary>
    public static bool IsInstalled(OriginalGame game)
    {
        var files = FilesOf(game).ToList();
        return files.Count > 0 && files.All(IsInPlace);
    }

    /// <summary>Where the player's copies of a content folder's files are (found by <see cref="GameInstalls.Detect"/>).</summary>
    static IEnumerable<string> SourcesOf(string folder)
    {
        GameInstalls.Detect();
        var sources = folder switch
        {
            "zerohour" => GameInstalls.ZeroHourSources,
            "generals" => GameInstalls.GeneralsSources,
            "kaneswrath" => GameInstalls.KanesWrathSources,
            _ => [],
        };
        string own = System.IO.Path.GetFullPath(ContentFolder(folder));
        return sources.Where(s => !System.IO.Path.GetFullPath(s).TrimEnd(System.IO.Path.DirectorySeparatorChar).Equals(own.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What an install would do: each missing file with the copies that have one of the right size, and the files no copy has.</summary>
    public static (List<(OriginalFile File, List<string> From)> Copy, List<OriginalFile> Unavailable) Plan(OriginalGame game)
    {
        var copy = new List<(OriginalFile, List<string>)>();
        var unavailable = new List<OriginalFile>();
        foreach (var file in FilesOf(game).Where(f => !IsInPlace(f)))
        {
            var from = SourcesOf(file.Folder)
                .Select(s => GameInstalls.FindFile(s, file.Path))
                .Where(p => p != null && new FileInfo(p).Length == file.Size)
                .Select(p => p!).ToList();
            if (from.Count == 0)
                unavailable.Add(file);
            else
                copy.Add((file, from));
        }
        return (copy, unavailable);
    }

    /// <summary>A copy of the game was found that has everything still needed (unchecked: the hashes are checked as it copies).</summary>
    public static bool CanInstall(OriginalGame game) => !IsInstalled(game) && Plan(game) is { Unavailable.Count: 0, Copy.Count: > 0 };

    /// <summary>Where the files would come from (for the player: "found in ...").</summary>
    public static IReadOnlyList<string> SourceFolders(OriginalGame game) =>
        FoldersOf(game).SelectMany(folder => SourcesOf(folder).Where(source => FilesOf(game).Any(f => f.Folder == folder
            && GameInstalls.FindFile(source, f.Path) is { } path && new FileInfo(path).Length == f.Size)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static long BytesToCopy(OriginalGame game) => FilesOf(game).Where(f => !IsInPlace(f)).Sum(f => f.Size);

    /// <summary>
    /// Copies what is missing. Each file is copied from the first copy that has it unmodified (its SHA-256 checked as it
    /// is copied; a modified one is thrown away and the next copy tried), to a ".part" file renamed when complete.
    /// Progress: bytes done, bytes in all, the file being copied.
    /// </summary>
    public static InstallResult Install(OriginalGame game, Action<long, long, string>? progress = null, CancellationToken cancel = default)
    {
        var (plan, unavailable) = Plan(game);
        string name = GameInstalls.DisplayName(game);
        if (unavailable.Count > 0)
            return new InstallResult(false,
                $"No copy of {name} found on this computer has all of its files ({unavailable.Count} missing). Insert the game's disc, plug in the drive it is on, or set its folder in installs.ini.",
                unavailable.Select(f => $"{f.Folder}/{f.Path}").ToList());
        long total = plan.Sum(p => p.File.Size), done = 0;
        var problems = new List<string>();
        var buffer = new byte[1 << 20];
        foreach (var (file, from) in plan)
        {
            cancel.ThrowIfCancellationRequested();
            string target = Target(file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            bool copied = false;
            foreach (string source in from)
            {
                string part = target + ".part";
                try
                {
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                    using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    using (var sha = SHA256.Create())
                    {
                        int read;
                        long fileDone = 0;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            sha.TransformBlock(buffer, 0, read, null, 0);
                            fileDone += read;
                            progress?.Invoke(done + fileDone, total, $"{file.Folder}/{file.Path}");
                        }
                        sha.TransformFinalBlock([], 0, 0);
                        if (!Convert.ToHexString(sha.Hash!).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add($"{source}: modified (not the original file), not copied");
                            output.Close();
                            File.Delete(part);
                            continue;
                        }
                    }
                    File.Move(part, target, overwrite: true);
                    copied = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(part); } catch (IOException) { }
                    throw;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{source}: {e.Message}");
                    try { File.Delete(part); } catch (IOException) { }
                }
            }
            done += file.Size;
            progress?.Invoke(done, total, $"{file.Folder}/{file.Path}");
            if (!copied)
                problems.Add($"{file.Folder}/{file.Path}: no unmodified copy found");
        }
        bool ok = IsInstalled(game);
        return new InstallResult(ok,
            ok ? $"{name}'s artwork is installed." : $"{name} could not be installed completely: some of its files are modified in every copy found (for example by a community patch). The game's disc or a fresh install has the originals.",
            problems);
    }
}
