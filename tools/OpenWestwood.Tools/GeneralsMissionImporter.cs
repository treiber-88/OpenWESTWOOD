using OpenWestwood.Core.FileSystem;
using OpenWestwood.Core.Rules;
using OpenWestwood.Formats.Ini;

namespace OpenWestwood.Tools;

/// <summary>
/// The original Generals campaign (before Zero Hour), playable beside Zero Hour's: its campaign list and the
/// definitions its missions need that Zero Hour's data dropped (mission speech, intro movies...). Zero Hour's
/// INIZH.big shadows Generals' INI.big file by file, so these are read from INI.big itself, and only blocks
/// Zero Hour does not define are written (to mods/zh/rules/generals). The campaigns get new names
/// (GENERALS_USA...) because Zero Hour reuses USA, GLA and China for its own.
/// </summary>
static partial class RulesImporter
{
    static readonly string[] GeneralsMissionFiles = ["Speech", "Video", "Music", "SoundEffects", "Voice", "MiscAudio"];

    public static int RunGeneralsMissions(string root, VirtualFileSystem zeroHour)
    {
        string bigPath = Path.Combine(root, "content", "generals", "INI.big");
        if (!File.Exists(bigPath))
        {
            Console.Error.WriteLine($"{bigPath} not found.");
            return 1;
        }
        using var generals = new VirtualFileSystem();
        generals.Mount(new BigArchive(bigPath));

        var outputs = new SortedDictionary<string, RuleNode>(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (string file in GeneralsMissionFiles)
        {
            string path = $"Data/INI/{file}.ini";
            if (!generals.Exists(path))
                continue;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zeroHour.Exists(path))
                foreach (var block in IniParser.Parse(zeroHour.ReadAllText(path), path).Blocks)
                    known.Add(block.Type + " " + CleanValue(block.Name));
            foreach (var block in IniParser.Parse(generals.ReadAllText(path), path).Blocks)
                if (block.Name.Length > 0 && known.Add(block.Type + " " + CleanValue(block.Name)))
                {
                    AddTopLevelBlock(OutputFile(outputs, OutputPathFor(file + ".ini")), block);
                    added++;
                }
        }

        // The campaigns, renamed; Training is the same map in both games and Zero Hour already has it.
        var campaigns = OutputFile(outputs, "campaigns.yaml");
        foreach (var block in IniParser.Parse(generals.ReadAllText("Data/INI/Campaign.ini"), "Data/INI/Campaign.ini").Blocks)
        {
            string name = CleanValue(block.Name);
            if (!block.Type.Equals("Campaign", StringComparison.OrdinalIgnoreCase) || name.Equals("TRAINING", StringComparison.OrdinalIgnoreCase))
                continue;
            AddNamed(Section(campaigns, "Campaigns"), "GENERALS_" + name.ToUpperInvariant(), ConvertBody(block, block.Type));
            added++;
        }

        string outputDirectory = Path.Combine(root, "mods", "zh", "rules", "generals");
        Directory.CreateDirectory(outputDirectory);
        foreach (var (path, node) in outputs)
        {
            string header = "GENERATED from the original Generals INI data (INI.big) by 'OpenWestwood.Tools import-generals-missions'.\n" +
                            "Only what Zero Hour's own data does not define: Generals' campaigns and their mission speech and movies.";
            File.WriteAllText(Path.Combine(outputDirectory, path), RuleYaml.Write(node, header));
        }
        Console.WriteLine($"Imported {added} Generals definitions into {outputs.Count} files in {outputDirectory}");
        return 0;
    }
}
