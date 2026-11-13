using System.Numerics;
using OpenWestwood.Client.Platform;
using OpenWestwood.Client.Shell;
using OpenWestwood.Client.UI;
using OpenWestwood.Logic.SoleSurvivor;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenWestwood.Client.SoleSurvivor;

/// <summary>
/// OpenWESTWOOD: Sole Survivor Remix's setup: the mode (Freeplay, Zone Defense, Capture the Flag, Classic), the map (any
/// game's), the kind of unit everyone plays (vehicles, infantry or naval; aircraft are allowed besides), the game and faction
/// to play (the unit is chosen in the match), how many computer players, the lives and the computer's difficulty. The map's
/// picture shows its start points: click one to start there (again for any). Start makes the match (SsrMatchSetup) and goes
/// into it; Back returns to the main menu.
/// </summary>
public sealed class SsrSetupScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly IWindow window;
    readonly UiResources ui;
    readonly Logic.Multiplayer.MapCache mapCache;
    readonly List<Action> unsubscribe = new();
    readonly SsrProfile profile = SsrProfile.Current;
    readonly List<(string File, string Name, int Players)> maps;
    readonly IReadOnlyList<SsrFaction> factions;
    int width = 1280, height = 720;
    Vector2 mouse;

    int mode, map, unitType, game, faction, computers = 3, allies = 1, enemies = 2, lives, timeLimit, killLimit, difficulty = 1, start = -1;
    static readonly string[] ModeNames = ["FREEPLAY", "ZONE DEFENSE", "CAPTURE THE FLAG", "CLASSIC", "TEAM BATTLE"];
    static readonly int[] TimeLimits = [0, 5, 10, 15, 20, 30, 45, 60];
    static readonly int[] KillLimits = [0, 5, 10, 15, 20, 25, 30, 50, 75, 100];
    static readonly (string Name, SsrCategory Category)[] UnitTypes = [("VEHICLES", SsrCategory.Vehicle), ("INFANTRY", SsrCategory.Infantry), ("NAVAL", SsrCategory.Naval)];
    static readonly int[] LivesChoices = [0, 1, 3, 5, 10];
    static readonly string[] DifficultyNames = ["EASY", "NORMAL", "HARD"];
    const int RowCount = 11;
    const int RowPitch = 40, Top = 140;
    SsrMode Mode => (SsrMode)mode;
    bool TeamBattle => Mode == SsrMode.TeamBattle;
    bool KillLimitApplies => Mode is SsrMode.Freeplay or SsrMode.TeamBattle;
    string? status;

    public SsrSetupScreen(GL gl, IInputContext input, IWindow window, UiResources ui, Logic.Multiplayer.MapCache mapCache)
    {
        this.gl = gl;
        this.input = input;
        this.window = window;
        this.ui = ui;
        this.mapCache = mapCache;
        maps = mapCache.Maps.Where(m => m.IsMultiplayer && m.NumPlayers >= 2 && m.NameLookupTag.Length > 0)
            .Select(m => (m.FileName, ui.Strings.Get(m.NameLookupTag), m.NumPlayers))
            .OrderBy(m => m.NumPlayers).ThenBy(m => m.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        factions = SsrCatalog.Factions(ui.Content.Rules, new Logic.Templates.TemplateStore(ui.Content.Rules),
            node => Logic.Multiplayer.GameInfo.IsInstalledFaction(node));
        // The setup last used.
        int Index(string key, int count) => profile.Setup.TryGetValue(key, out var v) && int.TryParse(v, out int i) ? Math.Clamp(i, 0, Math.Max(0, count - 1)) : 0;
        mode = Index("Mode", ModeNames.Length);
        map = maps.FindIndex(m => profile.Setup.TryGetValue("Map", out var file) && m.File == file) is var found and >= 0 ? found : 0;
        unitType = Index("UnitType", UnitTypes.Length);
        game = Index("Game", Games.Count);
        faction = Index("Faction", 99);
        computers = profile.Setup.TryGetValue("Computers", out var c) && int.TryParse(c, out int n) ? Math.Clamp(n, 1, SsrMatchSetup.MaxComputers) : 3;
        int Number(string key, int fallback, int min, int max) => profile.Setup.TryGetValue(key, out var v) && int.TryParse(v, out int x) ? Math.Clamp(x, min, max) : fallback;
        allies = Number("Allies", 1, 0, SsrMatchSetup.MaxComputers - 1);
        enemies = Number("Enemies", 2, 1, SsrMatchSetup.MaxComputers - allies);
        timeLimit = Index("TimeLimit", TimeLimits.Length);
        killLimit = Index("KillLimit", KillLimits.Length);
        lives = Index("Lives", LivesChoices.Length);
        difficulty = profile.Setup.ContainsKey("Difficulty") ? Index("Difficulty", 3) : 1;
        start = profile.Setup.TryGetValue("Start", out var s) && int.TryParse(s, out int st) ? st : -1;
        foreach (var m in input.Mice)
        {
            Action<IMouse, Vector2> move = (_, p) => mouse = p * GameWindow.MouseScale;
            Action<IMouse, MouseButton> down = (_, b) => { if (b == MouseButton.Left) Click(mouse); };
            Action<IMouse, ScrollWheel> scroll = (_, w) => { if (listOpen) ScrollList(w.Y > 0 ? -3 : w.Y < 0 ? 3 : 0); };
            m.MouseMove += move;
            m.MouseDown += down;
            m.Scroll += scroll;
            unsubscribe.Add(() => { m.MouseMove -= move; m.MouseDown -= down; m.Scroll -= scroll; });
        }
        foreach (var k in input.Keyboards)
        {
            Action<IKeyboard, Key, int> key = (_, kk, _) =>
            {
                if (listOpen)
                {
                    // The open map list: up and down move through it, Enter takes the map, Escape closes it.
                    if (kk == Key.Escape) listOpen = false;
                    else if (kk == Key.Enter) { if (hover >= 0) PickMap(hover); listOpen = false; }
                    else if (kk is Key.Up or Key.Down) { hover = Math.Clamp((hover < 0 ? map : hover) + (kk == Key.Up ? -1 : 1), 0, maps.Count - 1); KeepInView(hover); }
                    return;
                }
                if (kk == Key.Escape) Back(); else if (kk == Key.Enter) Start();
            };
            k.KeyDown += key;
            unsubscribe.Add(() => k.KeyDown -= key);
        }
    }

    SsrCategory Category => UnitTypes[Math.Clamp(unitType, 0, UnitTypes.Length - 1)].Category;

    /// <summary>The factions that have the chosen kind of unit (one to fight with).</summary>
    List<SsrFaction> Playable => factions.Where(f => f.Units.Any(u => u.Category == Category)).ToList();

    List<string> Games => SsrCatalog.Games.Where(g => Playable.Any(f => f.Game == g)).ToList();

    List<SsrFaction> GameFactions
    {
        get
        {
            var games = Games;
            return games.Count == 0 ? [] : Playable.Where(f => f.Game == games[Math.Clamp(game, 0, games.Count - 1)]).ToList();
        }
    }

    Logic.Multiplayer.MapMetaData? MapInfo => maps.Count > 0 ? mapCache.Find(maps[map].File) : null;

    // ---- The map list (a drop-down, as the skirmish menu's map list: by number of players, then name) ----

    bool listOpen;
    int listTop, hover = -1;
    const int ListRows = 13;
    Rect ListRect => new(Row(1).X + 200 * Scale, Row(1).Y + Row(1).Height, Row(1).Width - 200 * Scale, ListRows * 24 * Scale);
    Rect ListEntry(int k) => new(ListRect.X, ListRect.Y + k * 24 * Scale, ListRect.Width - 12 * Scale, 24 * Scale);

    /// <summary>The map shown in the preview: the one the mouse is over in the open list, else the chosen one.</summary>
    Logic.Multiplayer.MapMetaData? PreviewInfo => listOpen && hover >= 0 && hover < maps.Count ? mapCache.Find(maps[hover].File) : MapInfo;

    void ScrollList(int by) => listTop = Math.Clamp(listTop + by, 0, Math.Max(0, maps.Count - ListRows));

    void KeepInView(int index)
    {
        if (index < listTop) listTop = index;
        else if (index >= listTop + ListRows) listTop = index - ListRows + 1;
        ScrollList(0);
    }

    void PickMap(int index)
    {
        if (index == map)
            return;
        map = index;
        start = -1;
    }

    void OpenList()
    {
        listOpen = true;
        hover = map;
        listTop = map - ListRows / 2;
        ScrollList(0);
    }

    /// <summary>A click while the list is open: on a map takes it; anywhere else just closes the list.</summary>
    void ClickList(Vector2 point)
    {
        var r = ListRect;
        if (r.Contains(point))
        {
            if (point.X > r.X + r.Width - 12 * Scale)
            {
                // The scroll bar: a page up or down.
                ScrollList(point.Y < r.Y + r.Height / 2 ? -ListRows : ListRows);
                return;
            }
            int k = listTop + (int)((point.Y - r.Y) / (24 * Scale));
            if (k >= 0 && k < maps.Count)
                PickMap(k);
        }
        listOpen = false;
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        ui.Resize(width, height);
    }

    public void Update(float seconds) { }

    float Scale => height / 720f;
    Rect Row(int i) => new(width / 2f - 600 * Scale, (Top + i * RowPitch) * Scale, 600 * Scale, (RowPitch - 6) * Scale);
    float Below => Top + RowCount * RowPitch;
    Rect StartButton => new(width / 2f - 290 * Scale, (Below + 6) * Scale, 290 * Scale, 44 * Scale);
    Rect BackButton => new(width / 2f - 600 * Scale, (Below + 6) * Scale, 290 * Scale, 44 * Scale);
    Rect PreviewRect => new(width / 2f + 40 * Scale, Top * Scale, 520 * Scale, 390 * Scale);

    /// <summary>The map's start points on its picture (index, where).</summary>
    IEnumerable<(int Index, Vector2 At)> StartSpots(Logic.Multiplayer.MapMetaData? meta = null)
    {
        meta ??= MapInfo;
        if (meta == null)
            yield break;
        for (int i = 0; i < meta.NumPlayers; i++)
            if (meta.StartPosition(i) is { } p)
                yield return (i, MapPreviewDrawer.MapToScreen(PreviewRect, meta, p));
    }

    void Click(Vector2 point)
    {
        if (listOpen)
        {
            ClickList(point);
            return;
        }
        if (Row(1).Contains(point))
        {
            OpenList();
            return;
        }
        if (StartButton.Contains(point))
        {
            Start();
            return;
        }
        if (BackButton.Contains(point))
        {
            Back();
            return;
        }
        // A start point on the map: start there (clicked again: anywhere).
        foreach (var (index, at) in StartSpots())
            if (Vector2.Distance(at, point) < 14 * Scale)
            {
                start = start == index ? -1 : index;
                return;
            }
        for (int i = 0; i < RowCount; i++)
        {
            var r = Row(i);
            if (!r.Contains(point))
                continue;
            int step = point.X < r.X + r.Width / 2 ? -1 : 1;
            int Wrap(int value, int count) => count == 0 ? 0 : ((value + step) % count + count) % count;
            int Stepped(int value) => value + step * (step > 0 ? (value >= 10 ? 5 : 1) : (value > 10 ? 5 : 1));
            switch (i)
            {
                case 0: mode = Wrap(mode, ModeNames.Length); break;
                case 2: unitType = Wrap(unitType, UnitTypes.Length); game = Math.Clamp(game, 0, Math.Max(0, Games.Count - 1)); faction = 0; break;
                case 3: game = Wrap(game, Games.Count); faction = 0; break;
                case 4: faction = Wrap(faction, GameFactions.Count); break;
                case 5:
                    // One at a time to ten, then fives. (Team Battle: the computers on the person's team.)
                    if (TeamBattle)
                        allies = Math.Clamp(Stepped(allies), 0, SsrMatchSetup.MaxComputers - enemies);
                    else
                        computers = Math.Clamp(Stepped(computers), 1, SsrMatchSetup.MaxComputers);
                    break;
                case 6:
                    if (TeamBattle)
                        enemies = Math.Clamp(Stepped(enemies), 1, SsrMatchSetup.MaxComputers - allies);
                    break;
                case 7: lives = Wrap(lives, LivesChoices.Length); break;
                case 8: timeLimit = Wrap(timeLimit, TimeLimits.Length); break;
                case 9:
                    if (KillLimitApplies)
                        killLimit = Wrap(killLimit, KillLimits.Length);
                    break;
                case 10: difficulty = Wrap(difficulty, 3); break;
            }
        }
    }

    void Back() => GameWindow.Current?.Switch(new ShellScreen(gl, input, window, ui));

    void Start()
    {
        var chosen = GameFactions;
        if (maps.Count == 0 || chosen.Count == 0)
        {
            status = "No map or faction available.";
            return;
        }
        var setup = new SsrMatchSetup
        {
            Mode = (SsrMode)mode,
            Map = maps[map].File,
            MapPlayers = maps[map].Players,
            MapInfo = MapInfo,
            UnitType = Category,
            Start = start,
            Faction = chosen[Math.Clamp(faction, 0, chosen.Count - 1)].Name,
            Computers = computers,
            AlliedComputers = allies,
            EnemyComputers = enemies,
            TimeLimitMinutes = TimeLimits[timeLimit],
            KillLimit = KillLimitApplies ? KillLimits[killLimit] : 0,
            Lives = LivesChoices[lives],
            Difficulty = (Logic.Scripting.Difficulty)difficulty,
        };
        profile.Setup["Mode"] = mode.ToString();
        profile.Setup["Map"] = maps[map].File;
        profile.Setup["UnitType"] = unitType.ToString();
        profile.Setup["Game"] = game.ToString();
        profile.Setup["Faction"] = faction.ToString();
        profile.Setup["Computers"] = computers.ToString();
        profile.Setup["Allies"] = allies.ToString();
        profile.Setup["Enemies"] = enemies.ToString();
        profile.Setup["TimeLimit"] = timeLimit.ToString();
        profile.Setup["KillLimit"] = killLimit.ToString();
        profile.Setup["Lives"] = lives.ToString();
        profile.Setup["Difficulty"] = difficulty.ToString();
        profile.Setup["Start"] = start.ToString();
        profile.Save();
        GameWindow.Current?.Switch(Play(gl, input, window, ui, setup));
    }

    /// <summary>Makes the match and its game screen, with Sole Survivor's controls on it.</summary>
    public static InGame.GameScreen Play(GL gl, IInputContext input, IWindow window, UiResources ui, SsrMatchSetup setup)
    {
        setup.MapInfo ??= ShellScreen.BuildMapCache(ui.Content).Find(setup.Map);
        if (setup.MapInfo is { NumPlayers: > 0 } info)
            setup.MapPlayers = info.NumPlayers;
        var (game, map, match) = setup.Build(ui.Content);
        ArtPreloader.PreloadFor(game, ui.Content, setup.Map);
        var screen = new InGame.GameScreen(gl, input, ui, game, map);
        void ToMenu() => GameWindow.Current?.Switch(new ShellScreen(gl, input, window, ui));
        var me = match.Players.First(p => !p.IsComputer);
        _ = new SsrController(screen, match, me, ToMenu, setup.MapInfo, ShellScreen.LoadMapPreview(ui, setup.Map)) { AutoUnit = setup.AutoUnit };
        // (Classic ends by the usual victory, the others by the match's own.)
        screen.GameEnded = ToMenu;
        return screen;
    }

    // ---- Drawing ----

    static readonly Vector4 Gold = new(1f, 0.82f, 0.35f, 1), Shadow = new(0, 0, 0, 1), Frame = new(0.85f, 0.65f, 0.25f, 1);

    void Text(SpriteBatch batch, string text, float x, float y, float size, Vector4 color, bool center = false)
    {
        var font = ui.Fonts.Get("Arial", Math.Max(7, size * Scale), bold: true);
        float w = center ? font.Measure(text) : 0;
        font.DrawWithShadow(batch, text, new Vector2(MathF.Round(x - w / 2), MathF.Round(y)), color, Shadow);
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0.02f, 0.02f, 0.03f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var batch = ui.Batch;
        batch.Begin(width, height);
        Text(batch, "OPENWESTWOOD", width / 2f, 40 * Scale, 18, new Vector4(0.8f, 0.8f, 0.8f, 1), center: true);
        Text(batch, "SOLE SURVIVOR REMIX", width / 2f, 66 * Scale, 34, Gold, center: true);
        Text(batch, $"CREDITS {profile.Credits}    KILLS {profile.Kills}", width / 2f, 114 * Scale, 12, new Vector4(0.75f, 0.75f, 0.75f, 1), center: true);
        var chosen = GameFactions;
        var games = Games;
        var factionNow = chosen.Count > 0 ? chosen[Math.Clamp(faction, 0, chosen.Count - 1)] : null;
        string[] labels = ["MODE", "MAP", "UNIT TYPE", "GAME", "FACTION", TeamBattle ? "COMPUTERS ON MY TEAM" : "COMPUTER PLAYERS",
            "COMPUTERS ON OTHER TEAM", "LIVES", "TIME LIMIT", "KILL LIMIT", "COMPUTER SKILL"];
        // Rows that don't apply to this mode.
        bool[] off = [false, false, false, false, false, false, !TeamBattle, false, false, !KillLimitApplies, false];
        string[] values =
        [
            ModeNames[mode],
            maps.Count > 0 ? $"{maps[map].Name} ({maps[map].Players})" : "-",
            Category == SsrCategory.Infantry ? UnitTypes[unitType].Name : UnitTypes[unitType].Name + " (+ AIRCRAFT)",
            games.Count > 0 ? games[Math.Clamp(game, 0, games.Count - 1)] : "-",
            factionNow != null ? ui.Strings.Get(factionNow.DisplayName) : "-",
            TeamBattle ? allies.ToString() : computers.ToString(),
            TeamBattle ? enemies.ToString() : "TEAM BATTLE ONLY",
            LivesChoices[lives] == 0 ? "UNLIMITED" : LivesChoices[lives].ToString(),
            TimeLimits[timeLimit] == 0 ? "NONE" : $"{TimeLimits[timeLimit]} MINUTES (MOST KILLS WINS)",
            !KillLimitApplies ? "FREEPLAY / TEAM BATTLE ONLY" : KillLimits[killLimit] == 0 ? "NONE" : $"FIRST TO {KillLimits[killLimit]}",
            DifficultyNames[difficulty],
        ];
        for (int i = 0; i < labels.Length; i++)
        {
            var r = Row(i);
            batch.FillRect(r, r.Contains(mouse) ? new Vector4(0.14f, 0.11f, 0.05f, 0.95f) : new Vector4(0.06f, 0.06f, 0.08f, 0.95f));
            batch.DrawRect(r, Frame);
            Text(batch, labels[i], r.X + 14 * Scale, r.Y + 9 * Scale, 11, new Vector4(0.7f, 0.7f, 0.7f, 1));
            if (off[i])
            {
                Text(batch, values[i], r.X + 410 * Scale, r.Y + 9 * Scale, 10, new Vector4(0.45f, 0.45f, 0.45f, 1), center: true);
                continue;
            }
            if (i == 1)
            {
                // A drop-down: the arrow at its right.
                float ax = r.X + r.Width - 30 * Scale, ay = r.Y + 12 * Scale;
                batch.FillTriangle(new Vector2(ax, ay), new Vector2(ax + 16 * Scale, ay), new Vector2(ax + 8 * Scale, ay + 10 * Scale), Gold);
            }
            else
            {
                Text(batch, "<", r.X + 220 * Scale, r.Y + 6 * Scale, 15, Gold);
                Text(batch, ">", r.X + r.Width - 26 * Scale, r.Y + 6 * Scale, 15, Gold);
            }
            Text(batch, values[i], r.X + 410 * Scale, r.Y + 8 * Scale, 12, Vector4.One, center: true);
            // The faction's flag.
            if (i == 4 && factionNow != null && ui.Content.Rules.Get("PlayerTemplates", factionNow.Name) is { } node
                && (node.GetString("LobbyFlag") is { Length: > 0 } flag ? flag : node.GetString("SideIconImage")) is { Length: > 0 } icon
                && ui.Images.Find(icon) is { } image)
            {
                float h = 26 * Scale, w = Math.Min(h * 2, image.PixelWidth * h / Math.Max(1, image.PixelHeight));
                ui.Images.Draw(batch, image, new Rect(r.X + 250 * Scale, r.Y + 4 * Scale, w, h));
            }
        }
        DrawPreview(batch);
        foreach (var (button, label) in new[] { (StartButton, "START"), (BackButton, "BACK") })
        {
            batch.FillRect(button, button.Contains(mouse) ? new Vector4(0.45f, 0.32f, 0.08f, 1) : new Vector4(0.2f, 0.15f, 0.05f, 1));
            batch.DrawRect(button, Frame, 2);
            Text(batch, label, button.X + button.Width / 2, button.Y + 12 * Scale, 16, Vector4.One, center: true);
        }
        string help = (SsrMode)mode switch
        {
            SsrMode.ZoneDefense => "Two teams, three zones each. Stand in the other team's zones to score; keep them out of yours.",
            SsrMode.CaptureTheFlag => "Two teams. Bring the other team's flag to yours while yours is at home.",
            SsrMode.Classic => "Everyone also has a base: build and harvest (money only from harvesters). Last side standing wins.",
            SsrMode.TeamBattle => "Your team against the other. Every kill counts for the team.",
            _ => "Everyone for themselves. Kill to earn credits; buy better units and upgrades.",
        };
        Text(batch, help, width / 2f, (Below + 60) * Scale, 11, new Vector4(0.75f, 0.75f, 0.75f, 1), center: true);
        Text(batch, Category == SsrCategory.Infantry ? "Everyone plays infantry (no aircraft in an infantry match)."
            : "Everyone plays the chosen unit type; aircraft may be taken any time.", width / 2f, (Below + 76) * Scale, 11,
            new Vector4(0.75f, 0.75f, 0.75f, 1), center: true);
        if (status != null)
            Text(batch, status, width / 2f, (Below + 92) * Scale, 12, new Vector4(1, 0.4f, 0.3f, 1), center: true);
        // (The open map list over everything.)
        if (listOpen)
            DrawList(batch);
        batch.End();
    }

    void DrawList(SpriteBatch batch)
    {
        var r = ListRect;
        batch.FillRect(r, new Vector4(0.03f, 0.03f, 0.04f, 0.98f));
        if (r.Contains(mouse) && mouse.X <= r.X + r.Width - 12 * Scale)
            hover = listTop + (int)((mouse.Y - r.Y) / (24 * Scale));
        if (hover >= maps.Count)
            hover = -1;
        for (int k = 0; k < ListRows && listTop + k < maps.Count; k++)
        {
            int index = listTop + k;
            var e = ListEntry(k);
            if (index == hover)
                batch.FillRect(e, new Vector4(0.45f, 0.32f, 0.08f, 1));
            else if (index == map)
                batch.FillRect(e, new Vector4(0.2f, 0.15f, 0.05f, 1));
            Text(batch, maps[index].Name, e.X + 10 * Scale, e.Y + 5 * Scale, 11, index == map ? Gold : Vector4.One);
            Text(batch, $"{maps[index].Players} PLAYERS", e.X + e.Width - 80 * Scale, e.Y + 5 * Scale, 10, new Vector4(0.7f, 0.7f, 0.7f, 1));
        }
        // The scroll bar.
        var bar = new Rect(r.X + r.Width - 10 * Scale, r.Y, 10 * Scale, r.Height);
        batch.FillRect(bar, new Vector4(0.1f, 0.1f, 0.12f, 1));
        if (maps.Count > ListRows)
        {
            float h = r.Height * ListRows / maps.Count, y = r.Y + (r.Height - h) * listTop / Math.Max(1, maps.Count - ListRows);
            batch.FillRect(new Rect(bar.X + 1, y, bar.Width - 2, h), Frame);
        }
        batch.DrawRect(r, Frame);
    }

    void DrawPreview(SpriteBatch batch)
    {
        var r = PreviewRect;
        batch.FillRect(r, new Vector4(0, 0, 0, 1));
        if (PreviewInfo is { } meta)
        {
            var (ul, lr) = MapPreviewDrawer.FindDrawPositions(r, meta);
            var inner = new Rect(ul.X, ul.Y, lr.X - ul.X, lr.Y - ul.Y);
            if (ShellScreen.LoadMapPreview(ui, meta.FileName) is { } picture)
                batch.DrawTexture(picture, inner);
            else
                batch.FillRect(inner, new Vector4(0.2f, 0.2f, 0.2f, 1));
            foreach (var (index, at) in StartSpots(meta))
            {
                bool chosen = meta == MapInfo && index == start, over = Vector2.Distance(at, mouse) < 14 * Scale;
                float size = (chosen ? 22 : 18) * Scale;
                var box = new Rect(at.X - size / 2, at.Y - size / 2, size, size);
                batch.FillRect(box, chosen ? new Vector4(0.85f, 0.6f, 0.1f, 1) : over ? new Vector4(0.35f, 0.3f, 0.2f, 1) : new Vector4(0.1f, 0.1f, 0.12f, 0.9f));
                batch.DrawRect(box, chosen ? Vector4.One : Frame, chosen ? 2 : 1);
                Text(batch, (index + 1).ToString(), at.X, at.Y - 8 * Scale, 11, Vector4.One, center: true);
            }
        }
        batch.DrawRect(r, Frame);
        string where = start >= 0 ? $"YOU START AT {start + 1}" : "YOU START ANYWHERE";
        if ((SsrMode)mode is SsrMode.ZoneDefense or SsrMode.CaptureTheFlag or SsrMode.TeamBattle)
            where = start >= 0 ? $"YOUR TEAM'S BASE: {start + 1}" : "YOUR TEAM'S BASE: ANY";
        Text(batch, where, r.X + r.Width / 2, r.Y + r.Height + 8 * Scale, 12, Gold, center: true);
        Text(batch, "Click a start point to begin there.", r.X + r.Width / 2, r.Y + r.Height + 26 * Scale, 10, new Vector4(0.7f, 0.7f, 0.7f, 1), center: true);
    }

    public void Dispose()
    {
        foreach (var action in unsubscribe)
            action();
    }
}
