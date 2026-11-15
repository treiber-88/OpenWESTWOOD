using System.Numerics;
using OpenWestwood.Client.Platform;
using OpenWestwood.Client.UI;
using OpenWestwood.Core.Installs;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenWestwood.Client.Screens;

/// <summary>
/// Installs an original game's artwork (Zero Hour's, Kane's Wrath's) into OpenWESTWOOD from the player's own copy: it
/// looks for the copy anywhere on the computer (an install, another drive, the disc), or in the folder the player types
/// in, offers to install, copies the files with a progress bar, and restarts the game to use them. Without a game the
/// rest of OpenWESTWOOD still plays; that game's factions, maps and missions stay locked. Drawn with OpenWESTWOOD's own
/// fonts only (it also shows before any of a game's art is there).
/// </summary>
public sealed class InstallScreen : IScreen
{
    enum Stage { Searching, Found, NotFound, Installing, Done, Failed }

    readonly SpriteBatch batch;
    readonly FontLibrary fonts;
    readonly IWindow window;
    readonly List<OriginalGame> games;
    readonly Action? back;
    readonly List<Action> unsubscribe = new();
    readonly IKeyboard? keyboard;
    int current;
    int width, height;
    Vector2 mouse;
    volatile Stage stage = Stage.Searching;
    string details = "", missingFiles = "", folderNote = "";
    long done, total;
    string currentFile = "";
    InstallResult? result;
    readonly List<(string Text, Action Click, Func<Rect> Where)> buttons = new();
    CancellationTokenSource cancel = new();
    // The folder box: what the player typed (the folder set before, to start with) and whether it has the keyboard.
    string folder = "";
    bool typing;
    float caretTime;

    OriginalGame Game => games[current];

    /// <param name="games">The games to install, in turn (the first one not installed is offered first).</param>
    /// <param name="back">Where Back goes (the menus); null: the button closes OpenWESTWOOD.</param>
    public InstallScreen(GL gl, IInputContext input, IWindow window, string root, IReadOnlyList<OriginalGame>? games = null, Action? back = null)
    {
        this.window = window;
        this.back = back;
        this.games = (games is { Count: > 0 } ? games : [OriginalGame.ZeroHour]).ToList();
        batch = new SpriteBatch(gl);
        fonts = new FontLibrary(gl, root);
        foreach (var m in input.Mice)
        {
            Action<IMouse, Vector2> move = (_, p) => mouse = p * GameWindow.MouseScale;
            Action<IMouse, MouseButton> up = (_, b) => { if (b == MouseButton.Left) Click(); };
            m.MouseMove += move;
            m.MouseUp += up;
            unsubscribe.Add(() => { m.MouseMove -= move; m.MouseUp -= up; });
        }
        foreach (var k in input.Keyboards)
        {
            keyboard ??= k;
            Action<IKeyboard, Key, int> key = (kb, kk, _) => OnKey(kb, kk);
            Action<IKeyboard, char> ch = (_, c) => OnChar(c);
            k.KeyDown += key;
            k.KeyChar += ch;
            unsubscribe.Add(() => { k.KeyDown -= key; k.KeyChar -= ch; });
        }
        StartGame(0);
    }

    void StartGame(int index)
    {
        current = Math.Clamp(index, 0, games.Count - 1);
        folder = GameInstalls.FolderSetFor(Game) ?? "";
        folderNote = "";
        Search();
    }

    void Click()
    {
        typing = FolderBox().Contains(mouse);
        foreach (var (_, click, where) in buttons.ToList())
            if (where().Contains(mouse))
            {
                click();
                break;
            }
    }

