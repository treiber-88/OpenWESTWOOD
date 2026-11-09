using System.Globalization;
using System.Numerics;
using OpenWestwood.Client.Shell;
using OpenWestwood.Client.UI.Gui;
using OpenWestwood.Logic.Saving;
using Silk.NET.Input;

namespace OpenWestwood.Client.InGame;

// The in-game menu (the original's QuitMenu.wnd: Save/Load, Options, Restart, Exit, Return to Game) and saving.
public sealed partial class GameScreen
{
    /// <summary>The map the game was started from ("Maps/MD_USA01/MD_USA01.map", "td:SCM01EA"), kept in saves.</summary>
    public string MapPath { get; set; } = "";

    /// <summary>More for a save to keep (the campaign and mission being played), set by whoever started the game.</summary>
    public Func<IReadOnlyDictionary<string, string>>? SaveExtras { get; set; }

    /// <summary>Kept with a save and handed back when it is loaded (the skirmish's setup, for the score screen).</summary>
    public object? SaveAttachment { get; set; }

    /// <summary>Loads a saved game in this one's place.</summary>
    public Action<string>? LoadRequested { get; set; }

    /// <summary>Starts this game again from the beginning (missions and skirmishes; not network games).</summary>
    public Action? RestartRequested { get; set; }

    GuiLayout? quitMenu, saveLoadMenu, optionsMenu;

    /// <summary>Debug (--save-at): save the game once this many seconds into it.</summary>
    public static float? DebugSaveAt { get; set; }

    void DebugSave()
    {
        if (DebugSaveAt is not { } at || debugClock < at)
            return;
        DebugSaveAt = null;
        string path = SaveFiles.NewPath();
        Logic.Log.Info(SaveTo("Debug save", path) is { } error ? $"--save-at: {error}" : $"Saved: {path}");
    }

    /// <summary>Shows a line of text over the game for a few seconds.</summary>
    public void ShowNotice(string text) => ShowMessage(text);

    bool MenuOpen => quitMenu is { Hidden: false } || saveLoadMenu is { Hidden: false } || optionsMenu is { Hidden: false };

    /// <summary>A game on this machine alone can be saved; a network game can't (the others' state isn't here).</summary>
    public bool CanSave => network == null && !Game.IsGameOver;

    /// <summary>Keys while a menu is open: Escape backs out, Backspace edits the save's name; the game gets none.</summary>
    bool MenuKey(Key key)
    {
        if (!MenuOpen)
            return false;
        if (key == Key.Backspace)
            uiManager.KeyChar('\b');
        else if (key == Key.Escape)
        {
            if (optionsMenu is { Hidden: false })
                Close(optionsMenu);
            else if (saveLoadMenu is { Hidden: false })
                Close(saveLoadMenu);
            else
                CloseQuitMenu();
        }
        return true;
    }

    void OpenQuitMenu()
    {
        EnsureUi();
        if (quitMenu == null)
        {
            quitMenu = GuiLayout.Load(ui.Content.FileSystem, "Window/Menus/QuitMenu.wnd");
            Wire(quitMenu, "ButtonReturn", CloseQuitMenu);
            Wire(quitMenu, "ButtonSaveLoad", OpenSaveLoad);
            Wire(quitMenu, "ButtonOptions", OpenOptions);
            Wire(quitMenu, "ButtonRestart", () =>
            {
                if (RestartRequested is { } restart)
                {
                    CloseQuitMenu();
                    restart();
                }
            });
            Wire(quitMenu, "ButtonExit", () =>
            {
                CloseQuitMenu();
                Game.IsGameOver = true; // (the score screen follows, as after any game)
            });
        }
        if (quitMenu.Find("ButtonRestart") is { } restartButton)
            restartButton.Hidden = RestartRequested == null || network != null;
        Show(quitMenu);
        // A game on this machine alone waits while the menu is up.
        if (network == null)
            world.RunLogic = false;
    }

    void CloseQuitMenu()
    {
        foreach (var layout in new[] { optionsMenu, saveLoadMenu, quitMenu })
            if (layout != null)
                Close(layout);
        uiManager.Focus(null);
        world.RunLogic = true;
    }

    void OpenSaveLoad()
    {
        if (saveLoadMenu == null)
        {
            saveLoadMenu = GuiLayout.Load(ui.Content.FileSystem, "Window/Menus/PopupSaveLoad.wnd");
            var panel = new SaveLoadPanel(saveLoadMenu, uiManager, CanSave, () => SaveFiles.MapName(MapPath), SaveTo,
                path =>
                {
                    if (LoadRequested is { } loadGame)
                    {
                        CloseQuitMenu();
                        loadGame(path);
                    }
                },
                () => Close(saveLoadMenu!));
            saveLoadPanel = panel;
        }
        saveLoadPanel?.Refresh();
        Show(saveLoadMenu);
    }

    SaveLoadPanel? saveLoadPanel;

    void OpenOptions()
    {
        if (optionsMenu == null)
        {
            optionsMenu = GuiLayout.Load(ui.Content.FileSystem, "Window/Menus/OptionsMenu.wnd");
            Wire(optionsMenu, "ButtonBack", () => Close(optionsMenu!));
            Wire(optionsMenu, "ButtonAccept", () => Close(optionsMenu!));
        }
        Show(optionsMenu);
    }

    void Show(GuiLayout layout)
    {
        uiManager.Remove(layout);
        uiManager.Push(layout);
        layout.Layout(ui.Gui.Scale);
        layout.Hidden = false;
    }

    void Close(GuiLayout layout)
    {
        layout.Hidden = true;
        uiManager.Remove(layout);
    }

    static void Wire(GuiLayout layout, string button, Action action)
    {
        if (layout.Find<GuiPushButton>(button) is { } b)
            b.Clicked += _ => action();
    }

    /// <summary>Saves the game to a file; returns why it couldn't, or null.</summary>
    string? SaveTo(string description, string path)
    {
        if (!CanSave)
            return "This game can't be saved.";
        var extras = new Dictionary<string, string>(SaveExtras?.Invoke() ?? new Dictionary<string, string>());
        var camera = world.Camera;
        extras["Camera"] = string.Join(",", new[] { camera.Target.X, camera.Target.Y, camera.Yaw, camera.Pitch, camera.Distance }
            .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        extras["Player"] = player.Index.ToString(CultureInfo.InvariantCulture);
        try
        {
            SaveGame.WriteFile(Game, map, new SaveHeader(MapPath, description, DateTime.Now, Game.Frame, extras), path, SaveAttachment);
            ShowMessage(ui.Strings.Get("GUI:GameSaveComplete") is { Length: > 0 } done && !done.StartsWith("GUI:") ? done : "Game saved.");
            return null;
        }
        catch (Exception e) when (e is SaveGameException or IOException or UnauthorizedAccessException)
        {
            return "Could not save: " + e.Message;
        }
    }

    /// <summary>Puts the camera where it was when the game was saved.</summary>
    public void RestoreView(SaveHeader header)
    {
        var parts = header.Extra("Camera").Split(',');
        if (parts.Length != 5 || !parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return;
        var v = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        world.LookAt(new Vector3(v[0], v[1], 0));
        world.Camera.Target = new Vector3(v[0], v[1], world.Camera.Target.Z);
        world.Camera.Yaw = v[2];
        world.Camera.Pitch = v[3];
        world.Camera.Distance = v[4];
    }
}
