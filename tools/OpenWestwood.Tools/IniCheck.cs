using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Ini;

namespace OpenWestwood.Tools;

/// <summary>Parses every INI file in Data/INI and prints a summary of block types, so parser problems show up quickly.</summary>
static class IniCheck
{
    public static int Run(VirtualFileSystem fileSystem)
    {
        int failures = 0;
        var blockCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in fileSystem.List("Data/INI", ".ini", recursive: true))
        {
            try
            {
                var document = IniParser.Parse(fileSystem.ReadAllText(path), path);
                foreach (var block in document.Blocks)
                    blockCounts[block.Type] = blockCounts.GetValueOrDefault(block.Type) + 1;
            }
            catch (IniException e)
            {
                failures++;
                Console.WriteLine("FAIL " + e.Message);
            }
        }

        foreach (var (type, count) in blockCounts)
            Console.WriteLine($"{count,6}  {type}");
        Console.WriteLine(failures == 0 ? "All INI files parsed." : $"{failures} file(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>ini-blocks Data/INI/Object/X.ini: the top-level blocks the parser finds in one file (type, name, lines).</summary>
    public static int Blocks(VirtualFileSystem fileSystem, string[] args)
    {
        string path = args.FirstOrDefault() ?? "";
        var document = IniParser.Parse(fileSystem.ReadAllText(path), path);
        foreach (var block in document.Blocks)
            Console.WriteLine($"{block.Type} {block.Name}");
        return 0;
    }
}
