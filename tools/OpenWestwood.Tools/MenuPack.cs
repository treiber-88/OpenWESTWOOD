using OpenWestwood.Core.FileSystem;

namespace OpenWestwood.Tools;

/// <summary>
/// build-menu-pack &lt;trace.txt&gt;...: makes mods/zh/menu-pack, the Zero Hour files OpenWESTWOOD needs to run without
/// Zero Hour installed (its menus, fonts, in-game bar, shared effects). The files are the ones the game opened from Zero
/// Hour's and Generals' archives while playing only the other games (OpenWESTWOOD --pretend-missing zh --file-trace
/// trace.txt, with OPENWESTWOOD_MOUNT_WITHHELD=1), plus every menu layout (Window/**.wnd). Each is copied as the game
/// reads it (the first archive that has it wins, as in the game) from OpenWESTWOOD's verified copies in content/.
/// </summary>
static class MenuPack
{
    public static int Build(string root, VirtualFileSystem fileSystem, string[] traces)
    {
        if (traces.Length == 0)
        {
            Console.WriteLine("build-menu-pack <trace.txt>...");
            return 1;
        }
        // Zero Hour's and Generals' own archives (content/zerohour, content/generals).
        string zh = Path.GetFullPath(Path.Combine(root, "content", "zerohour")), gen = Path.GetFullPath(Path.Combine(root, "content", "generals"));
        var zeroHourPackages = fileSystem.Packages.Where(p => p is BigArchive && (File.Exists(Path.Combine(zh, p.Name)) || File.Exists(Path.Combine(gen, p.Name)))
            || p is FolderPackage && Path.GetFullPath(p.Name).StartsWith(zh, StringComparison.OrdinalIgnoreCase)).ToHashSet();
        if (zeroHourPackages.Count == 0)
        {
            Console.WriteLine("Zero Hour's files are not in content/: the pack is made from them.");
            return 1;
        }
        var paths = new SortedSet<string>(PathUtil.Comparer);
        foreach (string trace in traces)
            foreach (string line in File.ReadLines(trace))
                if (line.Split('|', 2) is [var package, var path] && package != "MISSING")
                    paths.Add(PathUtil.Normalize(path));
        // Every menu layout (small text files): a menu the traces didn't open still works.
        foreach (string wnd in fileSystem.List("Window", ".wnd", recursive: true))
            paths.Add(wnd);

        // The interface image sheets the menu layouts and the program name (mapped images: "IMAGE: name" in the .wnd
        // files, quoted names in src/): every menu's pictures, also for screens and states the traces didn't show. As
        // the game finds a sheet: the English folder first, .tga or .dds.
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rulesFile in Directory.EnumerateFiles(Path.Combine(root, "mods", "zh", "rules", "ui", "mapped-images"), "*.yaml", SearchOption.AllDirectories))
        {
            string? image = null;
            foreach (string line in File.ReadLines(rulesFile))
            {
                if (line.StartsWith("  ") && !line.StartsWith("   ") && line.TrimEnd().EndsWith(':'))
                    image = line.Trim().TrimEnd(':');
                else if (image != null && line.Trim() is var t && t.StartsWith("Texture:", StringComparison.Ordinal))
                    images[image] = t[8..].Trim();
            }
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string wnd in fileSystem.List("Window", ".wnd", recursive: true))
            foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex(@"IMAGE:\s*([A-Za-z0-9_\-]+)").Matches(fileSystem.ReadAllText(wnd)))
                used.Add(m.Groups[1].Value);
        foreach (string source in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex(@"""([A-Za-z0-9_\-]+)""").Matches(File.ReadAllText(source)))
                used.Add(m.Groups[1].Value);
        foreach (string name in used)
            if (images.TryGetValue(name, out string? texture))
                foreach (string candidate in new[] { "Data/English/Art/Textures/" + texture, "Data/English/Art/Textures/" + Path.ChangeExtension(texture, ".dds"),
                             "Art/Textures/" + texture, "Art/Textures/" + Path.ChangeExtension(texture, ".dds") })
                    if (fileSystem.Exists(candidate))
                    {
                        paths.Add(PathUtil.Normalize(candidate));
                        break;
                    }

        string pack = Path.Combine(root, "mods", "zh", "menu-pack");
        if (Directory.Exists(pack))
            Directory.Delete(pack, recursive: true);
        Directory.CreateDirectory(pack);
        var listed = new List<string>();
        long bytes = 0;
        foreach (string path in paths)
        {
            if (fileSystem.Provider(path) is not { } provider || !zeroHourPackages.Contains(provider))
                continue;
            byte[] data = fileSystem.ReadAllBytes(path);
            string target = Path.Combine(pack, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
            listed.Add($"{path}|{Path.GetFileName(provider.Name)}|{data.Length}");
            bytes += data.Length;
        }
        File.WriteAllLines(Path.Combine(pack, "menu-pack.txt"),
            new[] { "# OpenWESTWOOD's Zero Hour menu pack: path|archive it came from|size (made by OpenWestwood.Tools build-menu-pack)." }.Concat(listed));
        Console.WriteLine($"{listed.Count} files, {bytes / 1e6:0.0} MB in {pack}");
        foreach (var group in listed.GroupBy(l => Path.GetExtension(l.Split('|')[0]).ToLowerInvariant()).OrderByDescending(g => g.Sum(l => long.Parse(l.Split('|')[2]))))
            Console.WriteLine($"  {group.Key,-6} {group.Count(),5} files {group.Sum(l => long.Parse(l.Split('|')[2])) / 1e6,8:0.0} MB");
        return 0;
    }
}
