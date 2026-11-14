using OpenWestwood.Client;
using OpenWestwood.Client.Platform;
using OpenWestwood.Client.Screens;

// OpenWESTWOOD - an open, moddable engine for Command & Conquer Generals Zero Hour.
//
// Usage:
//   OpenWESTWOOD                                   start the game at the main menu
//   OpenWESTWOOD --mod <id>                        start with a mod from mods/<id>
//   OpenWESTWOOD --view-model <name> [--anim <name>] [--house RRGGBB]
//                                             preview a W3D model
//   OpenWESTWOOD --view-map "Maps/<name>/<name>.map" preview a map
//   OpenWESTWOOD --skirmish "Maps/<name>/<name>.map" [--faction <PlayerTemplate>] [--enemy <PlayerTemplate>]
//   --open-menu <Menus/X.wnd>                 start at this shell menu
//   --click "<Menus/X.wnd:Button>@<seconds>;..." press shell buttons at those times
//   --end-game <VICTORY|DEFEAT>@<seconds>     end the game that way (to see the score screen)
//   --load <file.owsave>                      start in a saved game (saves are in %APPDATA%\OpenWESTWOOD\Save)
//   --save-at <seconds>                       save the next game that far into it
//   --perf                                    print frame timings and the slowest logic parts every 5 s
//   --screenshot <file.png>                   render a few frames, save a screenshot and quit
//   --screenshot-frames <n>                   ... after n rendered frames instead of 5
//   --screenshot-burst <n>                    ... and the n frames after it (file-0.png, file-1.png, ...)
//   --reveal                                  (skirmish) no shroud
//   --look-at <x,y>                           (skirmish) start the camera over this map point
//   --demo-fx <FXList>                        (skirmish) keep playing an FX list at the screen centre

var options = CommandLine.Parse(args);

// A crash leaves its details in %APPDATA%\OpenWESTWOOD\crash.log (and the console) for the bug report.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} OpenWESTWOOD crashed:\n{e.ExceptionObject}\n\n";
    Console.Error.WriteLine(text);
    try
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenWESTWOOD");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "crash.log"), text);
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
};

// Which original games the player has: --zh-path / --kw-path give a folder, --pretend-missing zh,kw tries the game
// without them, --detect-installs prints what was found and where it looked.
OpenWestwood.Core.Installs.GameInstalls.Configure(args);
// Debug: --content-root DIR uses another OpenWESTWOOD folder's content (to try a fresh copy's first start).
if (options.Get("content-root") is { } contentRootOption)
    OpenWestwood.Core.Installs.GameInstalls.ContentRoot = Path.GetFullPath(contentRootOption);
if (options.Has("detect-installs"))
{
    OpenWestwood.Core.Installs.GameInstalls.Detect();
    foreach (string line in OpenWestwood.Core.Installs.GameInstalls.Log)
        Console.WriteLine(line);
    foreach (var game in Enum.GetValues<OpenWestwood.Core.Installs.OriginalGame>())
    {
        var (copy, unavailable) = OpenWestwood.Core.Installs.ContentInstaller.Plan(game);
        Console.WriteLine($"{OpenWestwood.Core.Installs.GameInstalls.DisplayName(game)}: {(OpenWestwood.Core.Installs.GameInstalls.Has(game) ? "INSTALLED" : "NOT INSTALLED")}"
            + $" (files still to copy: {copy.Count}, not available in any copy found: {unavailable.Count})");
    }
    return;
}
// --install zh|kw: installs a game's artwork from the player's copy, in the console.
if (options.Get("install") is { } installWhat)
{
    var game = installWhat.StartsWith("k", StringComparison.OrdinalIgnoreCase) ? OpenWestwood.Core.Installs.OriginalGame.KanesWrath : OpenWestwood.Core.Installs.OriginalGame.ZeroHour;
    int lastPercent = -1;
    var installed = OpenWestwood.Core.Installs.ContentInstaller.Install(game, (done, total, file) =>
    {
        int percent = total > 0 ? (int)(done * 100 / total) : 100;
        if (percent != lastPercent)
            Console.WriteLine($"{percent,3}%  {file}");
        lastPercent = percent;
    });
    Console.WriteLine(installed.Message);
    foreach (string problem in installed.Problems)
        Console.WriteLine("  " + problem);
    Environment.ExitCode = installed.Success ? 0 : 1;
    return;
}