    void OnKey(IKeyboard kb, Key key)
    {
        if (!typing || stage is Stage.Installing or Stage.Searching)
            return;
        bool control = kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight);
        if (key == Key.Backspace && folder.Length > 0)
            folder = folder[..^1];
        else if (key == Key.V && control)
            folder += (kb.ClipboardText ?? "").Replace("\r", "").Replace("\n", "").Trim('"');
        else if (key is Key.Enter or Key.KeypadEnter)
            UseFolder();
        else if (key == Key.Escape)
            typing = false;
    }

    void OnChar(char c)
    {
        if (typing && !char.IsControl(c) && folder.Length < 400 && stage is not (Stage.Installing or Stage.Searching))
            folder += c;
    }

    /// <summary>The player's folder: searched from now on (kept in installs.ini), then looked through.</summary>
    void UseFolder()
    {
        string path = folder.Trim().Trim('"');
        typing = false;
        if (path.Length == 0)
        {
            // An empty box: back to searching the whole computer.
            GameInstalls.SetFolder(Game, null);
            folderNote = "Searching the whole computer again.";
            Search();
            return;
        }
        if (!Directory.Exists(path))
        {
            folderNote = $"There is no folder \"{path}\".";
            return;
        }
        if (!GameInstalls.FolderHasGame(Game, path))
        {
            folderNote = $"\"{path}\" doesn't have {GameInstalls.DisplayName(Game)}'s files. Type the folder the game is installed in"
                + (Game == OriginalGame.KanesWrath ? " (the one with cnc3ep1.exe and the Core folder)." : " (the one with WindowZH.big and INIZH.big).");
            return;
        }
        GameInstalls.SetFolder(Game, path);
        folderNote = $"Using \"{path}\".";
        Search();
    }

    void Search()
    {
        stage = Stage.Searching;
        var game = Game;
        Task.Run(() =>
        {
            GameInstalls.Rescan();
            var (copy, unavailable) = ContentInstaller.Plan(game);
            total = copy.Sum(c => c.File.Size);
            details = string.Join("\n", ContentInstaller.SourceFolders(game).Take(4));
            missingFiles = string.Join(", ", unavailable.Take(6).Select(f => f.Path)) + (unavailable.Count > 6 ? $" and {unavailable.Count - 6} more" : "");
            stage = ContentInstaller.IsInstalled(game) ? Stage.Done : copy.Count > 0 && unavailable.Count == 0 ? Stage.Found : Stage.NotFound;
            if (stage == Stage.Done)
                result = new InstallResult(true, $"{GameInstalls.DisplayName(game)}'s artwork is already installed.", []);
        });
    }

    void Install()
    {
        stage = Stage.Installing;
        cancel = new CancellationTokenSource();
        var game = Game;
        Task.Run(() =>
        {
            try
            {
                result = ContentInstaller.Install(game, (d, t, f) => { done = d; total = t; currentFile = f; }, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                result = new InstallResult(false, "The install was stopped.", []);
            }
            catch (Exception e)
            {
                result = new InstallResult(false, $"The install failed: {e.Message}", []);
            }
            stage = result.Success ? Stage.Done : Stage.Failed;
        });
    }

    /// <summary>Starts OpenWESTWOOD again (now with the artwork) and closes this one.</summary>
    public static void Restart()
    {
        try
        {
            string exe = Environment.ProcessPath!;
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            // Run through "dotnet x.dll": the first argument is the program itself.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                args.Insert(0, Environment.GetCommandLineArgs()[0]);
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (string a in args)
                start.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not restart: {e.Message}");
        }
        Environment.Exit(0);
    }

    public void Resize(int w, int h)
    {
        width = w;
        height = h;
        fonts.Scale = h / 600f;
    }

    public void Update(float seconds) => caretTime += seconds;

    Rect ButtonRect(int index, int count)
    {
        float w = 180 * height / 720f, h = 44 * height / 720f, gap = 18 * height / 720f;
        float left = (width - (count * w + (count - 1) * gap)) / 2;
        return new Rect(left + index * (w + gap), height * 0.84f, w, h);
    }

    Rect FolderBox() => new(width * 0.12f, height * 0.70f, width * 0.76f, 30 * height / 720f);

    bool ShowsFolder => stage is Stage.Found or Stage.NotFound or Stage.Failed;

    void SetButtons(params (string Text, Action Click)[] list)
    {
        buttons.Clear();
        for (int i = 0; i < list.Length; i++)
        {
            int index = i, count = list.Length;
            buttons.Add((list[i].Text, list[i].Click, () => ButtonRect(index, count)));
        }
    }

    (string, Action) Leave() => back != null ? ("Back", Back) : ("Quit", () => window.Close());

    void Back()
    {
        cancel.Cancel();
        back?.Invoke();
    }

    static string Gigabytes(long bytes) => $"{bytes / 1e9:0.0} GB";

    public void Render(float seconds)
    {
        string name = GameInstalls.DisplayName(Game);
        string title = $"Install {name}";
        string body;
        bool more = current + 1 < games.Count;
        switch (stage)
        {
            case Stage.Searching:
                body = $"Looking for your copy of {name} on this computer: installs, other drives, and the game's disc...";
                SetButtons(Leave());
                break;
            case Stage.Found:
                body = $"Found your copy of {name}:\n{details}\n\nInstall its artwork into OpenWESTWOOD? This copies {Gigabytes(total)} of the game's original files; "
                    + "after that the game runs without the original.";
                SetButtons(("Install", Install), ("Use this folder", UseFolder), Leave());
                break;
            case Stage.NotFound:
                body = details.Length > 0
                    ? $"A copy of {name} was found:\n{details}\n\nBut it doesn't have these original files (they are missing, or modified, for example by a community patch): {missingFiles}.\n\n"
                      + "OpenWESTWOOD only uses the original files. Type the folder of another copy below, insert the game's disc, or plug in a drive with an unmodified copy, then search again."
                    : $"No copy of {name} was found on this computer. If it is installed, type its folder below. "
                      + "Otherwise insert its disc or plug in the drive it is on and search again; the game is in the Command & Conquer Ultimate Collection on Steam and the EA app. "
                      + "You can play everything else without it; only its factions, maps and missions are locked.";
                SetButtons(("Use this folder", UseFolder), ("Search again", Search), ("Steam", () => Shell.ShellScreen.OpenUrl(GameInstalls.SteamUrl)),
                    ("EA app", () => Shell.ShellScreen.OpenUrl(GameInstalls.EaUrl)), Leave());
                break;
            case Stage.Installing:
                body = $"Installing {name}'s artwork... {Gigabytes(done)} of {Gigabytes(total)}\n{currentFile}";
                SetButtons(("Stop", () => cancel.Cancel()));
                break;
            case Stage.Done:
                body = result!.Message + (more ? "" : " Restart OpenWESTWOOD to use it.");
                SetButtons(more ? ("Next game", () => StartGame(current + 1)) : ("Restart", Restart), Leave());
                break;
            default:
                body = result?.Message ?? "The install failed.";
                if (result is { Problems.Count: > 0 })
                    body += "\n\n" + string.Join("\n", result.Problems.Take(5));
                SetButtons(("Use this folder", UseFolder), ("Search again", Search), Leave());
                break;
        }

        batch.Begin(width, height);
        batch.FillRect(new Rect(0, 0, width, height), new Vector4(0.04f, 0.05f, 0.08f, 1));
        var accent = new Vector4(0.95f, 0.75f, 0.2f, 1);
        var titleFont = fonts.Get("Arial", 20, bold: true);
        titleFont.DrawWithShadow(batch, title, new Vector2((width - titleFont.Measure(title)) / 2, height * 0.08f), accent, new Vector4(0, 0, 0, 1));
        var font = fonts.Get("Arial", 11);
        float y = height * 0.2f, margin = width * 0.12f;
        foreach (string paragraph in body.Split('\n'))
            foreach (string line in paragraph.Length == 0 ? [""] : font.WrapLines(paragraph, width - 2 * margin))
            {
                font.Draw(batch, line, new Vector2(margin, y), Vector4.One);
                y += font.LineHeight;
            }
        if (stage == Stage.Installing && total > 0)
        {
            var bar = new Rect(margin, height * 0.66f, width - 2 * margin, 18 * height / 720f);
            batch.FillRect(bar, new Vector4(0.15f, 0.15f, 0.2f, 1));
            batch.FillRect(bar with { Width = bar.Width * Math.Clamp(done / (float)total, 0, 1) }, accent);
            batch.DrawRect(bar, new Vector4(0.6f, 0.6f, 0.7f, 1));
        }
        if (ShowsFolder)
        {
            // The folder box: "the folder your copy is in", typed or pasted (Ctrl+V); Enter or "Use this folder" takes it.
            var box = FolderBox();
            var small = fonts.Get("Arial", 10);
            small.Draw(batch, $"The folder {name} is installed in (click, then type or paste it with Ctrl+V):", new Vector2(box.X, box.Y - small.LineHeight - 4), new Vector4(0.8f, 0.8f, 0.85f, 1));
            batch.FillRect(box, typing ? new Vector4(0.1f, 0.12f, 0.2f, 1) : new Vector4(0.07f, 0.08f, 0.12f, 1));
            batch.DrawRect(box, typing ? accent : new Vector4(0.5f, 0.5f, 0.6f, 1));
            string shown = folder;
            while (shown.Length > 0 && font.Measure(shown) > box.Width - 16)
                shown = shown[1..];
            if (typing && (int)(caretTime * 2) % 2 == 0)
                shown += "|";
            font.Draw(batch, shown, new Vector2(box.X + 6, box.Y + (box.Height - font.LineHeight) / 2), Vector4.One);
            if (folderNote.Length > 0)
                small.Draw(batch, folderNote, new Vector2(box.X, box.Y + box.Height + 4), accent);
        }
        var buttonFont = fonts.Get("Arial", 12, bold: true);
        foreach (var (text, _, where) in buttons)
        {
            var r = where();
            bool hot = r.Contains(mouse);
            batch.FillRect(r, hot ? new Vector4(0.25f, 0.3f, 0.5f, 1) : new Vector4(0.12f, 0.15f, 0.28f, 1));
            batch.DrawRect(r, accent);
            buttonFont.Draw(batch, text, new Vector2(r.X + (r.Width - buttonFont.Measure(text)) / 2, r.Y + (r.Height - buttonFont.LineHeight) / 2), Vector4.One);
        }
        batch.End();
    }

    public void Dispose()
    {
        cancel.Cancel();
        foreach (var action in unsubscribe)
            action();
        batch.Dispose();
    }
}
