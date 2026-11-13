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

    int mode, map, unitType, game, faction, computers = 3, lives, difficulty = 1, start = -1;
    static readonly string[] ModeNames = ["FREEPLAY", "ZONE DEFENSE", "CAPTURE THE FLAG", "CLASSIC"];
    static readonly (string Name, SsrCategory Category)[] UnitTypes = [("VEHICLES", SsrCategory.Vehicle), ("INFANTRY", SsrCategory.Infantry), ("NAVAL", SsrCategory.Naval)];
    static readonly int[] LivesChoices = [0, 1, 3, 5, 10];
    static readonly string[] DifficultyNames = ["EASY", "NORMAL", "HARD"];
    const int RowCount = 8;
    string? status;

    public SsrSetupScreen(GL gl, IInputContext input, IWindow window, UiResources ui, Logic.Multiplayer.MapCache mapCache)
    {
        this.gl = gl;
        this.input = input;
        this.window = window;
        this.ui = ui;
        this.mapCache = mapCache;
        maps = mapCache.Maps.Where(m => m.IsMultiplayer && m.NumPlayers >= 2 && m.NameLookupTag.Length > 0)
            .Select(m => (m.FileName, ui.Strings.Get(m.NameLookupTag), m.NumPlayers)).OrderBy(m => m.Item2, StringComparer.OrdinalIgnoreCase).ToList();
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
        lives = Index("Lives", LivesChoices.Length);
        difficulty = profile.Setup.ContainsKey("Difficulty") ? Index("Difficulty", 3) : 1;
        start = profile.Setup.TryGetValue("Start", out var s) && int.TryParse(s, out int st) ? st : -1;
        foreach (var m in input.Mice)
        {
            Action<IMouse, Vector2> move = (_, p) => mouse = p * GameWindow.MouseScale;
            Action<IMouse, MouseButton> down = (_, b) => { if (b == MouseButton.Left) Click(mouse); };
            m.MouseMove += move;
            m.MouseDown += down;
            unsubscribe.Add(() => { m.MouseMove -= move; m.MouseDown -= down; });
        }
        foreach (var k in input.Keyboards)
        {
            Action<IKeyboard, Key, int> key = (_, kk, _) => { if (kk == Key.Escape) Back(); else if (kk == Key.Enter) Start(); };
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

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        ui.Resize(width, height);
    }

    public void Update(float seconds) { }

    float Scale => height / 720f;
    Rect Row(int i) => new(width / 2f - 600 * Scale, (150 + i * 50) * Scale, 600 * Scale, 40 * Scale);
    Rect StartButton => new(width / 2f - 290 * Scale, (150 + RowCount * 50 + 14) * Scale, 290 * Scale, 46 * Scale);
    Rect BackButton => new(width / 2f - 600 * Scale, (150 + RowCount * 50 + 14) * Scale, 290 * Scale, 46 * Scale);
    Rect PreviewRect => new(width / 2f + 40 * Scale, 150 * Scale, 520 * Scale, 390 * Scale);

    /// <summary>The map's start points on its picture (index, where).</summary>
    IEnumerable<(int Index, Vector2 At)> StartSpots()
    {
        if (MapInfo is not { } meta)
            yield break;
        for (int i = 0; i < meta.NumPlayers; i++)
            if (meta.StartPosition(i) is { } p)
                yield return (i, MapPreviewDrawer.MapToScreen(PreviewRect, meta, p));
    }

    void Click(Vector2 point)
    {
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
            switch (i)
            {
                case 0: mode = Wrap(mode, ModeNames.Length); break;
                case 1: map = Wrap(map, maps.Count); start = -1; break;
                case 2: unitType = Wrap(unitType, UnitTypes.Length); game = Math.Clamp(game, 0, Math.Max(0, Games.Count - 1)); faction = 0; break;
                case 3: game = Wrap(game, Games.Count); faction = 0; break;
                case 4: faction = Wrap(faction, GameFactions.Count); break;
                case 5:
                    // One at a time to ten, then fives.
                    int by = step > 0 ? (computers >= 10 ? 5 : 1) : (computers > 10 ? 5 : 1);
                    computers = Math.Clamp(computers + step * by, 1, SsrMatchSetup.MaxComputers);
                    break;
                case 6: lives = Wrap(lives, LivesChoices.Length); break;
                case 7: difficulty = Wrap(difficulty, 3); break;
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
            Lives = LivesChoices[lives],
            Difficulty = (Logic.Scripting.Difficulty)difficulty,
        };
        profile.Setup["Mode"] = mode.ToString();
        profile.Setup["Map"] = maps[map].File;
        profile.Setup["UnitType"] = unitType.ToString();
        profile.Setup["Game"] = game.ToString();
        profile.Setup["Faction"] = faction.ToString();
        profile.Setup["Computers"] = computers.ToString();
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
        string[] labels = ["MODE", "MAP", "UNIT TYPE", "GAME", "FACTION", "COMPUTER PLAYERS", "LIVES", "COMPUTER SKILL"];
        string[] values =
        [
            ModeNames[mode],
            maps.Count > 0 ? $"{maps[map].Name} ({maps[map].Players})" : "-",
            Category == SsrCategory.Infantry ? UnitTypes[unitType].Name : UnitTypes[unitType].Name + " (+ AIRCRAFT)",
            games.Count > 0 ? games[Math.Clamp(game, 0, games.Count - 1)] : "-",
            factionNow != null ? ui.Strings.Get(factionNow.DisplayName) : "-",
            computers.ToString(),
            LivesChoices[lives] == 0 ? "UNLIMITED" : LivesChoices[lives].ToString(),
            DifficultyNames[difficulty],
        ];
        for (int i = 0; i < labels.Length; i++)
        {
            var r = Row(i);
            batch.FillRect(r, r.Contains(mouse) ? new Vector4(0.14f, 0.11f, 0.05f, 0.95f) : new Vector4(0.06f, 0.06f, 0.08f, 0.95f));
            batch.DrawRect(r, Frame);
            Text(batch, labels[i], r.X + 14 * Scale, r.Y + 11 * Scale, 12, new Vector4(0.7f, 0.7f, 0.7f, 1));
            Text(batch, "<", r.X + 220 * Scale, r.Y + 8 * Scale, 16, Gold);
            Text(batch, ">", r.X + r.Width - 26 * Scale, r.Y + 8 * Scale, 16, Gold);
            Text(batch, values[i], r.X + 410 * Scale, r.Y + 10 * Scale, 13, Vector4.One, center: true);
            // The faction's flag.
            if (i == 4 && factionNow != null && ui.Content.Rules.Get("PlayerTemplates", factionNow.Name) is { } node
                && (node.GetString("LobbyFlag") is { Length: > 0 } flag ? flag : node.GetString("SideIconImage")) is { Length: > 0 } icon
                && ui.Images.Find(icon) is { } image)
            {
                float h = 30 * Scale, w = Math.Min(h * 2, image.PixelWidth * h / Math.Max(1, image.PixelHeight));
                ui.Images.Draw(batch, image, new Rect(r.X + 250 * Scale, r.Y + 5 * Scale, w, h));
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
            _ => "Everyone for themselves. Kill to earn credits; buy better units and upgrades.",
        };
        Text(batch, help, width / 2f, (150 + RowCount * 50 + 76) * Scale, 11, new Vector4(0.75f, 0.75f, 0.75f, 1), center: true);
        Text(batch, Category == SsrCategory.Infantry ? "Everyone plays infantry (no aircraft in an infantry match)."
            : "Everyone plays the chosen unit type; aircraft may be taken any time.", width / 2f, (150 + RowCount * 50 + 94) * Scale, 11,
            new Vector4(0.75f, 0.75f, 0.75f, 1), center: true);
        if (status != null)
            Text(batch, status, width / 2f, (150 + RowCount * 50 + 112) * Scale, 12, new Vector4(1, 0.4f, 0.3f, 1), center: true);
        batch.End();
    }

    void DrawPreview(SpriteBatch batch)
    {
        var r = PreviewRect;
        batch.FillRect(r, new Vector4(0, 0, 0, 1));
        if (MapInfo is { } meta)
        {
            var (ul, lr) = MapPreviewDrawer.FindDrawPositions(r, meta);
            var inner = new Rect(ul.X, ul.Y, lr.X - ul.X, lr.Y - ul.Y);
            if (ShellScreen.LoadMapPreview(ui, meta.FileName) is { } picture)
                batch.DrawTexture(picture, inner);
            else
                batch.FillRect(inner, new Vector4(0.2f, 0.2f, 0.2f, 1));
            foreach (var (index, at) in StartSpots())
            {
                bool chosen = index == start, over = Vector2.Distance(at, mouse) < 14 * Scale;
                float size = (chosen ? 22 : 18) * Scale;
                var box = new Rect(at.X - size / 2, at.Y - size / 2, size, size);
                batch.FillRect(box, chosen ? new Vector4(0.85f, 0.6f, 0.1f, 1) : over ? new Vector4(0.35f, 0.3f, 0.2f, 1) : new Vector4(0.1f, 0.1f, 0.12f, 0.9f));
                batch.DrawRect(box, chosen ? Vector4.One : Frame, chosen ? 2 : 1);
                Text(batch, (index + 1).ToString(), at.X, at.Y - 8 * Scale, 11, Vector4.One, center: true);
            }
        }
        batch.DrawRect(r, Frame);
        string where = start >= 0 ? $"YOU START AT {start + 1}" : "YOU START ANYWHERE";
        if ((SsrMode)mode is SsrMode.ZoneDefense or SsrMode.CaptureTheFlag)
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