// Without Zero Hour's artwork nothing else can run (the menus are made of it): the install screen comes first.
// (The files themselves: --pretend-missing zh still starts the menus, with Zero Hour's factions locked.)
if (!OpenWestwood.Core.Installs.ContentInstaller.IsInstalled(OpenWestwood.Core.Installs.OriginalGame.ZeroHour))
{
    if (options.Has("hidden") || options.Has("audit") || options.Has("net-test") || options.Has("ai-census"))
    {
        Console.Error.WriteLine("Zero Hour's artwork is not installed. Run OpenWESTWOOD (or OpenWESTWOOD --install zh) to install it from your copy of the game.");
        Environment.ExitCode = 1;
        return;
    }
    var installWindow = new GameWindow("OpenWESTWOOD", 1280, 720, visible: true, fullscreen: false)
    {
        ScreenshotPath = options.Get("screenshot"),
        ScreenshotAfterFrames = int.TryParse(options.Get("screenshot-frames"), out int installShotFrames) ? installShotFrames : 5,
    };
    installWindow.Run((gl, input, osWindow) => new OpenWestwood.Client.Screens.InstallScreen(gl, input, osWindow, OpenWestwood.Core.Installs.GameInstalls.ContentRoot));
    return;
}

using var content = GameContent.Load(options.Get("mod"));

// Everything every faction should have (see Audit): --audit [--faction F] [--all] [--out file]
if (options.Has("audit"))
{
    Environment.ExitCode = OpenWestwood.Game.Audit.Run(content, options);
    return;
}

// Multiplayer without a window (see NetTest): --net-test host|join ...
if (options.Has("net-test"))
{
    Environment.ExitCode = OpenWestwood.Game.NetTest.Run(content, options);
    return;
}

