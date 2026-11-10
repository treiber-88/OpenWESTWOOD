using System.Numerics;
using OpenWestwood.Client.Platform;
using OpenWestwood.Client.UI;
using OpenWestwood.Core.Installs;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenWestwood.Client.Screens;

/// <summary>
/// The first thing a new player sees when OpenWESTWOOD doesn't have Zero Hour's artwork yet (its menus are made of it, so
/// nothing else can show): it looks for the player's copy of Command &amp; Conquer Generals Zero Hour anywhere on the
/// computer (an install, another drive, the disc), offers to install its artwork into OpenWESTWOOD, copies it with a
/// progress bar, and then starts the game. Drawn with OpenWESTWOOD's own fonts only.
/// </summary>
public sealed class InstallScreen : IScreen
{
    enum Stage { Searching, Found, NotFound, Installing, Done, Failed }

    readonly SpriteBatch batch;
    readonly FontLibrary fonts;
    readonly IWindow window;
    readonly OriginalGame game;
    int width, height;
    Vector2 mouse;
    volatile Stage stage = Stage.Searching;
    string details = "", missingFiles = "";
    long done, total;
    string currentFile = "";
    InstallResult? result;
    readonly List<(string Text, Action Click, Func<Rect> Where)> buttons = new();
    readonly CancellationTokenSource cancel = new();

    public InstallScreen(GL gl, IInputContext input, IWindow window, string root, OriginalGame game = OriginalGame.ZeroHour)
    {
        this.window = window;
        this.game = game;
        batch = new SpriteBatch(gl);
        fonts = new FontLibrary(gl, root);
        foreach (var m in input.Mice)
        {
            m.MouseMove += (_, p) => mouse = p;
            m.MouseUp += (_, b) =>
            {
                if (b == MouseButton.Left)
                    foreach (var (_, click, where) in buttons.ToList())
                        if (where().Contains(mouse))
                        {
                            click();
                            break;
                        }
            };
        }
        Search();
    }

    void Search()
    {
        stage = Stage.Searching;
        Task.Run(() =>
        {
            GameInstalls.Rescan();
            var (copy, unavailable) = ContentInstaller.Plan(game);
            total = copy.Sum(c => c.File.Size);
            details = string.Join("\n", ContentInstaller.SourceFolders(game).Take(4));
            missingFiles = string.Join(", ", unavailable.Take(6).Select(f => f.Path)) + (unavailable.Count > 6 ? $" and {unavailable.Count - 6} more" : "");
            stage = copy.Count > 0 && unavailable.Count == 0 ? Stage.Found : Stage.NotFound;
        });
    }

    void Install()
    {
        stage = Stage.Installing;
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

    public void Update(float seconds) { }

    Rect ButtonRect(int index, int count)
    {
        float w = 200 * height / 720f, h = 44 * height / 720f, gap = 24 * height / 720f;
        float left = (width - (count * w + (count - 1) * gap)) / 2;
        return new Rect(left + index * (w + gap), height * 0.78f, w, h);
    }

    void SetButtons(params (string Text, Action Click)[] list)
    {
        buttons.Clear();
        for (int i = 0; i < list.Length; i++)
        {
            int index = i, count = list.Length;
            buttons.Add((list[i].Text, list[i].Click, () => ButtonRect(index, count)));
        }
    }

    static string Gigabytes(long bytes) => $"{bytes / 1e9:0.0} GB";

    public void Render(float seconds)
    {
        string name = GameInstalls.DisplayName(game);
        string title = $"OpenWESTWOOD needs {name}";
        string body;
        switch (stage)
        {
            case Stage.Searching:
                body = $"Looking for your copy of {name} on this computer: installs, other drives, and the game's disc...";
                SetButtons(("Quit", () => window.Close()));
                break;
            case Stage.Found:
                body = $"Found your copy of {name}:\n{details}\n\nInstall its artwork into OpenWESTWOOD? This copies {Gigabytes(total)} of the game's original files; "
                    + "after that the game runs without the original.";
                SetButtons(("Install", Install), ("Quit", () => window.Close()));
                break;
            case Stage.NotFound:
                body = details.Length > 0
                    ? $"A copy of {name} was found:\n{details}\n\nBut it doesn't have these original files (they are missing, or modified, for example by a community patch): {missingFiles}.\n\n"
                      + "OpenWESTWOOD only uses the original files. Insert the game's disc, or plug in a drive with an unmodified copy, then search again."
                    : $"No copy of {name} was found on this computer. OpenWESTWOOD uses the original game's artwork, so you need the game: "
                      + "install it (it is in the Command & Conquer Ultimate Collection on Steam and the EA app), insert its disc, or plug in the drive it is on, then search again.";
                SetButtons(("Search again", Search), ("Steam", () => Shell.ShellScreen.OpenUrl(GameInstalls.SteamUrl)),
                    ("EA app", () => Shell.ShellScreen.OpenUrl(GameInstalls.EaUrl)), ("Quit", () => window.Close()));
                break;
            case Stage.Installing:
                body = $"Installing {name}'s artwork... {Gigabytes(done)} of {Gigabytes(total)}\n{currentFile}";
                SetButtons(("Stop", () => cancel.Cancel()));
                break;
            case Stage.Done:
                body = result!.Message + " OpenWESTWOOD is ready.";
                SetButtons(("Start", Restart));
                break;
            default:
                body = result?.Message ?? "The install failed.";
                if (result is { Problems.Count: > 0 })
                    body += "\n\n" + string.Join("\n", result.Problems.Take(5));
                SetButtons(("Search again", Search), ("Quit", () => window.Close()));
                break;
        }

        batch.Begin(width, height);
        batch.FillRect(new Rect(0, 0, width, height), new Vector4(0.04f, 0.05f, 0.08f, 1));
        var accent = new Vector4(0.95f, 0.75f, 0.2f, 1);
        var titleFont = fonts.Get("Arial", 20, bold: true);
        titleFont.DrawWithShadow(batch, title, new Vector2((width - titleFont.Measure(title)) / 2, height * 0.12f), accent, new Vector4(0, 0, 0, 1));
        var font = fonts.Get("Arial", 11);
        float y = height * 0.25f, margin = width * 0.12f;
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
        batch.Dispose();
    }
}
