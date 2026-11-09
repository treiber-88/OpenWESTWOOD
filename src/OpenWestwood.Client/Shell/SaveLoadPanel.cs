using OpenWestwood.Client.UI.Gui;
using OpenWestwood.Logic.Saving;

namespace OpenWestwood.Client.Shell;

/// <summary>Where saved games go (%APPDATA%\OpenWESTWOOD\Save) and the list of them, newest first.</summary>
public static class SaveFiles
{
    public const string Extension = ".owsave";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenWESTWOOD", "Save");

    public sealed record Entry(string Path, SaveHeader Header);

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        if (!Directory.Exists(Folder))
            return list;
        foreach (string file in Directory.EnumerateFiles(Folder, "*" + Extension))
        {
            try
            {
                list.Add(new Entry(file, SaveGame.ReadHeaderFile(file)));
            }
            catch (Exception e) when (e is SaveGameException or IOException or UnauthorizedAccessException)
            {
                // (an unreadable file is left out of the list)
            }
        }
        return list.OrderByDescending(e => e.Header.SavedAt).ToList();
    }

    /// <summary>A file name no other save has.</summary>
    public static string NewPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(Folder, $"Save{stamp}{Extension}");
        for (int n = 2; File.Exists(path); n++)
            path = System.IO.Path.Combine(Folder, $"Save{stamp}-{n}{Extension}");
        return path;
    }

    /// <summary>The map's name as players know it ("MD_USA01", "Tournament Desert").</summary>
    public static string MapName(string map) =>
        map.Contains(':') ? map[(map.IndexOf(':') + 1)..] : System.IO.Path.GetFileNameWithoutExtension(map.Replace('\\', '/'));

    public static string Line(Entry e)
    {
        var h = e.Header;
        var time = TimeSpan.FromSeconds(h.Frame / (double)Logic.Core.LogicTime.FramesPerSecond);
        return $"{h.Description}   -   {MapName(h.Map)}   -   {time:h\\:mm\\:ss}   -   {h.SavedAt:g}";
    }
}

/// <summary>
/// The save / load list (the original's PopupSaveLoad.wnd in the game and SaveLoad.wnd at the main menu): pick a game to
/// load or delete, or save the game being played as a new one or over an old one (each step confirmed as in the original).
/// </summary>
public sealed class SaveLoadPanel
{
    readonly GuiLayout layout;
    readonly GuiManager gui;
    readonly bool canSave;
    readonly Func<string> defaultDescription;
    readonly Func<string, string, string?> save;
    readonly Action<string> load;
    List<SaveFiles.Entry> entries = new();
    string? overwrite;

    /// <param name="save">Saves the game (description, file) and returns an error message, or null.</param>
    public SaveLoadPanel(GuiLayout layout, GuiManager gui, bool canSave, Func<string> defaultDescription,
        Func<string, string, string?> save, Action<string> load, Action back)
    {
        this.layout = layout;
        this.gui = gui;
        this.canSave = canSave;
        this.defaultDescription = defaultDescription;
        this.save = save;
        this.load = load;
        if (Find("ButtonSave") is { } saveButton)
            saveButton.Hidden = !canSave;
        Click("ButtonBack", back);
        Click("ButtonSave", AskSave);
        Click("ButtonLoad", () => Ask("LoadConfirmParent"));
        Click("ButtonDelete", () => Ask("DeleteConfirmParent"));
        Click("ButtonLoadConfirm", () =>
        {
            Close("LoadConfirmParent");
            if (Selected is { } e)
                load(e.Path);
        });
        Click("ButtonLoadCancel", () => Close("LoadConfirmParent"));
        Click("ButtonDeleteConfirm", () =>
        {
            Close("DeleteConfirmParent");
            if (Selected is { } e)
            {
                try { File.Delete(e.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Refresh();
            }
        });
        Click("ButtonDeleteCancel", () => Close("DeleteConfirmParent"));
        Click("ButtonOverwriteConfirm", () =>
        {
            Close("OverwriteConfirmParent");
            AskDescription(Selected?.Header.Description ?? defaultDescription());
        });
        Click("ButtonOverwriteCancel", () => Close("OverwriteConfirmParent"));
        Click("ButtonSaveDescConfirm", ConfirmSave);
        Click("ButtonSaveDescCancel", () =>
        {
            Close("SaveDescParent");
            gui.Focus(null);
        });
        Refresh();
    }

    GuiWindow? Find(string name) => layout.Find(name);

    void Click(string name, Action action)
    {
        if (layout.Find<GuiPushButton>(name) is { } button)
            button.Clicked += _ => action();
    }

    GuiListBox? List => layout.Find<GuiListBox>("ListboxGames");

    SaveFiles.Entry? Selected => List is { SelectedIndex: >= 0 } list && list.SelectedIndex < entries.Count ? entries[list.SelectedIndex] : null;

    public void Refresh()
    {
        entries = SaveFiles.List();
        if (List is not { } list)
            return;
        list.Items.Clear();
        list.Items.AddRange(entries.Select(SaveFiles.Line));
        list.SelectedIndex = -1;
    }

    void Ask(string confirm)
    {
        if (Selected != null)
            Open(confirm);
    }

    void AskSave()
    {
        if (!canSave)
            return;
        overwrite = Selected?.Path;
        if (overwrite != null)
            Open("OverwriteConfirmParent");
        else
            AskDescription(defaultDescription());
    }

    void AskDescription(string description)
    {
        Open("SaveDescParent");
        if (layout.Find<GuiTextEntry>("EntryDesc") is { } entry)
        {
            entry.Text = description;
            gui.Focus(entry);
        }
    }

    void ConfirmSave()
    {
        Close("SaveDescParent");
        gui.Focus(null);
        string description = layout.Find<GuiTextEntry>("EntryDesc")?.Text?.Trim() is { Length: > 0 } d ? d : defaultDescription();
        string? error = save(description, overwrite ?? SaveFiles.NewPath());
        overwrite = null;
        Refresh();
        if (error != null && List is { } list)
        {
            list.Items.Add(error); // (after the saves, so it never stands for one)
            list.SelectedIndex = -1;
        }
    }

    void Open(string name)
    {
        foreach (string other in new[] { "LoadConfirmParent", "DeleteConfirmParent", "OverwriteConfirmParent", "SaveDescParent" })
            Close(other);
        if (Find(name) is { } w)
            w.Hidden = false;
    }

    void Close(string name)
    {
        if (Find(name) is { } w)
            w.Hidden = true;
    }
}

/// <summary>The main menu's Load Game (the original's SaveLoad.wnd): load or delete a saved game; nothing to save here.</summary>
public sealed class SaveLoadMenu : ShellMenu
{
    readonly SaveLoadPanel panel;

    public SaveLoadMenu(ShellScreen screen, GuiLayout layout) : base(screen, layout)
    {
        panel = new SaveLoadPanel(layout, screen.Gui, canSave: false, () => "", (_, _) => null,
            path =>
            {
                if (screen.LoadSavedGame(path) is { } error)
                    screen.ShowMessageBox("Load Game", error);
            },
            () => Shell.Pop());
    }

    public override void Init()
    {
        base.Init();
        panel.Refresh();
    }
}