// Debug: --ai-census "FactionA,FactionB" [--minutes 12] [--map Maps/...] runs each faction's computer player alone against
// an idle opponent, with no window, and prints its base and army every three minutes.
if (options.Get("ai-census") is { } censusFactions)
{
    int censusMinutes = int.TryParse(options.Get("minutes"), out int cm) ? cm : 12;
    string censusMap = options.Get("map") ?? "Maps/Tournament Desert/Tournament Desert.map";
    OpenWestwood.Logic.GameProfiler.Enabled = options.Has("perf");
    foreach (string faction in censusFactions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var mapFile = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem, censusMap);
        var game = OpenWestwood.Logic.GameSetup.FromMap(mapFile, content.Rules, seed: 7,
        [
            // --enemy Faction: a computer opponent (the scripts of Zero Hour's AI react to what the enemy does); else an idle one.
            new OpenWestwood.Logic.PlayerSlot("Idle", options.Get("enemy") ?? "FactionTDGDI", IsHuman: options.Get("enemy") == null, StartPosition: 1, Color: 0x0000FF),
            // --difficulty easy|hard: the computer player's (Medium by default).
            new OpenWestwood.Logic.PlayerSlot("Bob", faction, IsHuman: false, StartPosition: 2, Color: 0xFF0000,
                Difficulty: Enum.TryParse<OpenWestwood.Logic.Scripting.Difficulty>(options.Get("difficulty") ?? "Normal", ignoreCase: true, out var censusDifficulty) ? censusDifficulty : OpenWestwood.Logic.Scripting.Difficulty.Normal),
        ], models: content.Models, readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        var start = bob.StartPosition ?? System.Numerics.Vector3.Zero;
        Console.WriteLine($"== {faction} ({bob.AI?.GetType().Name}) on {censusMap}");
        // AI_TRACE=1: the computer player's script and base-building decisions go to ai-trace.txt.
        if (Environment.GetEnvironmentVariable("AI_TRACE") == "1")
        {
            var trace = new StreamWriter($"ai-trace-{faction}.txt") { AutoFlush = true };
            OpenWestwood.Logic.Scripting.ScriptEngine.TraceScripts = true;
            OpenWestwood.Logic.Scripting.ScriptEngine.ShowDebugMessages = true;
            OpenWestwood.Logic.Log.Output = m => { lock (trace) trace.WriteLine($"{game.Frame / 30,5}s {m}"); };
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int fps = OpenWestwood.Logic.Core.LogicTime.FramesPerSecond;
        for (int i = 0; i <= censusMinutes * 60 * fps; i++)
        {
            if (i % (180 * fps) == 0)
            {
                var mine = game.ObjectsOwnedBy(bob).Where(o => !o.IsEffectivelyDead).ToList();
                var structures = mine.Where(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure)).ToList();
                bool IsGatherer(OpenWestwood.Logic.Objects.GameObject o) => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Harvester) || OpenWestwood.Logic.AI.TdFactionAI.TdTypeOf(o.Template) == "UNIT_HARVESTER";
                var army = mine.Where(o => o.IsMobile && o.Template.Rules.TryGet("WeaponSet", out _) && !IsGatherer(o)
                    && OpenWestwood.Logic.AI.TdFactionAI.TdTypeOf(o.Template) != "UNIT_MCV" && !o.IsKindOf("DOZER")).ToList();
                float reach = structures.Count == 0 ? 0 : structures.Max(o => OpenWestwood.Logic.Core.MathUtil.Distance2D(o.Position, start));
                Console.WriteLine($"{i / fps / 60,3} min: ${bob.Money.Amount,6}  structures {structures.Count,3}  army {army.Count,3}  gatherers {mine.Count(IsGatherer),2}  "
                    + $"yards {structures.Count(o => o.IsKindOf("COMMANDCENTER"))}  reach {reach,5:F0}  earned {bob.Score.TotalMoneyEarned}  objects {game.Objects.Count()}  ({watch.Elapsed.TotalSeconds:F0}s)");
                // --perf: the slowest logic parts over those three minutes (ms per game second).
                if (options.Has("perf") && i > 0)
                {
                    foreach (var (name, ms) in OpenWestwood.Logic.GameProfiler.Report().Take(25))
                        Console.WriteLine($"   [perf] {name,-50} {ms / 180:F2} ms/s");
                    OpenWestwood.Logic.GameProfiler.Reset();
                }
                if (i == censusMinutes * 60 * fps)
                {
                    Console.WriteLine("   structures: " + string.Join(", ", structures.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
                    Console.WriteLine("   army: " + string.Join(", ", army.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
                    foreach (var info in bob.BuildList)
                        Console.WriteLine($"   build list: {info.TemplateName,-34} auto {info.IsAutomaticBuild,-5} priority {info.IsPriorityBuild,-5} rebuilds {info.NumRebuilds,2} object {info.ObjectId}");
                }
            }
            // CENSUS_WATCH=Name: that object's state every 5 seconds for the first five minutes.
            if (Environment.GetEnvironmentVariable("CENSUS_WATCH") is { } watchName && i % (5 * fps) == 0 && i < 300 * fps)
                foreach (var w in game.ObjectsOwnedBy(bob).Where(o => o.Template.Name.Contains(watchName)))
                    Console.WriteLine($"   {i / fps,4}s {w.Template.Name}#{w.Id} at {w.Position.X:0},{w.Position.Y:0},{w.Position.Z:0} ai {w.AI?.State} {w.AI?.DockStepName} "
                        + $"gatherer {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(w)?.State} boxes {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(w)?.Boxes} money ${bob.Money.Amount}");
            game.IsGameOver = false; // the idle side may be wiped out: the census goes on
            game.Update();
        }
    }
    return;
}

// The shell map's battle is set up while the window opens and the menus load (only when the main menu will show).
if (!options.Has("skirmish") && !options.Has("trailer") && !options.Has("view-model") && !options.Has("view-map") && !options.Has("sole-survivor")
    && !options.Has("demo-fx") && !options.Has("demo-drive") && !options.Has("select") && !options.Has("look-at"))
    OpenWestwood.Client.Shell.DuneShellBattle.StartEarly(content);

// The trailer's scenes (--trailer 1..6) and --fullscreen play on the whole screen.
bool fullscreen = options.Has("trailer") || options.Has("fullscreen");
var window = new GameWindow("OpenWESTWOOD", fullscreen ? 1920 : 1280, fullscreen ? 1080 : 720, visible: !options.Has("hidden"), fullscreen: fullscreen)
{
    ScreenshotPath = options.Get("screenshot"),
    ScreenshotAfterFrames = int.TryParse(options.Get("screenshot-frames"), out int shotFrames) ? shotFrames : 5,
    ScreenshotBurst = int.TryParse(options.Get("screenshot-burst"), out int shotBurst) ? shotBurst : 0,
    PerfReport = options.Has("perf"),
};
OpenWestwood.Logic.GameProfiler.Enabled = options.Has("perf");

window.Run((gl, input, osWindow) =>
{
    if (options.Get("view-model") is { } modelName)
    {
        uint house = Convert.ToUInt32(options.Get("house") ?? "0", 16);
        return new ModelViewerScreen(gl, input, content, modelName, options.Get("anim"), house);
    }

    // OpenWESTWOOD's trailer: a scripted scene (--trailer N [--trailer-signal DIR], see Trailer).
    if (int.TryParse(options.Get("trailer"), out int trailerScene))
    {
        var (trailerMap, trailerGame, director) = OpenWestwood.Game.Trailer.Build(content, trailerScene);
        foreach (var art in content.FileSystem.Packages.OfType<OpenWestwood.Formats.Sage.SageArtPackage>())
            _ = art.Stream; // Kane's Wrath's art, read now rather than mid-scene
        var trailerScreen = new OpenWestwood.Client.InGame.GameScreen(gl, input, new OpenWestwood.Client.UI.UiResources(gl, content), trailerGame, trailerMap);
        director.Setup(trailerScreen, options.Get("trailer-signal"));
        trailerScreen.Tick = dt => director.Tick(trailerScreen, dt);
        return trailerScreen;
    }

    if (options.Get("view-map") is { } mapPath)
        return new MapViewerScreen(gl, input, content, mapPath, options.Has("topdown"));

    // Sole Survivor Remix: --sole-survivor setup (its setup screen), join / host (its network game screen), or --sole-survivor [--ssr-mode Freeplay|ZoneDefense|
    // CaptureTheFlag|Classic] [--faction F] [--map M] [--computers N] [--ssr-type Vehicle|Infantry|Naval|Any]
    // [--ssr-start N (1-based)] (straight into a match).
    if (options.Has("sole-survivor"))
    {
        var ssrUi = new OpenWestwood.Client.UI.UiResources(gl, content);
        if (options.Get("sole-survivor") == "setup")
            return new OpenWestwood.Client.SoleSurvivor.SsrSetupScreen(gl, input, osWindow, ssrUi, OpenWestwood.Client.Shell.ShellScreen.BuildMapCache(content));
        // (--sole-survivor join: the network game screen, to join one; --sole-survivor host: hosts the default setup.)
        if (options.Get("sole-survivor") is "join" or "host")
            return new OpenWestwood.Client.SoleSurvivor.SsrOnlineScreen(gl, input, osWindow, ssrUi, OpenWestwood.Client.Shell.ShellScreen.BuildMapCache(content),
                options.Get("sole-survivor") == "host" ? new OpenWestwood.Client.SoleSurvivor.SsrMatchSetup { Faction = options.Get("faction") ?? "FactionAmerica", Map = options.Get("map") ?? "Maps/Tournament Desert/Tournament Desert.map" } : null);
        var ssrSetup = new OpenWestwood.Client.SoleSurvivor.SsrMatchSetup
        {
            Mode = Enum.TryParse<OpenWestwood.Logic.SoleSurvivor.SsrMode>(options.Get("ssr-mode") ?? "Freeplay", true, out var ssrMode) ? ssrMode : OpenWestwood.Logic.SoleSurvivor.SsrMode.Freeplay,
            Faction = options.Get("faction") ?? "FactionAmerica",
            Map = options.Get("map") ?? "Maps/Tournament Desert/Tournament Desert.map",
            MapPlayers = 4,
            Computers = int.TryParse(options.Get("computers"), out int ssrComputers) ? ssrComputers : 3,
            UnitType = options.Get("ssr-type") is { } ssrType
                ? Enum.TryParse<OpenWestwood.Logic.SoleSurvivor.SsrCategory>(ssrType, true, out var ssrKind) ? ssrKind : null
                : OpenWestwood.Logic.SoleSurvivor.SsrCategory.Vehicle,
            Start = int.TryParse(options.Get("ssr-start"), out int ssrStart) ? ssrStart - 1 : -1,
            // Debug: --ssr-unit TEMPLATE goes in as that unit at once.
            AutoUnit = options.Get("ssr-unit"),
        };
        return OpenWestwood.Client.SoleSurvivor.SsrSetupScreen.Play(gl, input, osWindow, ssrUi, ssrSetup);
    }

    // Without a skirmish (or one of its debug options) the game starts at the main menu.
    if (!options.Has("skirmish") && !options.Has("demo-fx") && !options.Has("demo-drive") && !options.Has("select") && !options.Has("look-at"))
    {
        OpenWestwood.Client.Shell.MainMenu.RevealAtStart = options.Has("menu-shown");
        OpenWestwood.Client.Shell.ShellScreen.DebugStartMenu = options.Get("open-menu");
        // --load <file.owsave>: straight into a saved game. Debug: --save-at <seconds> saves the next game that far in.
        OpenWestwood.Client.Shell.ShellScreen.LoadAtStart = options.Get("load");
        // --mission "GLA/Mission02": straight into a campaign mission (campaign and mission names as in campaigns.yaml).
        OpenWestwood.Client.Shell.ShellScreen.MissionAtStart = options.Get("mission");
        if (float.TryParse(options.Get("save-at"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float saveAt))
            OpenWestwood.Client.InGame.GameScreen.DebugSaveAt = saveAt;
        // Debug: --end-game "VICTORY@5" ends the next game that way after 5 seconds.
        if (options.Get("end-game") is { } endGame && endGame.Split('@') is [var endAction, var endAt]
            && float.TryParse(endAt, System.Globalization.CultureInfo.InvariantCulture, out float endSeconds))
            OpenWestwood.Client.InGame.GameScreen.DebugEndGame = (endAction.ToUpperInvariant(), endSeconds);
        // Debug: --click "Menus/X.wnd:ButtonName@2.5;..." presses buttons at those times (seconds).
        foreach (var click in (options.Get("click") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (click.LastIndexOf('@') is int at and > 0 && float.TryParse(click[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture, out float when))
                OpenWestwood.Client.Shell.ShellScreen.DebugClicks.Enqueue((click[..at], when));
        return new OpenWestwood.Client.Shell.ShellScreen(gl, input, osWindow, new OpenWestwood.Client.UI.UiResources(gl, content));
    }

    // A quick skirmish against the computer: --skirmish "Maps/<name>/<name>.map" [--faction FactionAmerica] [--enemy FactionChina]
    string skirmishMap = options.Get("skirmish") ?? "Maps/Tournament Desert/Tournament Desert.map";
    var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem, skirmishMap); // also "td:SCM01EA"
    var game = OpenWestwood.Logic.GameSetup.FromMap(map, content.Rules, (ulong)Environment.TickCount64,
    [
        new OpenWestwood.Logic.PlayerSlot("Player", options.Get("faction") ?? "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x2050FF),
        new OpenWestwood.Logic.PlayerSlot("Enemy", options.Get("enemy") ?? "FactionChina", IsHuman: false, StartPosition: 2, Color: 0xE02020),
    ], models: content.Models, readFile: p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    // --game-speed 2: the lobby's game speed (1, 1.5 or 2).
    if (float.TryParse(options.Get("game-speed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gameSpeed))
        game.GameSpeed = gameSpeed;
    // The lobby's map options: --explored (the map starts explored), --nofog (no fog of war).
    OpenWestwood.Logic.GameSetup.ApplyVisibility(game, options.Has("explored"), options.Has("nofog"));
    if (options.Has("reveal"))
        foreach (var player in game.Players.All)
            game.Shroud.RevealMapForPlayerPermanently(player);
    // Debug: --deploy unfolds a Tiberian Dawn player's MCV at once; --fast N plays N seconds first.
    if (options.Has("deploy"))
        foreach (var mcv in game.Objects.Where(o => o.Owner == game.Players.Find("Player")).ToList())
            mcv.FindModule<OpenWestwood.Logic.Modules.Behaviors.TdDeployToStructure>()?.Deploy();
    // Debug: --fight "TDGDI_MTNK,ChinaTankBattleMaster" puts the player's and the enemy's units face to face
    // in the middle of the map (a few of each) and the camera on them.
    System.Numerics.Vector3? fightAt = null;
    if (options.Get("fight") is { } fight && fight.Split(',') is [var mineName, var theirsName])
    {
        var middle = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        // --fight-count N: N a side (3 by default), in columns of ten.
        int fightCount = int.TryParse(options.Get("fight-count"), out int fc) ? fc : 3;
        for (int i = 0; i < fightCount; i++)
        {
            var a = middle + new System.Numerics.Vector3(-70 - i / 10 * 40, (i % 10 - 1) * 40, 0);
            var b = middle + new System.Numerics.Vector3(70 + i / 10 * 40, (i % 10 - 1) * 40, 0);
            var mine = game.CreateObject(mineName.Trim(), game.Players.Find("Player")!, a with { Z = game.Terrain.GroundHeight(a) });
            var theirs = game.CreateObject(theirsName.Trim(), game.Players.Find("Enemy")!, b with { Z = game.Terrain.GroundHeight(b) });
            // Debug: --fight-still stuns the enemy's ones (to watch the player's shots without return fire).
            if (options.Has("fight-still") && theirs != null)
            {
                theirs.SetDisabled("DISABLED_EMP");
                if (mine != null)
                    new OpenWestwood.Logic.Commands.AttackObjectCommand(game.Players.Find("Player")!.Index, [mine.Id], theirs.Id).Execute(game);
            }
            // Debug: --fight-walk sends the player's ones walking off across the screen (to see units on the move).
            if (options.Has("fight-walk") && mine != null)
                new OpenWestwood.Logic.Commands.MoveCommand(game.Players.Find("Player")!.Index, [mine.Id], a + new System.Numerics.Vector3(-600, 300, 0)).Execute(game);
        }
        fightAt = middle;
    }
    // Debug: --spawn "AmericaBarracks,AmericaWarFactory" gives the player those, finished, near its start (to see their
    // build menus with --select).
    if (options.Get("spawn") is { } spawn && game.Players.Find("Player") is { } spawnOwner)
    {
        var spawnAt = spawnOwner.StartPosition ?? new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        int spawned = 0;
        foreach (string spawnName in spawn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = spawnAt + new System.Numerics.Vector3(-300 + 150 * (spawned % 5), 250 + 150 * (spawned / 5), 0);
            spawned++;
            if (game.CreateObject(spawnName, spawnOwner, p with { Z = game.Terrain.GroundHeight(p) }) is { } made)
                game.TreatAsBuilt(made);
        }
        spawnOwner.Money.Deposit(100000);
    }
    if (int.TryParse(options.Get("fast"), out int fastSeconds))
        for (int i = 0; i < fastSeconds * OpenWestwood.Logic.Core.LogicTime.FramesPerSecond; i++)
            game.Update();
    // Debug: --game-click "ButtonGeneral@3;ButtonExit@5" clicks interface windows during the game.
    foreach (var gameClick in (options.Get("game-click") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        if (gameClick.LastIndexOf('@') is int clickAt and > 0 && float.TryParse(gameClick[(clickAt + 1)..], System.Globalization.CultureInfo.InvariantCulture, out float clickWhen))
            OpenWestwood.Client.InGame.GameScreen.DebugClicks.Enqueue((gameClick[..clickAt], clickWhen));
    var ui = new OpenWestwood.Client.UI.UiResources(gl, content);
    var screen = new OpenWestwood.Client.InGame.GameScreen(gl, input, ui, game, map);
    if (fightAt is { } fightPoint)
    {
        screen.LookAt(fightPoint);
        // Debug: --fight-kill destroys the enemy's ones two seconds in (to see how vehicles die and what they leave).
        if (options.Has("fight-kill") && options.Get("fight") is { } killNames && killNames.Split(',') is [_, var killName])
        {
            float shown = 0;
            bool killed = false;
            screen.Tick = dt =>
            {
                shown += dt;
                if (killed || shown < 2)
                    return;
                killed = true;
                foreach (var victim in game.Objects.Where(o => o.Template.Name == killName.Trim() && !o.IsEffectivelyDead).ToList())
                    victim.Kill();
            };
        }
    }
    if (options.Get("look-at") is "player" or "enemy")
    {
        var who = game.Players.Find(options.Get("look-at") == "player" ? "Player" : "Enemy");
        if (who?.StartPosition is { } start)
            screen.LookAt(start);
    }
    if (options.Get("select") is { } selectTemplate)
        screen.SelectFirst(selectTemplate);
    if (options.Get("look-at") is { } lookAt && lookAt.Split(',') is [var lx, var ly]
        && float.TryParse(lx, System.Globalization.CultureInfo.InvariantCulture, out float x) && float.TryParse(ly, System.Globalization.CultureInfo.InvariantCulture, out float y))
        screen.LookAt(new System.Numerics.Vector3(x, y, 0));
    if (options.Get("demo-fx") is { } demoFx)
        screen.DemoFx(demoFx);
    // Debug: --demo-drive "AmericaTankCrusader,AmericaVehicleHumvee" spawns the vehicles next to the
    // player's command center and drives them off together (for checking treads, wheels and body motion).
    if (options.Get("demo-drive") is { } demoDrive)
    {
        var player = game.Players.Find("Player")!;
        var home = (game.Objects.FirstOrDefault(o => o.Owner == player && o.IsKindOf("COMMANDCENTER")) ?? game.Objects.First(o => o.Owner == player)).Position;
        var units = demoDrive.Split(',').Select((name, i) =>
            game.CreateObject(name.Trim(), player, home + new System.Numerics.Vector3(-120 + i % 8 * 45, -140 + i / 8 * 45, 0))).OfType<OpenWestwood.Logic.Objects.GameObject>().ToList();
        foreach (var unit in units)
            unit.AI?.MoveTo(unit.Position + new System.Numerics.Vector3(0, -900, 0));
        screen.LookAt(home + new System.Numerics.Vector3(0, -260, 0));
    }
    return screen;
});

/// <summary>Minimal "--key value" / "--flag" command line parser.</summary>
sealed class CommandLine
{
    readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                continue;
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            result.values[key] = value;
        }
        return result;
    }

    public bool Has(string key) => values.ContainsKey(key);
    public string? Get(string key) => values.GetValueOrDefault(key);
}
