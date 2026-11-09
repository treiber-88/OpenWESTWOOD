using OpenWestwood.Core.Mods;
using OpenWestwood.Tools;

// OpenWESTWOOD developer tools. Run with no arguments for help.
//
//   OpenWestwood.Tools import-rules [--force] Convert the original INI data into YAML rules files.
//   OpenWestwood.Tools import-td-rules <src> <out> Convert Tiberian Dawn's C++ type tables into YAML rules.
//   OpenWestwood.Tools ini-check              Parse every original INI file and report problems.
//   OpenWestwood.Tools list <dir> [ext]       List files in the virtual file system.
//   OpenWestwood.Tools extract <path> <out>   Copy one file out of the virtual file system.

string root = GameRoot.Find();
string command = args.Length > 0 ? args[0] : "help";
string[] rest = args.Skip(1).ToArray();

// Tiberian Dawn's rules come from EA's released C++ source, not the Zero Hour files.
if (command == "import-td-rules")
    return TdRulesImporter.Run(rest);
// Tiberian Dawn's GDI and Nod as Zero Hour engine factions (mods/zh/rules/td).
if (command == "export-d2k-factions")
    return D2kFactionExporter.Run(root);

// Red Alert's Allies and Soviets as Zero Hour engine factions (mods/zh/rules/ra).
// Red Alert 2 / Yuri's Revenge's Allies, Soviets and Yuri (mods/zh/rules/ra2).
if (command == "export-ra2-factions")
    return Ra2FactionExporter.Run(root);
if (command == "export-ts-factions")
    return Ra2FactionExporter.RunTiberianSun(root);
if (command == "export-ra-factions")
    return RaFactionExporter.Run(root);

// Kane's Wrath's GDI, Nod and Scrin with their sub-factions (mods/zh/rules/kw), from EA's source XML.
if (command == "export-kw-factions")
    return KwFactionExporter.Run(root);

if (command == "export-kw-terrain")
{
    KwTerrainExporter.Run(root, Path.Combine(root, "mods", "zh", "rules", "kw"));
    return 0;
}

// Emperor: Battle for Dune's Atreides, Harkonnen and Ordos (mods/zh/rules/emperor), from content/emperor.
if (command == "export-emperor-factions")
    return EmperorFactionExporter.Run(root);

if (command == "export-td-factions")
    return TdFactionExporter.Run(root);

var manifest = ModManifest.Load(Path.Combine(root, "mods", "zh", "mod.yaml"));
using var fileSystem = manifest.MountPackages(root);

return command switch
{
    "ini-check" => IniCheck.Run(fileSystem),
    "ini-blocks" => IniCheck.Blocks(fileSystem, rest),
    "import-rules" => RulesImporter.Run(fileSystem,
        Array.IndexOf(rest, "--out") is int o and >= 0 && o + 1 < rest.Length ? rest[o + 1] : Path.Combine(root, "mods", "zh", "rules"), rest.Contains("--force")),
    "import-generals-missions" => RulesImporter.RunGeneralsMissions(root, fileSystem),
    "map-check" => MapCheck.Run(fileSystem, rest),
    "script-stats" => ScriptStats.Run(fileSystem, rest),
    "terrain-view" => TerrainView.Run(fileSystem, rest),
    "wnd-dump" => WndDump.Run(fileSystem, rest),
    "w3d-dump" => W3dDump.Run(fileSystem, rest),
    "list" => FileTools.List(fileSystem, rest),
    "extract" => FileTools.Extract(fileSystem, rest),
    "texture-png" => FileTools.TexturePng(fileSystem, rest),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        OpenWestwood.Tools <command>
          import-rules           Convert original INI data into mods/zh/rules/*.yaml
          ini-check              Parse every original INI file and report problems
          map-check [filter]     Parse every map and report problems
          list <dir> [ext]       List files in the virtual file system
          extract <path> <out>   Copy a file out of the virtual file system
        """);
    return 1;
}
