using System.Numerics;
using OpenWestwood.Client.InGame;
using OpenWestwood.Client.Platform;
using OpenWestwood.Client.UI;
using OpenWestwood.Client.UI.Gui;
using OpenWestwood.Client.World;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Scripting;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenWestwood.Client.Shell;

/// <summary>
/// The front end: the menus (<see cref="Shell"/>) over the shell map, a scripted battle playing
/// behind them (GameData ShellMapName, run in the original's GAME_SHELL mode).
/// </summary>
public sealed class ShellScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly IWindow window;
    readonly List<Action> unsubscribe = new();
    int width = 1280, height = 720;
    Vector2 mouse;

    public UiResources Ui { get; }
    public GuiManager Gui { get; } = new();
    public TransitionHandler Transitions { get; }
    public Shell Shell { get; }
    public CampaignManager Campaigns { get; }
    public GameLauncher Launcher { get; }

    // The shell map.
    GameLogic? shellGame;
    WorldView? shellWorld;
    ScriptDirector? director;
    Audio.GameAudio? shellAudio;

    /// <summary>The game that just ended, for the score screen (null when the shell starts fresh).</summary>
    public FinishedGame? LastGame { get; }

    public ShellScreen(GL gl, IInputContext input, IWindow window, UiResources ui, CampaignManager? campaigns = null,
        FinishedGame? finishedGame = null)
    {
        LastGame = finishedGame;
        this.gl = gl;
        this.input = input;
        this.window = window;
        Ui = ui;
        Launcher = new GameLauncher(gl, input, window, ui);
        Campaigns = campaigns ?? new CampaignManager(ui.Content.Rules.Section("Campaigns"));
        Transitions = new TransitionHandler(ui.Content.Rules.Section("WindowTransitions"))
        {
            FindWindow = Gui.Find,
            PlaySound = name => ui.Audio.AddAudioEvent(name),
            GetText = w => w.Text ?? ui.Gui.Text(w.Definition.Text),
        };
        Shell = new Shell(this);
        Shell.Register("Menus/MainMenu.wnd", (s, l) => new MainMenu(s, l));
        Shell.Register("Menus/SkirmishGameOptionsMenu.wnd", (s, l) => new SkirmishGameOptionsMenu(s, l));
        // OpenWESTWOOD's network play: the LAN lobby, direct connect (by address) and the network game's setup.
        Shell.Register("Menus/LanLobbyMenu.wnd", (s, l) => new LanLobbyMenu(s, l));
        Shell.Register("Menus/NetworkDirectConnect.wnd", (s, l) => new NetworkDirectConnectMenu(s, l));
        Shell.Register("Menus/LanGameOptionsMenu.wnd", (s, l) => new SkirmishGameOptionsMenu(s, l, network: true));
        Shell.Register("Menus/ScoreScreen.wnd", (s, l) => new ScoreScreen(s, l));
        Shell.Register("Menus/SaveLoad.wnd", (s, l) => new SaveLoadMenu(s, l));

        foreach (var m in input.Mice)
        {
            Action<IMouse, Vector2> move = (_, p) => OnMouseMove(p * GameWindow.MouseScale);
            Action<IMouse, MouseButton> down = (_, b) =>
            {
                if (b != MouseButton.Left)
                    return;
                Gui.MouseDown(mouse);
            };
            Action<IMouse, MouseButton> up = (_, b) => { if (b == MouseButton.Left) Gui.MouseUp(mouse); };
            Action<IMouse, ScrollWheel> scroll = (_, w) => Gui.Scroll(mouse, w.Y);
            m.MouseMove += move;
            m.MouseDown += down;
            m.MouseUp += up;
            m.Scroll += scroll;
            unsubscribe.Add(() => { m.MouseMove -= move; m.MouseDown -= down; m.MouseUp -= up; m.Scroll -= scroll; });
        }
        foreach (var k in input.Keyboards)
        {
            Action<IKeyboard, char> ch = (_, c) => OnChar(c);
            k.KeyChar += ch;
            unsubscribe.Add(() => k.KeyChar -= ch);
        }

        if (finishedGame != null)
        {
            // The original's clearGameData: the score screen goes on top of the menus the game was started from.
            Shell.Restore(finishedGame.ShellStack);
            Transitions.SetGroup("FadeWholeScreen");
            Shell.Push("Menus/ScoreScreen.wnd");
            Transitions.Reverse("FadeWholeScreen");
        }
        else
            Shell.Push(DebugStartMenu ?? "Menus/MainMenu.wnd");
    }

    /// <summary>A campaign mission to start as soon as the shell is up (--mission "GLA/Mission02").</summary>
    public static string? MissionAtStart { get; set; }

    /// <summary>A saved game to load as soon as the shell is up (--load).</summary>
    public static string? LoadAtStart { get; set; }

    /// <summary>Debug: open this menu instead of the main menu (e.g. "Menus/SkirmishGameOptionsMenu.wnd").</summary>
    public static string? DebugStartMenu { get; set; }

    /// <summary>Debug: "Window.wnd:Name@seconds" entries clicked in order once each time is reached.</summary>
    public static Queue<(string Window, float At)> DebugClicks { get; } = new();
    float debugClock;

    void OnMouseMove(Vector2 point)
    {
        mouse = point;
        if (Shell.Top?.OnMouseMove(point) == true)
            return;
        Gui.MouseMove(point);
    }

    void OnChar(char c)
    {
        if (Shell.Top?.OnKey(c) == true)
            return;
        Gui.KeyChar(c);
    }

    /// <summary>Starts (or ends) the shell map behind the menus (the original's showShellMap).</summary>
    public void ShowShellMap(bool on)
    {
        if (!on)
        {
            shellWorld?.Dispose();
            shellWorld = null;
            shellGame = null;
            director = null;
            shellAudio = null;
            pendingBattle = null;
            duneBattle = false;
            return;
        }
        if (shellGame != null || pendingBattle != null)
            return;
        // OpenWESTWOOD: a battle for Dune (GameData OpenWestwoodShellMap), set up and played ahead in the background while
        // its theme starts.
        if (DuneShellBattle.MapName(Ui.Content.Rules) != null)
        {
            string music = Ui.Content.Rules.Section("GameData").GetString("OpenWestwoodShellMusic");
            if (music.Length > 0)
                Ui.Audio.ChangeMusic(music, fadeOut: false, 0);
            // (The first battle was started while the game loaded: see DuneShellBattle.StartEarly.)
            pendingBattle = DuneShellBattle.TakeEarly() ?? DuneShellBattle.Start(Ui.Content);
            return;
        }
        string map = ui.Content.Rules.Section("GameData").GetString("ShellMapName").Replace('\\', '/');
        if (map.Length == 0 || !Ui.Content.FileSystem.Exists(map))
            return;
        try
        {
            var mapFile = Formats.Maps.MapFile.Read(Ui.Content.FileSystem.ReadAllBytes(map));
            shellGame = GameSetup.FromMap(mapFile, Ui.Content.Rules, seed: 0, models: Ui.Content.Models,
                readFile: p => Ui.Content.FileSystem.Exists(p) ? Ui.Content.FileSystem.ReadAllBytes(p) : null);
            shellGame.IsShellGame = true;
            var viewer = shellGame.Players.Local ?? shellGame.Players.All.First(p => !p.IsNeutral);
            shellWorld = new WorldView(gl, Ui.Content, shellGame, mapFile, Ui.Textures) { Viewer = viewer };
            shellGame.Shroud.RevealMapForPlayerPermanently(viewer);
            director = new ScriptDirector(shellGame, shellWorld, Ui, viewer);
            shellAudio = new Audio.GameAudio(Ui.Audio, shellGame, shellWorld, viewer);
            shellWorld.LookAt(new Vector3(shellGame.Terrain.WorldWidth / 2, shellGame.Terrain.WorldHeight / 2, 0));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Shell map failed to start: {e.Message}");
            shellGame = null;
            shellWorld = null;
        }
    }

    UiResources ui => Ui;

    /// <summary>Leaves the shell for a game screen.</summary>
    public void SwitchTo(IScreen next)
    {
        Shell.Reset();
        // The shell map and its music end before the game starts.
        ShowShellMap(false);
        Ui.Audio.StopAllAudio();
        GameWindow.Current?.Switch(next);
    }

    public void Quit() => window.Close();

    /// <summary>Leaves for Sole Survivor Remix's setup (OpenWESTWOOD).</summary>
    public void OpenSoleSurvivor() => SwitchTo(new SoleSurvivor.SsrSetupScreen(gl, input, window, Ui, MapCache));

    /// <summary>The difficulty picked for the Generals Challenge.</summary>
    public Difficulty ChallengeDifficulty { get; set; } = Difficulty.Normal;

    GuiLayout? optionsLayout;

    /// <summary>The options popup (the original's OptionsMenu.wnd layout, kept outside the menu stack).</summary>
    public void ShowOptions()
    {
        if (optionsLayout == null)
        {
            optionsLayout = GuiLayout.Load(Ui.Content.FileSystem, "Window/Menus/OptionsMenu.wnd");
            LayoutAdditions.WireViewOption(optionsLayout);
            foreach (string close in new[] { "ButtonBack", "ButtonAccept" })
                if (optionsLayout.Find<GuiPushButton>(close) is { } button)
                    button.Clicked += _ =>
                    {
                        if (optionsLayout != null)
                            Gui.Remove(optionsLayout);
                    };
        }
        Gui.Remove(optionsLayout);
        Gui.Push(optionsLayout);
        optionsLayout.Layout(Ui.Gui.Scale);
        optionsLayout.Hidden = false;
    }

    // ---- Skirmish ----

    /// <summary>The skirmish being set up (the original's TheSkirmishGameInfo).</summary>
    public Logic.Multiplayer.GameInfo? SkirmishGameInfo { get; set; }

    Logic.Multiplayer.MapCache? mapCache;
    /// <summary>The map list, read on a loading thread from when the menus open (reading every game's maps took seconds,
    /// a freeze when Skirmish was clicked).</summary>
    Task<Logic.Multiplayer.MapCache>? mapCacheLoading;
    public Logic.Multiplayer.MapCache MapCache => mapCache ??= (mapCacheLoading ?? Task.FromResult(LoadMapCache())).GetAwaiter().GetResult();

    /// <summary>The official maps, and (OpenWESTWOOD) Tiberian Dawn's multiplayer maps when its files are installed.</summary>
    Logic.Multiplayer.MapCache LoadMapCache() => BuildMapCache(Ui.Content);

    /// <summary>Every map this machine can play (also used to set up a network game without the menus).</summary>
    public static Logic.Multiplayer.MapCache BuildMapCache(GameContent content)
    {
        // OpenWESTWOOD: Zero Hour's own maps only for players who have Zero Hour installed.
        var cache = Core.Installs.GameInstalls.Has(Core.Installs.OriginalGame.ZeroHour)
            ? Logic.Multiplayer.MapCache.Load(content.FileSystem)
            : new Logic.Multiplayer.MapCache();
        TiberianDawn.TdMaps.AddTo(cache);
        Dune2000.D2kMaps.AddTo(cache);
        RedAlert2.Ra2Maps.AddTo(cache);
        KanesWrath.KwMaps.AddTo(cache);
        return cache;
    }

    readonly Dictionary<string, Graphics.Texture?> previews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The map's picture: the .tga beside the .map (the original's getMapPreviewImage).</summary>
    public Graphics.Texture? MapPreview(string map)
    {
        if (map.Length == 0)
            return null;
        if (!previews.TryGetValue(map, out var texture))
            previews[map] = texture = LoadMapPreview(Ui, map);
        return texture;
    }

    /// <summary>A map's picture, whichever game's map it is (null if it has none).</summary>
    public static Graphics.Texture? LoadMapPreview(UI.UiResources ui, string map)
    {
        if (map.Length == 0)
            return null;
        if (RedAlert2.Ra2Maps.IsRa2Map(map))
            return ui.Textures.GetOrCreate("preview:" + map, () => RedAlert2.Ra2Maps.Preview(map));
        if (KanesWrath.KwMaps.IsKwMap(map))
            return ui.Textures.GetOrCreate("preview:" + map, () => KanesWrath.KwMaps.Preview(map));
        if (Dune2000.D2kMaps.IsD2kMap(map))
            return ui.Textures.GetOrCreate("preview:" + map, () => Dune2000.D2kMaps.Preview(map));
        // A Tiberian Dawn map's picture: its whole ground with Tiberium and trees.
        if (TiberianDawn.TdMaps.IsTdMap(map))
            return ui.Textures.GetOrCreate("preview:" + map, () => TiberianDawn.TdMaps.Preview(map));
        string tga = Path.ChangeExtension(map.Replace('\\', '/'), ".tga");
        return ui.Content.FileSystem.Exists(tga) ? ui.Textures.Get(tga, repeat: false) : null;
    }

    /// <summary>A message box with an OK button (the original's MessageBoxOk).</summary>
    public void ShowMessageBox(string title, string message)
    {
        var box = GuiLayout.Load(Ui.Content.FileSystem, "Window/Menus/MessageBox.wnd");
        if (box.Find("MessageBox.wnd:StaticTextTitle") is { } t)
            t.Text = title;
        if (box.Find("MessageBox.wnd:StaticTextMessage") is { } m)
            m.Text = message;
        foreach (string hide in new[] { "ButtonNo", "ButtonYes", "ButtonCancel" })
            if (box.Find($"MessageBox.wnd:{hide}") is { } b)
                b.Hidden = true;
        if (box.Find<GuiPushButton>("MessageBox.wnd:ButtonOk") is { } ok)
            ok.Clicked += _ => Gui.Remove(box);
        Gui.Push(box);
        box.Layout(Ui.Gui.Scale);
    }

    /// <summary>
    /// OpenWESTWOOD: tells the player that Zero Hour and/or Kane's Wrath are not installed (their factions, maps and
    /// campaigns are locked) with buttons to the Ultimate Collection on Steam and on the EA app, and Close.
    /// With no game given, it names every missing game, and shows nothing if none is missing.
    /// </summary>
    public void ShowMissingGames(params Core.Installs.OriginalGame[] games)
    {
        var missing = games.Length > 0 ? games.ToList() : Core.Installs.GameInstalls.Missing().ToList();
        if (missing.Count == 0)
            return;
        string names = string.Join(" and ", missing.Select(Core.Installs.GameInstalls.DisplayName));
        string them = missing.Count > 1 ? "their" : "its";
        string message =
            $"{names}'s artwork {(missing.Count > 1 ? "are" : "is")} not installed in OpenWESTWOOD, so {them} factions, maps and missions are locked. " +
            "You can still play every other game's factions. " +
            $"If you have the game (installed, on another drive or on its disc), press Install to copy {them} artwork into OpenWESTWOOD. " +
            $"If not, get {(missing.Count > 1 ? "them" : "it")} in the Command & Conquer Ultimate Collection on Steam or the EA app.";
        var box = GuiLayout.Load(Ui.Content.FileSystem, "Window/Menus/MessageBox.wnd");
        if (box.Find("MessageBox.wnd:StaticTextTitle") is { } t)
            t.Text = "Games not installed";
        if (box.Find("MessageBox.wnd:StaticTextMessage") is { } m)
            m.Text = message;
        var messageText = box.Find("MessageBox.wnd:StaticTextMessage");
        if (box.Find<GuiPushButton>("MessageBox.wnd:ButtonYes") is { } yes)
        {
            // Install: finds the player's copy, copies the artwork in the background, and shows how far it is.
            yes.Hidden = false;
            yes.Text = "Install";
            bool running = false, finished = false;
            string status = "";
            yes.Clicked += _ =>
            {
                if (finished)
                {
                    Screens.InstallScreen.Restart();
                    return;
                }
                if (running)
                    return;
                running = true;
                status = "Looking for your copy of the game...";
                Task.Run(() =>
                {
                    var lines = new List<string>();
                    Core.Installs.GameInstalls.Rescan();
                    foreach (var game in missing)
                    {
                        var result = Core.Installs.ContentInstaller.Install(game, (done, total, file) =>
                            status = $"Installing {Core.Installs.GameInstalls.DisplayName(game)}: {done / 1e9:0.0} of {total / 1e9:0.0} GB ({file})");
                        lines.Add(result.Message);
                    }
                    finished = missing.All(Core.Installs.GameInstalls.Has);
                    status = string.Join(" ", lines) + (finished ? " Press Restart to use it." : "");
                    running = false;
                });
            };
            perFrame = () =>
            {
                if (status.Length > 0 && messageText != null)
                    messageText.Text = status;
                yes.Text = finished ? "Restart" : running ? "Installing" : "Install";
            };
        }
        // The bottom row: OK, No and Cancel, side by side.
        void Setup(string name, string text, Action click)
        {
            if (box.Find<GuiPushButton>($"MessageBox.wnd:{name}") is not { } button)
                return;
            button.Hidden = false;
            button.Text = text;
            button.Clicked += _ => click();
        }
        Setup("ButtonOk", "Steam", () => OpenUrl(Core.Installs.GameInstalls.SteamUrl));
        Setup("ButtonNo", "EA app", () => OpenUrl(Core.Installs.GameInstalls.EaUrl));
        Setup("ButtonCancel", "Close", () => { perFrame = null; Gui.Remove(box); });
        Gui.Push(box);
        box.Layout(Ui.Gui.Scale);
        // The layout's OK and No buttons touch (they are never shown together in the original): space the three evenly.
        if (box.Find("MessageBox.wnd:ButtonOk") is { } okButton && box.Find("MessageBox.wnd:ButtonNo") is { } noButton)
        {
            okButton.LayoutOffset = new Vector2(-4, 0);
            noButton.LayoutOffset = new Vector2(7, 0);
            box.Layout(Ui.Gui.Scale);
        }
    }

    /// <summary>Opens a web page in the player's browser (Windows, macOS or Linux).</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open", url)
                : new System.Diagnostics.ProcessStartInfo("xdg-open", url);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not open {url}: {e.Message}");
        }
    }

    /// <summary>
    /// The map list popup (the original's SkirmishMapSelectMenu): multiplayer maps by player count and
    /// name, with a preview; OK picks the highlighted map.
    /// </summary>
    public GuiLayout OpenSkirmishMapSelect(string currentMap, Action<string> onSelect, Action onCancel, Func<string, bool>? allow = null)
    {
        const string L = "SkirmishMapSelectMenu.wnd";
        var popup = GuiLayout.Load(Ui.Content.FileSystem, $"Window/Menus/{L}");
        // Skipped like the original: two Generals maps that Zero Hour does not list.
        string[] excluded = ["maps/armored fury/armored fury.map", "maps/scorched earth/scorched earth.map"];
        var maps = MapCache.Maps
            .Where(m => m.IsMultiplayer && m.NameLookupTag.Length > 0 && !excluded.Contains(m.FileName) && (allow?.Invoke(m.FileName) ?? true))
            .OrderBy(m => m.NumPlayers).ThenBy(m => Ui.Strings.Get(m.NameLookupTag), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var list = popup.Find<GuiListBox>($"{L}:ListboxMap");
        var preview = popup.Find($"{L}:WinMapPreview");
        var startButtons = Enumerable.Range(0, Logic.Multiplayer.GameInfo.MaxSlots).Select(i => popup.Find($"{L}:ButtonMapStartPosition{i}")).ToList();
        string selected = currentMap;
        if (list != null)
        {
            list.Items.Clear();
            foreach (var map in maps)
                list.Items.Add(Ui.Strings.Get(map.NameLookupTag));
            list.SelectedIndex = Math.Max(0, maps.FindIndex(m => m.FileName.Equals(Logic.Multiplayer.MapCache.Key(currentMap), StringComparison.OrdinalIgnoreCase)));
            if (maps.Count > 0)
                selected = maps[list.SelectedIndex].FileName;
            list.ScrollToSelected();
            list.Clicked += _ =>
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex < maps.Count)
                    selected = maps[list.SelectedIndex].FileName;
            };
        }
        if (preview != null)
            preview.CustomDraw = (w, context) =>
            {
                var meta = MapCache.Find(selected);
                MapPreviewDrawer.Draw(w, context, meta, MapPreview(selected));
                for (int i = 0; i < startButtons.Count; i++)
                    if (startButtons[i] is { } b)
                    {
                        b.Hidden = meta == null || i >= meta.NumPlayers;
                        b.Enabled = false;
                    }
                if (meta != null)
                    MapPreviewDrawer.PositionStartButtons(w, meta, startButtons);
            };
        if (popup.Find<GuiPushButton>($"{L}:ButtonOK") is { } okButton)
            okButton.Clicked += _ => onSelect(selected);
        if (popup.Find<GuiPushButton>($"{L}:ButtonBack") is { } backButton)
            backButton.Clicked += _ => onCancel();
        Gui.Push(popup);
        popup.Layout(Ui.Gui.Scale);
        return popup;
    }

    /// <summary>
    /// OpenWESTWOOD: the other games' campaigns (the main menu's MORE CAMPAIGNS), in the map list popup: every
    /// mission of every such campaign, with its map preview. A mission plays on through the rest of its campaign.
    /// </summary>
    public GuiLayout OpenMoreCampaigns(Action<Campaign, CampaignMission> onSelect, Action onCancel)
    {
        const string L = "SkirmishMapSelectMenu.wnd";
        var popup = GuiLayout.Load(Ui.Content.FileSystem, $"Window/Menus/{L}");
        var rows = new List<(Campaign Campaign, CampaignMission Mission)>();
        var list = popup.Find<GuiListBox>($"{L}:ListboxMap");
        foreach (var campaign in Campaigns.OtherGames)
        {
            int number = 1;
            for (var mission = campaign.NextMission(null); mission != null && number < 50; mission = campaign.NextMission(mission), number++)
            {
                rows.Add((campaign, mission));
                // (A label such as "CAMPAIGN:USA" is looked up; plain text, as Tiberian Dawn's "GDI", is shown as it is.)
                string Text(string label) => label.Contains(':') ? Ui.Strings.Get(label) : label;
                string location = mission.LocationNameLabel.Length > 0 ? Text(mission.LocationNameLabel) : "";
                list?.Items.Add($"{CampaignManager.GameName(campaign)} {Text(campaign.NameLabel)} {number}" + (location.Length > 0 ? $": {location}" : ""));
            }
        }
        int selected = rows.Count > 0 ? 0 : -1;
        if (list != null)
        {
            list.SelectedIndex = selected;
            list.Clicked += _ => selected = list.SelectedIndex;
        }
        foreach (string hide in new[] { "RadioButtonSystemMaps", "RadioButtonUserMaps" })
            if (popup.Find($"{L}:{hide}") is { } radio)
                radio.Hidden = true;
        foreach (var root in popup.Roots)
        {
            foreach (var title in root.Children.OfType<GuiStaticText>())
                title.Text = "Choose a mission";
            // In the skirmish menu this popup sits on that menu's own panel; over the main menu it needs one of its own.
            root.CustomDraw = (w, context) =>
            {
                var shown = w.Children.Where(c => !c.Hidden).Select(c => c.ScreenRect).ToList();
                if (shown.Count == 0)
                    return;
                float left = shown.Min(r => r.X), top = shown.Min(r => r.Y), right = shown.Max(r => r.Right), bottom = shown.Max(r => r.Bottom);
                var panel = new Rect(left, top, right - left, bottom - top).Inflate(8 * Ui.Gui.Scale.Y);
                context.Batch.FillRect(panel, new Vector4(0, 0, 0, 0.85f));
                context.Batch.DrawRect(panel, new Vector4(0.3f, 0.5f, 1f, 1f));
            };
        }
        var startButtons = Enumerable.Range(0, Logic.Multiplayer.GameInfo.MaxSlots).Select(i => popup.Find($"{L}:ButtonMapStartPosition{i}"));
        foreach (var b in startButtons)
            if (b != null)
                b.Hidden = true;
        if (popup.Find($"{L}:WinMapPreview") is { } preview)
            preview.CustomDraw = (w, context) =>
            {
                if (selected < 0 || selected >= rows.Count)
                    return;
                string map = rows[selected].Mission.Map;
                MapPreviewDrawer.Draw(w, context, MapCache.Find(map), MapPreview(map));
            };
        if (popup.Find<GuiPushButton>($"{L}:ButtonOK") is { } okButton)
            okButton.Clicked += _ =>
            {
                if (selected >= 0 && selected < rows.Count)
                    onSelect(rows[selected].Campaign, rows[selected].Mission);
            };
        if (popup.Find<GuiPushButton>($"{L}:ButtonBack") is { } backButton)
            backButton.Clicked += _ => onCancel();
        Gui.Push(popup);
        popup.Layout(Ui.Gui.Scale);
        return popup;
    }

    /// <summary>Starts the skirmish set up in the menu (the original's reallyDoStart).</summary>
    /// <summary>The network game being set up (the LAN lobby and direct connect make it; its setup screen uses it).</summary>
    public Network.NetSession? NetSession { get; set; }

    /// <summary>Starts a network game built from the host's settings (every machine builds the same one).</summary>
    public void StartNetworkGame(Logic.Multiplayer.GameInfo info, Network.NetSession session)
    {
        var (game, map, local, humans) = Network.NetGame.Build(Ui.Content, MapCache, info, session.Lobby!, session.ClientId);
        session.Attach(game, local, humans);
        var screen = Launcher.ScreenFor(game, map);
        screen.Network = session;
        NetSession = null;
        Play(screen, info);
    }

    public void StartSkirmish(Logic.Multiplayer.GameInfo info)
    {
        var screen = Launcher.StartSkirmish(info, MapCache);
        SkirmishGameInfo = null;
        screen.RestartRequested = () => StartSkirmish(info);
        Play(screen, info);
    }

    /// <summary>Starts the campaign's current mission (the original's doGameStart for GAME_SINGLE_PLAYER).</summary>
    public void StartCampaignMission(string map, Difficulty difficulty)
    {
        if (map.Length == 0)
            return;
        Logic.Log.Info($"Mission: {map} ({difficulty})");
        var screen = Launcher.StartMission(map, difficulty);
        screen.RestartRequested = () => StartCampaignMission(map, difficulty);
        Play(screen, null);
    }

    /// <summary>
    /// Loads a saved game and plays it (from the main menu's Load Game or the in-game menu). Returns why it couldn't, or null.
    /// The campaign and mission it was saved in carry on after it, as they would have.
    /// </summary>
    public string? LoadSavedGame(string path)
    {
        try
        {
            var (screen, header, attachment) = Launcher.LoadGame(path);
            var campaigns = Campaigns;
            if (header.Extra("Campaign") is { Length: > 0 } campaign)
            {
                campaigns.SetCampaignAndMission(campaign, header.Extra("Mission"));
                if (Enum.TryParse<Difficulty>(header.Extra("Difficulty"), out var difficulty))
                    campaigns.GameDifficulty = difficulty;
                if (campaigns.CurrentMap is { Length: > 0 } missionMap)
                    screen.RestartRequested = () => StartCampaignMission(missionMap, campaigns.GameDifficulty);
            }
            else
                campaigns.SetCampaign("");
            var skirmish = attachment as Logic.Multiplayer.GameInfo;
            if (skirmish != null)
                screen.RestartRequested = () => StartSkirmish(skirmish);
            Logic.Log.Info($"Loaded: {path} ({header.Map}, frame {header.Frame})");
            Play(screen, skirmish);
            screen.RestoreView(header);
            return null;
        }
        catch (Exception e) when (e is Logic.Saving.SaveGameException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logic.Log.Warning($"Could not load {path}: {e.Message}");
            return e.Message;
        }
    }

    /// <summary>Leaves for the game; when it ends, a new shell comes back with the score screen.</summary>
    void Play(GameScreen screen, Logic.Multiplayer.GameInfo? skirmish)
    {
        var stack = Shell.StackFiles;
        var campaigns = Campaigns;
        var game = screen.Game;
        if (game.Scripts is { } scripts)
            scripts.OnMissionEnded += victory => campaigns.IsVictorious = victory;
        screen.GameEnded = () =>
            GameWindow.Current?.Switch(new ShellScreen(gl, input, window, Ui, campaigns,
                new FinishedGame(game, game.Players.Local, skirmish, stack)));
        // Saving: the campaign and mission go with the save, and a skirmish's setup (for the score screen after loading).
        screen.SaveExtras = () => campaigns.CurrentCampaign is { } c && campaigns.CurrentMission is { } m && skirmish == null
            ? new Dictionary<string, string> { ["Campaign"] = c.Name, ["Mission"] = m.Name, ["Difficulty"] = campaigns.GameDifficulty.ToString() }
            : new Dictionary<string, string>();
        screen.SaveAttachment ??= skirmish;
        screen.LoadRequested = path =>
        {
            if (LoadSavedGame(path) is { } error)
                screen.ShowNotice(error);
        };
        SwitchTo(screen);
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        Ui.Resize(width, height);
        Gui.Layout(Ui.Gui.Scale);
        Transitions.ScreenSize = new Vector2(width, height);
    }

    // ---- The Dune shell battle ----

    Task<(GameLogic Game, Formats.Maps.MapFile Map)?>? pendingBattle;
    bool duneBattle;
    Vector3 cameraGoal;
    float nextCameraPick, nextOutcomeCheck;
    int baseTurn;

    /// <summary>The battle, once set up: shown behind the menus, watched by its observer.</summary>
    void ShowDuneBattle()
    {
        if (pendingBattle is not { IsCompleted: true } task)
            return;
        pendingBattle = null;
        if (task.IsFaulted || task.Result is not { } battle)
        {
            Console.Error.WriteLine($"Shell battle failed to start: {task.Exception?.InnerException?.Message}");
            return;
        }
        // A new battle replaces the one that just ended (it stayed on screen while this one was set up).
        shellWorld?.Dispose();
        shellGame = battle.Game;
        var viewer = shellGame.Players.Local ?? shellGame.Players.All.First(p => !p.IsNeutral);
        // An observer's view: everything visible, no shroud drawn (Viewer null).
        shellWorld = new WorldView(gl, Ui.Content, shellGame, battle.Map, Ui.Textures) { Viewer = null };
        shellAudio = new Audio.GameAudio(Ui.Audio, shellGame, shellWorld, viewer);
        cameraGoal = DuneShellBattle.Fighting(shellGame) ?? DuneShellBattle.Base(shellGame, baseTurn++)
            ?? new Vector3(shellGame.Terrain.WorldWidth / 2, shellGame.Terrain.WorldHeight / 2, 0);
        shellWorld.Camera.Distance = DuneShellBattle.Number(Ui.Content.Rules, "OpenWestwoodShellCameraDistance", 800);
        shellWorld.LookAt(cameraGoal);
        duneBattle = true;
        nextCameraPick = 6;
        nextOutcomeCheck = 10;
    }

    /// <summary>The camera drifts to where the fighting is; a battle down to one house starts over.</summary>
    void DirectDuneBattle(float seconds)
    {
        if (!duneBattle || shellGame == null || shellWorld == null)
            return;
        var camera = shellWorld.Camera;
        var target = camera.Target;
        // Kept far enough inside the map that the wide view doesn't look past its edges.
        // (The camera looks north: the far, wider edge of the view is the top.)
        float w = shellGame.Terrain.WorldWidth, h = shellGame.Terrain.WorldHeight;
        float marginX = Math.Min(camera.Distance * 0.9f, w / 2), marginTop = Math.Min(camera.Distance * 1.1f, h / 2), marginBottom = Math.Min(camera.Distance * 0.45f, h / 2);
        Vector3 Inside(Vector3 p) => new(Math.Clamp(p.X, marginX, w - marginX), Math.Clamp(p.Y, marginBottom, Math.Max(marginBottom, h - marginTop)), target.Z);
        var goal = Inside(cameraGoal);
        // Once it has arrived (where it can get to), the camera stays a while, then moves on: to the fighting, or,
        // if that is where it already is, the next house's base, so it keeps touring.
        bool arrived = Vector2.Distance(new Vector2(target.X, target.Y), new Vector2(goal.X, goal.Y)) < 30;
        if (arrived)
            nextCameraPick -= seconds;
        if (nextCameraPick <= 0)
        {
            nextCameraPick = DuneShellBattle.Number(Ui.Content.Rules, "OpenWestwoodShellCameraHold", 10);
            var next = DuneShellBattle.Fighting(shellGame);
            if (next == null || Vector2.Distance(new Vector2(Inside(next.Value).X, Inside(next.Value).Y), new Vector2(target.X, target.Y)) < 300)
                next = DuneShellBattle.Base(shellGame, baseTurn++);
            if (next is { } chosen)
                cameraGoal = chosen;
            goal = Inside(cameraGoal);
        }
        // A steady glide (easing only over the last stretch), not a dash that crawls to a stop.
        var toGoal = goal - target;
        float remaining = toGoal.Length();
        if (remaining > 0.5f)
        {
            float speed = DuneShellBattle.Number(Ui.Content.Rules, "OpenWestwoodShellCameraSpeed", 220) * Math.Clamp(remaining / 150f, 0.25f, 1f);
            camera.Target = target + toGoal / remaining * Math.Min(remaining, speed * seconds);
        }
        nextOutcomeCheck -= seconds;
        if (nextOutcomeCheck <= 0)
        {
            nextOutcomeCheck = 10;
            // Down to one house: the next battle is set up behind this one, which plays on until it is ready.
            if (pendingBattle == null && DuneShellBattle.HousesLeft(shellGame) < 2)
                pendingBattle = DuneShellBattle.Start(Ui.Content);
        }
    }

    /// <summary>Runs every frame while set (the install message's progress).</summary>
    Action? perFrame;

    public void Update(float seconds)
    {
        perFrame?.Invoke();
        ShowDuneBattle();
        DirectDuneBattle(seconds);
        // (Once the shell battle is up, not alongside it: both at once slowed the battle's start.)
        if (mapCache == null && mapCacheLoading == null && pendingBattle == null)
        {
            var content = Ui.Content;
            mapCacheLoading = Task.Run(() => BuildMapCache(content));
        }
        if (shellWorld != null)
        {
            shellWorld.ViewportSize = new Vector2(width, height);
            shellWorld.Update(seconds);
            director?.Update(seconds);
            shellAudio?.Update();
        }
        else
            Ui.Audio.Update();
        Shell.Update(seconds);
        Transitions.Update(seconds);
        var cursors = Ui.CursorsFor(input);
        cursors.Set("Arrow");
        cursors.Update(seconds);
        debugClock += seconds;
        if (MissionAtStart is { } start && debugClock > 0.5f && start.Split('/') is [var campaignName, var missionName])
        {
            MissionAtStart = null;
            Campaigns.SetCampaignAndMission(campaignName, missionName);
            StartCampaignMission(Campaigns.CurrentMap, Difficulty.Normal);
            return;
        }
        if (LoadAtStart is { } load && debugClock > 0.5f)
        {
            LoadAtStart = null;
            if (LoadSavedGame(load) is { } error)
                ShowMessageBox("Load Game", error);
            return;
        }
        while (DebugClicks.Count > 0 && DebugClicks.Peek().At <= debugClock)
        {
            var (name, _) = DebugClicks.Dequeue();
            // Like a real mouse: press and release at the button's centre, so whatever is on top there gets the click.
            if (Gui.Find(name) is GuiPushButton or GuiComboBox && Gui.Find(name) is { } button)
            {
                var centre = new Vector2(button.ScreenRect.X + button.ScreenRect.Width / 2, button.ScreenRect.Y + button.ScreenRect.Height / 2);
                var hit = Gui.HitTest(centre);
                if (hit != button)
                    Console.WriteLine($"--click: {name} is covered by {hit?.Name ?? "nothing"} (hidden={button.Hidden}, visible={button.IsVisible}; layouts {string.Join(", ", Gui.Layouts.Select(l => l.FileName + (l.Hidden ? "(hidden)" : "")))}; at {centre}; chain {string.Join(" < ", Chain(hit))}; medium chain {string.Join(" < ", Chain(button))})");
                static IEnumerable<string> Chain(GuiWindow? w) { for (; w != null; w = w.Parent) yield return $"{w.Name}{(w.Hidden ? "(H)" : "")}"; }
                Gui.MouseDown(centre);
                Gui.MouseUp(centre);
            }
            else
                Console.WriteLine($"--click: no button {name}");
        }
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        shellWorld?.Render(seconds, width / (float)height);
        var batch = Ui.Batch;
        batch.Begin(width, height);
        Gui.Draw(Ui.Gui);
        Transitions.Draw(Ui.Gui);
        batch.End();
    }

    public void Dispose()
    {
        foreach (var action in unsubscribe)
            action();
        shellWorld?.Dispose();
    }
}
