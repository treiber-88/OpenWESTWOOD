using OpenWestwood.Client;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Modules;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Game;

/// <summary>
/// OpenWESTWOOD --audit [--faction Name] [--out file]: checks that every faction of every game has everything it
/// should, without a window. For each playable faction it walks the whole tech tree (from its starting buildings and
/// units, through every command set, the ones upgrades switch to, and every object those objects create: OCL
/// reinforcements, spawns, payloads, projectiles) and reports:
///   - command buttons whose icon can't be found, whose text label is missing, or whose object/upgrade/power/science
///     does not exist;
///   - objects with no art (Zero Hour / Kane's Wrath models, Tiberian Dawn / Red Alert / Dune 2000 sprites, Red Alert 2 /
///     Tiberian Sun voxels and sprites) or no build icon;
///   - module classes the engine does not implement (logic) or cannot draw (client);
///   - special powers no object of the faction can fire, upgrades nothing uses, weapons that don't exist.
/// </summary>
static class Audit
{
    static readonly string[] TextureFolders = ["Data/English/Art/Textures/", "Art/Textures/", "Art/Terrain/", ""];

    static readonly HashSet<string> DrawClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "W3DModelDraw", "W3DDefaultDraw", "W3DTankDraw", "W3DTruckDraw", "W3DTankTruckDraw", "W3DDependencyModelDraw", "W3DOverlordAircraftDraw",
        "W3DOverlordTankDraw", "W3DOverlordTruckDraw", "W3DPoliceCarDraw", "W3DProjectileStreamDraw", "W3DPropDraw", "W3DRopeDraw",
        "W3DScienceModelDraw", "W3DSupplyDraw", "W3DTracerDraw", "W3DTreeDraw", "W3DLaserDraw", "W3DNoDraw", "TDSpriteDraw", "RASpriteDraw",
        "RA2SpriteDraw", "D2KSpriteDraw", "KWTracerDraw",
    };

    sealed class Report
    {
        public readonly SortedDictionary<string, SortedSet<string>> Problems = new(StringComparer.Ordinal);
        public void Add(string kind, string what)
        {
            if (!Problems.TryGetValue(kind, out var set))
                Problems[kind] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(what);
        }
    }

    public static int Run(GameContent content, CommandLine options)
    {
        var rules = content.Rules;
        // --mix-files "tiberiansun:LIMP": files a Westwood game's archives hold whose names contain the text (unnamed: "<id>.unknown").
        if (options.Get("mix-files") is { } mixFiles && mixFiles.Split(':') is [var modId, var pattern])
        {
            using var westwood = TiberianDawn.TdContent.Load(content.RootFolder, modId);
            foreach (var package in westwood.Files.Packages)
                foreach (string path in package.FilePaths.Where(p => p.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{package.Name}: {path}");
            return 0;
        }
        // --kw-assets "HamHead": Kane's Wrath's compiled assets whose names contain the text.
        if (options.Get("kw-assets") is { } kwPattern)
        {
            foreach (var package in content.FileSystem.Packages.OfType<Formats.Sage.SageArtPackage>())
                foreach (var asset in package.Stream.Assets.Where(a => a.Name.Contains(kwPattern, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{package.Name}: {asset.Name}");
            return 0;
        }
        // --kw-strings "Name:X,DESC:Y": Kane's Wrath's texts for those labels ("label<TAB>text" lines).
        if (options.Get("kw-strings") is { } kwLabels)
        {
            foreach (string label in kwLabels.Split(',', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine($"{label}\t{Client.KanesWrath.KwMaps.Text(label)?.Replace('\n', ' ')}");
            return 0;
        }
        // --art-info "TS:LIMPET,RA2:GAPOWR": a Tiberian Sun / Red Alert 2 type's RULES and ART entries (to find its files).
        if (options.Get("art-info") is { } info)
        {
            foreach (string item in info.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(':');
                var art = Client.RedAlert2.Ra2Art.For(parts[0]);
                string type = parts[^1];
                if (art == null)
                    continue;
                Console.WriteLine($"[{type}] rules: " + string.Join("; ", art.Rules.Section(type).Select(kv => $"{kv.Key}={kv.Value}")));
                string image = art.Rules.Get(type, "Image", type);
                Console.WriteLine($"[{image}] art: " + string.Join("; ", art.Art.Section(image).Select(kv => $"{kv.Key}={kv.Value}")));
            }
            return 0;
        }
        var catalog = new RulesCatalog(rules);
        var templates = new TemplateStore(rules);
        var strings = new Formats.Strings.StringTable();
        if (content.FileSystem.Exists("Data/English/generals.csf"))
            strings.LoadCsf(content.FileSystem.ReadAllBytes("Data/English/generals.csf"));
        foreach (var (label, text) in rules.Section("Strings").Entries)
            if (text.Value != null)
                strings.Set(label, text.Value);
        var imageCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var artCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string only = options.Get("faction") ?? "";
        var output = new List<string>();
        void Out(string line)
        {
            Console.WriteLine(line);
            output.Add(line);
        }

        // Things used anywhere (an upgrade is "used" if any rule mentions it besides its own button).
        var allText = new System.Text.StringBuilder();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(content.RootFolder, "mods"), "*.yaml", SearchOption.AllDirectories))
            allText.AppendLine(File.ReadAllText(file));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(content.RootFolder, "src"), "*.cs", SearchOption.AllDirectories))
            if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                allText.AppendLine(File.ReadAllText(file));
        string everything = allText.ToString();

        var factions = GameInfo.PlayerTemplates(rules).Where(t => GameInfo.IsTdFaction(t.Rules) || t.Rules.GetString("StartingBuilding").Length > 0)
            .Where(t => only.Length == 0 || t.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
        int totalProblems = 0;
        var globalMissingModules = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, factionName, faction) in factions)
        {
            var report = new Report();
            var objects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var commandSets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var buttons = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            void AddObject(string name)
            {
                if (name.Length > 0 && templates.Find(name) != null && objects.Add(name))
                    queue.Enqueue(name);
            }
            void AddCommandSet(string name)
            {
                if (name.Length == 0 || !commandSets.Add(name))
                    return;
                if (name.StartsWith('='))
                    name = name.TrimStart('=', ' ');
                if (catalog.CommandSet(name) == null)
                {
                    report.Add("command set missing", name);
                    return;
                }
                foreach (var (_, button) in CommandSets.Buttons(catalog, name))
                {
                    buttons.Add(button.Name);
                    if (button.Object.Length > 0)
                        foreach (string o in RuleValue.Words(button.Object))
                            if (templates.Find(o) == null)
                                report.Add("button builds a missing object", $"{button.Name} -> {o}");
                            else
                                AddObject(o);
                }
            }
            AddObject(faction.GetString("StartingBuilding"));
            for (int i = 0; i < 10; i++)
                AddObject(faction.GetString($"StartingUnit{i}"));
            foreach (string key in new[] { "PurchaseScienceCommandSetRank1", "PurchaseScienceCommandSetRank3", "PurchaseScienceCommandSetRank8", "SpecialPowerShortcutCommandSet" })
                AddCommandSet(faction.GetString(key));

            var objectNames = new HashSet<string>(templates.ObjectNames, StringComparer.OrdinalIgnoreCase);
            while (queue.Count > 0)
            {
                var t = templates.Find(queue.Dequeue())!;
                AddCommandSet(t.CommandSet);
                foreach (var module in t.Modules)
                {
                    // Command sets an upgrade switches to, and any object a module names (spawns, payloads, OCLs...).
                    foreach (var (key, value) in Flatten(module.Settings))
                    {
                        if (key.StartsWith("CommandSet", StringComparison.OrdinalIgnoreCase))
                            AddCommandSet(value);
                        foreach (string word in RuleValue.Words(value))
                        {
                            if (objectNames.Contains(word))
                                AddObject(word);
                            else if (catalog.ObjectCreationList(word) is { } ocl)
                                foreach (var (_, v) in Flatten(ocl))
                                    foreach (string w in RuleValue.Words(v))
                                        if (objectNames.Contains(w))
                                            AddObject(w);
                        }
                    }
                }
                // Weapons: they must exist, and their projectiles are objects too.
                foreach (var (key, value) in Flatten(t.Rules))
                {
                    if (!key.EndsWith("Weapon", StringComparison.OrdinalIgnoreCase) || key.Contains("Slot"))
                        continue;
                    foreach (string w in RuleValue.Words(value))
                    {
                        // (Stage names such as FINAL and MIDPOINT, and slot names, are not weapons.)
                        if (w.Equals("NONE", StringComparison.OrdinalIgnoreCase) || w.All(c => char.IsUpper(c) || c == '_'))
                            continue;
                        if (rules.Get("Weapons", w) is { } weapon)
                            AddObject(weapon.GetString("ProjectileObject"));
                        else if (key.Equals("Weapon", StringComparison.OrdinalIgnoreCase) && !objectNames.Contains(w))
                            report.Add("weapon missing", $"{t.Name}: {w}");
                    }
                }
            }

            var builtByButton = new HashSet<string>(buttons.Select(b => catalog.CommandButtonTemplate(b)!)
                .Where(b => b.CommandType is "UNIT_BUILD" or "DOZER_CONSTRUCT").SelectMany(b => RuleValue.Words(b.Object)), StringComparer.OrdinalIgnoreCase);
            // ---- Buttons ----
            int units = 0, structures = 0, upgrades = 0, powers = 0;
            foreach (string name in buttons)
            {
                var b = catalog.CommandButtonTemplate(name)!;
                string type = b.CommandType;
                bool needsIcon = type is "UNIT_BUILD" or "DOZER_CONSTRUCT" or "PLAYER_UPGRADE" or "OBJECT_UPGRADE" or "PURCHASE_SCIENCE"
                    || type.StartsWith("SPECIAL_POWER");
                if (needsIcon && !ImageExists(content, rules, b.ButtonImage, imageCache))
                    report.Add("icon missing", $"{name} ({type}): '{b.ButtonImage}'");
                // (A label that isn't in the string table is shown as written: only "SECTION:Key" labels can be missing.)
                if (needsIcon && b.TextLabel.Contains(':') && !b.TextLabel.Contains(' ') && !strings.TryGet(b.TextLabel, out _))
                    report.Add("text label missing", $"{name}: {b.TextLabel}");
                switch (type)
                {
                    case "UNIT_BUILD": units++; break;
                    case "DOZER_CONSTRUCT": structures++; break;
                    case "PLAYER_UPGRADE" or "OBJECT_UPGRADE":
                        upgrades++;
                        foreach (string u in RuleValue.Words(b.Upgrade))
                        {
                            if (catalog.Upgrade(u) == null)
                                report.Add("upgrade missing", $"{name} -> {u}");
                            else if (CountOccurrences(everything, u) < 3)
                                report.Add("upgrade that nothing reacts to", $"{name} -> {u}");
                        }
                        break;
                    case "PURCHASE_SCIENCE":
                        foreach (string s in RuleValue.Words(b.Science))
                            if (catalog.Science(s) == null)
                                report.Add("science missing", $"{name} -> {s}");
                        break;
                }
                if (type.StartsWith("SPECIAL_POWER") && b.SpecialPower.Length > 0)
                {
                    powers++;
                    if (catalog.SpecialPower(b.SpecialPower) == null)
                        report.Add("special power missing", $"{name} -> {b.SpecialPower}");
                    else if (!objects.Any(o => templates.Find(o)!.Modules.Any(m => Flatten(m.Settings).Any(kv => RuleValue.Words(kv.Value).Contains(b.SpecialPower, StringComparer.OrdinalIgnoreCase)))))
                        report.Add("special power nothing fires", $"{name} -> {b.SpecialPower}");
                }
            }

            // ---- Objects ----
            foreach (string name in objects)
            {
                var t = templates.Find(name)!;
                foreach (var module in t.Modules)
                {
                    bool draw = module.ModuleType.Equals("Draw", StringComparison.OrdinalIgnoreCase);
                    bool clientOnly = draw || module.ModuleType is "ClientUpdate" or "ClientBehavior";
                    if (draw && !DrawClasses.Contains(module.ClassName))
                        report.Add("draw module not drawn", $"{module.ClassName} ({name})");
                    if (!clientOnly && !ModuleRegistry.IsImplemented(module.ClassName))
                    {
                        report.Add("module not implemented", $"{module.ClassName} ({name})");
                        globalMissingModules.Add(module.ClassName);
                    }
                    if (draw && ArtProblem(content, module, artCache) is { } problem)
                        report.Add("art missing", $"{name}: {problem}");
                }
                // (Only what a button builds needs an icon: Tiberian Dawn's construction yard comes from its MCV and has none.)
                bool buildable = builtByButton.Contains(name);
                if (buildable && !t.Is("PROJECTILE") && !t.Is("INERT"))
                {
                    string icon = t.Rules.GetString("ButtonImage");
                    if (icon.Length > 0 && !ImageExists(content, rules, icon, imageCache))
                        report.Add("object icon missing", $"{name}: '{icon}'");
                }
            }

            int count = report.Problems.Values.Sum(s => s.Count);
            totalProblems += count;
            Out($"== {factionName}: {objects.Count} objects reachable, {units} unit buttons, {structures} building buttons, {upgrades} upgrades, {powers} powers; {count} problems");
            foreach (var (kind, items) in report.Problems)
            {
                Out($"   {kind} ({items.Count}):");
                foreach (string item in items.Take(options.Has("all") ? int.MaxValue : 40))
                    Out($"      {item}");
                if (items.Count > 40 && !options.Has("all"))
                    Out($"      ... and {items.Count - 40} more (--all)");
            }
        }
        Out($"modules not implemented anywhere: {string.Join(", ", globalMissingModules)}");
        Out($"{totalProblems} problems in {factions.Count} factions");
        if (options.Get("out") is { } outFile)
            File.WriteAllLines(outFile, output);
        return totalProblems == 0 ? 0 : 1;
    }

    static int CountOccurrences(string text, string word)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            n++;
            at += word.Length;
        }
        return n;
    }

    /// <summary>Every (key, value) in a rule node, any depth.</summary>
    static IEnumerable<(string Key, string Value)> Flatten(RuleNode node, string key = "")
    {
        if (node.Value != null)
            yield return (key, node.Value);
        if (node.IsMapping)
            foreach (var (k, child) in node.Entries)
                foreach (var item in Flatten(child, k))
                    yield return item;
        else if (node.IsSequence)
            foreach (var child in node.AsList())
                foreach (var item in Flatten(child, key))
                    yield return item;
    }

    /// <summary>Whether a button or portrait image can be shown (the same lookups as the client's ImageCatalog).</summary>
    static bool ImageExists(GameContent content, RuleSet rules, string name, Dictionary<string, bool> cache)
    {
        if (name.Length == 0 || name.Equals("NoImage", StringComparison.OrdinalIgnoreCase))
            return false;
        if (cache.TryGetValue(name, out bool known))
            return known;
        bool found;
        try
        {
            if (rules.Get("MappedImages", name) is { } mapped)
                found = TextureExists(content, mapped.GetString("Texture"));
            else if (name.StartsWith("TDCAMEO:", StringComparison.OrdinalIgnoreCase))
                found = Client.TiberianDawn.TdCameos.Image(name[8..]) != null;
            else if (name.StartsWith("TSCAMEO:", StringComparison.OrdinalIgnoreCase))
                found = Client.RedAlert2.Ra2Art.GetTiberianSun()?.Cameo(name[8..]) != null;
            else if (name.StartsWith("RA2CAMEO:", StringComparison.OrdinalIgnoreCase))
                found = Client.RedAlert2.Ra2Art.Get()?.Cameo(name[9..]) != null;
            else if (name.StartsWith("RACAMEO:", StringComparison.OrdinalIgnoreCase))
                found = RedAlertContent.Art?.Cameo(name[8..]) != null;
            else if (name.StartsWith("KWCAMEO:", StringComparison.OrdinalIgnoreCase))
                found = content.FileSystem.Packages.OfType<Formats.Sage.SageArtPackage>().FirstOrDefault()?.PackedImage(name[8..]) != null;
            else if (name.StartsWith("D2KCAMEO:", StringComparison.OrdinalIgnoreCase))
                found = Client.Dune2000.D2kArt.Get()?.Cameo(name[9..]) != null;
            else
                found = TextureExists(content, name + ".tga");
        }
        catch (Exception)
        {
            found = false;
        }
        return cache[name] = found;
    }

    static bool TextureExists(GameContent content, string name)
    {
        if (name.Length == 0)
            return false;
        string normalized = name.Replace('\\', '/');
        string stem = normalized.Contains('.') ? normalized[..normalized.LastIndexOf('.')] : normalized;
        return TextureFolders.Any(f => content.FileSystem.Exists(f + stem + ".dds") || content.FileSystem.Exists(f + normalized) || content.FileSystem.Exists(f + stem + ".tga"));
    }

    /// <summary>What art a draw module needs that is missing (null: all there).</summary>
    static string? ArtProblem(GameContent content, ModuleDefinition module, Dictionary<string, string?> cache)
    {
        string Key(string what) => $"{module.ClassName}:{what}";
        switch (module.ClassName)
        {
            case "TDSpriteDraw":
            {
                string shape = module.Get("Shape");
                return Cached(Key(shape), () => TiberianDawnContent.Art?.Shape(shape) == null ? $"TD shape {shape}" : null);
            }
            case "RASpriteDraw":
            {
                string shape = module.Get("Shape");
                return Cached(Key(shape), () => RedAlertContent.Art?.Shape(shape) == null ? $"RA shape {shape}" : null);
            }
            case "D2KSpriteDraw":
            {
                string image = module.Get("Image");
                return Cached(Key(image), () => Client.Dune2000.D2kArt.Get()?.HasImage(image) == true ? null : $"D2K image {image}");
            }
            case "RA2SpriteDraw":
            {
                string image = module.Get("Image");
                string game = module.Get("Game");
                bool voxel = RuleValue.Bool(module.Get("Voxel"));
                return Cached(Key(game + image + voxel), () =>
                {
                    var art = Client.RedAlert2.Ra2Art.For(game);
                    if (art == null)
                        return $"{game} art not installed";
                    bool ok = voxel ? art.VoxelImage(image, 0, 0, 0xFFFFFF) != null : (art.BuildingShape(image) ?? art.Shape(image)) != null;
                    return ok ? null : $"{(game.Length > 0 ? game : "RA2")} {(voxel ? "voxel" : "shape")} {image}";
                });
            }
            case "KWTracerDraw" or "W3DLaserDraw" or "W3DNoDraw" or "W3DProjectileStreamDraw" or "W3DRopeDraw" or "W3DTracerDraw":
                return null;
        }
        // Model draws: every model they name must exist.
        var missing = new List<string>();
        foreach (var (key, value) in Flatten(module.Settings))
        {
            if (!key.Equals("Model", StringComparison.OrdinalIgnoreCase) && !key.Equals("Models", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (string model in RuleValue.Words(value))
            {
                if (model.Equals("NONE", StringComparison.OrdinalIgnoreCase) || model.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? problem = Cached("model:" + model, () =>
                    content.Models.GetFile(model) != null ? null : model);
                if (problem != null)
                    missing.Add(problem);
            }
        }
        return missing.Count > 0 ? "model " + string.Join(", ", missing.Distinct()) : null;

        string? Cached(string key, Func<string?> check)
        {
            if (!cache.TryGetValue(key, out var result))
            {
                try
                {
                    result = check();
                }
                catch (Exception e)
                {
                    result = $"{key} ({e.Message})";
                }
                cache[key] = result;
            }
            return result;
        }
    }
}
