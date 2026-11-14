using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpenWestwood.Client.Network;
using OpenWestwood.Client.UI.Gui;

namespace OpenWestwood.Client.Shell;

/// <summary>The player's network name and the last address joined, kept in Network.ini in the settings folder.</summary>
static class NetworkPreferences
{
    static string File => Path.Combine(SkirmishPreferences.UserDataFolder, "Network.ini");

    static Dictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.File.Exists(File))
                foreach (string line in System.IO.File.ReadAllLines(File))
                    if (line.IndexOf('=') is int eq and > 0)
                        values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        catch (Exception)
        {
        }
        return values;
    }

    static void Save(string key, string value)
    {
        var values = Load();
        values[key] = value;
        try
        {
            Directory.CreateDirectory(SkirmishPreferences.UserDataFolder);
            System.IO.File.WriteAllLines(File, values.Select(kv => $"{kv.Key} = {kv.Value}"));
        }
        catch (Exception)
        {
        }
    }

    public static string Name
    {
        get => Load().GetValueOrDefault("Name") is { Length: > 0 } n ? n : Environment.UserName;
        set => Save("Name", value);
    }

    public static string LastAddress
    {
        get => Load().GetValueOrDefault("LastAddress") ?? "";
        set => Save("LastAddress", value);
    }

    /// <summary>This machine's own addresses on its networks (to tell a friend for direct connect).</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).Distinct().ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Hosts a game here, announced on the LAN. An internet game also asks the router to let internet players in
    /// (see <see cref="RouterPort"/>); a password, if given, is needed to join.
    /// </summary>
    public static NetSession Host(ShellScreen screen, string name, string password = "", bool internet = false)
    {
        var content = screen.Ui.Content;
        var maps = screen.MapCache;
        return NetSession.HostGame($"{name}'s game", name, NetHost.DefaultPort, lan: true, NetHost.Fingerprint(content),
            _ => null, (info, lobby) => NetGame.ResolveRandoms(content, maps, info, lobby), password, openRouter: internet);
    }
}

/// <summary>
/// The LAN lobby (the original's LanLobbyMenu.wnd): the games announced on the local network, your name, and Host,
/// Join (the chosen game) and Direct Connect (a game by address, over the internet).
/// </summary>
public sealed class LanLobbyMenu : ShellMenu
{
    const string L = "LanLobbyMenu.wnd";
    LanBeacon.Listener? listener;
    List<LanBeacon.LanGame> games = new();
    GuiListBox? listGames, listInfo;
    GuiTextEntry? textName;
    GuiWindow? gameInfo;
    float refresh;
    bool busy;

    public LanLobbyMenu(ShellScreen screen, GuiLayout layout) : base(screen, layout)
    {
        OnClick("ButtonBack", () =>
        {
            if (busy)
                return;
            busy = true;
            Shell.Pop();
        });
        OnClick("ButtonHost", () =>
        {
            if (busy)
                return;
            string name = PlayerName();
            try
            {
                Screen.NetSession = NetworkPreferences.Host(Screen, name);
                busy = true;
                Shell.Push("Menus/LanGameOptionsMenu.wnd");
            }
            catch (Exception e)
            {
                Screen.ShowMessageBox("Network game", $"Could not host a game: {e.Message}");
            }
        });
        OnClick("ButtonJoin", () =>
        {
            if (busy || listGames?.SelectedIndex is not int i || i < 0 || i >= games.Count)
                return;
            var game = games[i];
            if (game.Fingerprint != NetHost.Fingerprint(Screen.Ui.Content))
            {
                Screen.ShowMessageBox("Network game", "That game runs a different version of OpenWESTWOOD (or different mods).");
                return;
            }
            if (game.Locked)
            {
                Screen.ShowMessageBox("Network game", $"That game has a password. Use Direct Connect: type the password and the address {game.Address}.");
                return;
            }
            Join(Screen, game.Address, game.Port, PlayerName(), () => busy = true);
        });
        OnClick("ButtonDirectConnect", () =>
        {
            if (busy)
                return;
            NetworkPreferences.Name = PlayerName();
            busy = true;
            Shell.Push("Menus/NetworkDirectConnect.wnd");
        });
        OnClick("ButtonClear", () => { if (textName != null) textName.Text = ""; });
    }

    string PlayerName()
    {
        string name = (textName?.Text ?? "").Trim();
        if (name.Length == 0)
            name = NetworkPreferences.Name;
        NetworkPreferences.Name = name;
        return name;
    }

    /// <summary>Joins a game and goes to its setup (or says why it can't).</summary>
    public static void Join(ShellScreen screen, string address, int port, string name, Action onJoined, string password = "")
    {
        try
        {
            screen.NetSession = NetSession.Join(address, port, name, NetHost.Fingerprint(screen.Ui.Content), password);
            onJoined();
            screen.Shell.Push("Menus/LanGameOptionsMenu.wnd");
        }
        catch (Exception e)
        {
            screen.ShowMessageBox("Network game", $"Could not join the game at {address}:{port}. {e.InnerException?.Message ?? e.Message}");
        }
    }

    public override void Init()
    {
        busy = false;
        Layout.Hidden = false;
        Screen.ShowShellMap(true);
        listGames = Layout.Find<GuiListBox>($"{L}:ListboxGames");
        listInfo = Layout.Find<GuiListBox>($"{L}:ListboxChatWindowLanLobby");
        textName = Layout.Find<GuiTextEntry>($"{L}:TextEntryPlayerName");
        gameInfo = Window($"{L}:StaticTextGameInfo");
        if (textName != null)
            textName.Text = NetworkPreferences.Name;
        // (The LAN lobby's chat and player list need a lobby server, which LAN play does not have.)
        foreach (string unused in new[] { "TextEntryChat", "ButtonEmote", "ListboxPlayers" })
            if (Window($"{L}:{unused}") is { } w)
                w.Hidden = true;
        listInfo?.Items.AddRange([
            "Games on your local network are listed above: choose one and press Join, or Host your own.",
            "To play over the internet, the host presses Host in Direct Connect (with a password if they like) and gives "
                + "the others the internet address it shows; they join in Direct Connect with that address and the password.",
        ]);
        listener ??= new LanBeacon.Listener();
        Transitions.SetGroup("LanLobbyFade");
    }

    public override void Update()
    {
        refresh -= 1f / 30;
        if (refresh > 0 || listener == null || listGames == null)
            return;
        refresh = 0.5f;
        string? selected = listGames.SelectedIndex >= 0 && listGames.SelectedIndex < games.Count ? $"{games[listGames.SelectedIndex].Address}:{games[listGames.SelectedIndex].Port}" : null;
        games = listener.Games.Where(g => !g.Name.StartsWith("[SSR]", StringComparison.Ordinal)).ToList(); // (Sole Survivor Remix games are joined from its own screen.)
        string mine = NetHost.Fingerprint(Screen.Ui.Content);
        listGames.Items.Clear();
        foreach (var g in games)
            listGames.Items.Add($"{g.Name} ({g.Players}/{g.MaxPlayers}){(g.Locked ? " - password" : "")}{(g.Fingerprint == mine ? "" : " - other version")}");
        listGames.SelectedIndex = selected == null ? -1 : games.FindIndex(g => $"{g.Address}:{g.Port}" == selected);
        if (gameInfo != null)
            gameInfo.Text = listGames.SelectedIndex >= 0 ? MapName(games[listGames.SelectedIndex].Map) : "";
    }

    string MapName(string map) =>
        Screen.MapCache.Find(map) is { NameLookupTag.Length: > 0 } meta ? Screen.Ui.Strings.Get(meta.NameLookupTag) : Path.GetFileNameWithoutExtension(map);

    public override void Shutdown(bool immediate)
    {
        listener?.Dispose();
        listener = null;
        GuiComboBox.Close();
        Layout.Hidden = true;
        Shell.ShutdownComplete(this);
    }

    public override bool OnKey(char character)
    {
        if (character == (char)27 && !busy)
        {
            Button($"{L}:ButtonBack")?.SimulateClick();
            return true;
        }
        return false;
    }
}

/// <summary>
/// Direct connect (the original's NetworkDirectConnect.wnd): join a game by its host's address (over the internet,
/// "address" or "address:port"), or host one; shows this machine's own addresses to give to the other players.
/// </summary>
public sealed class NetworkDirectConnectMenu : ShellMenu
{
    const string L = "NetworkDirectConnect.wnd";
    GuiTextEntry? textName, textAddress, textPassword;
    bool busy;

    public NetworkDirectConnectMenu(ShellScreen screen, GuiLayout layout) : base(screen, layout)
    {
        OnClick("ButtonBack", () =>
        {
            if (busy)
                return;
            busy = true;
            Shell.Pop();
        });
        OnClick("ButtonHost", () =>
        {
            if (busy)
                return;
            try
            {
                Screen.NetSession = NetworkPreferences.Host(Screen, Name(), Password(), internet: true);
                busy = true;
                Shell.Push("Menus/LanGameOptionsMenu.wnd");
            }
            catch (Exception e)
            {
                Screen.ShowMessageBox("Network game", $"Could not host a game: {e.Message}");
            }
        });
        OnClick("ButtonJoin", () =>
        {
            if (busy)
                return;
            string text = (textAddress?.Text ?? "").Trim();
            if (text.Length == 0)
                return;
            int port = NetHost.DefaultPort;
            string address = text;
            if (text.LastIndexOf(':') is int colon and > 0 && int.TryParse(text[(colon + 1)..], out int p))
            {
                address = text[..colon];
                port = p;
            }
            NetworkPreferences.LastAddress = text;
            LanLobbyMenu.Join(Screen, address, port, Name(), () => busy = true, Password());
        });
    }

    string Name()
    {
        string name = (textName?.Text ?? "").Trim();
        if (name.Length == 0)
            name = NetworkPreferences.Name;
        NetworkPreferences.Name = name;
        return name;
    }

    string Password() => textPassword?.Text ?? "";

    /// <summary>A text field like the name field, at another place (OpenWESTWOOD's additions to this screen).</summary>
    GuiTextEntry AddEntry(string name, GuiWindow parent, int x, int y, int width)
    {
        var d = textName!.Definition;
        var copy = new Formats.Wnd.WndWindow
        {
            Type = d.Type, Name = $"{L}:{name}", X = x, Y = y, Width = width, Height = d.Height,
            CreationWidth = d.CreationWidth, CreationHeight = d.CreationHeight, FontName = d.FontName, FontSize = d.FontSize, FontBold = d.FontBold,
            TextEnabled = d.TextEnabled, TextEnabledBorder = d.TextEnabledBorder, TextDisabled = d.TextDisabled,
            TextDisabledBorder = d.TextDisabledBorder, TextHilite = d.TextHilite, TextHiliteBorder = d.TextHiliteBorder,
        };
        copy.Status.UnionWith(d.Status);
        copy.Style.UnionWith(d.Style);
        copy.Enabled.AddRange(d.Enabled);
        copy.Disabled.AddRange(d.Disabled);
        copy.Hilite.AddRange(d.Hilite);
        foreach (var (key, values) in d.GadgetData)
            copy.GadgetData[key] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var entry = new GuiTextEntry(copy);
        parent.AddChild(entry);
        return entry;
    }

    /// <summary>A label like the name's label, at another place.</summary>
    void AddLabel(string name, string text, GuiWindow parent, int x, int y)
    {
        if (Window($"{L}:StaticPlayerNameDescription") is not GuiStaticText label)
            return;
        var d = label.Definition;
        var copy = new Formats.Wnd.WndWindow
        {
            Type = d.Type, Name = $"{L}:{name}", X = x, Y = y, Width = d.Width + 100, Height = d.Height,
            CreationWidth = d.CreationWidth, CreationHeight = d.CreationHeight, FontName = d.FontName, FontSize = d.FontSize, FontBold = d.FontBold,
            TextEnabled = d.TextEnabled, TextEnabledBorder = d.TextEnabledBorder, TextDisabled = d.TextDisabled,
            TextDisabledBorder = d.TextDisabledBorder, TextHilite = d.TextHilite, TextHiliteBorder = d.TextHiliteBorder, Text = text,
        };
        copy.Status.UnionWith(d.Status);
        copy.Style.UnionWith(d.Style);
        foreach (var (key, values) in d.GadgetData)
            copy.GadgetData[key] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        copy.Enabled.AddRange(d.Enabled);
        parent.AddChild(new GuiStaticText(copy));
    }

    public override void Init()
    {
        busy = false;
        Layout.Hidden = false;
        Screen.ShowShellMap(true);
        textName = Layout.Find<GuiTextEntry>($"{L}:EditPlayerName");
        if (textName != null)
            textName.Text = NetworkPreferences.Name;
        // The address: a text field where the original has its address history box (drawn like the name field).
        if (textAddress == null && Window($"{L}:ComboboxRemoteIP") is { } combo && textName != null && combo.Parent is { } parent)
        {
            var c = combo.Definition;
            textAddress = AddEntry("TextEntryRemoteIP", parent, c.X, c.Y, c.Width);
            combo.Hidden = true;
            // OpenWESTWOOD: the game's password (for hosting, or to join a game that has one), under the address.
            if (textName.Parent is { } box)
                box.Definition.Height += 56;
            AddLabel("StaticPasswordDescription", "Password (optional)", parent, 162, 353);
            textPassword = AddEntry("TextEntryPassword", parent, c.X, 381, c.Width);
            textPassword.Masked = true;
            Layout.Layout(Screen.Ui.Gui.Scale);
        }
        if (textAddress != null)
            textAddress.Text = NetworkPreferences.LastAddress;
        if (Window($"{L}:StaticLocalIP") is { } local)
            local.Text = string.Join(", ", NetworkPreferences.LocalAddresses()) is { Length: > 0 } a ? a : "-";
        Transitions.SetGroup("NetworkDirectConnectFade");
    }

    public override void Shutdown(bool immediate)
    {
        GuiComboBox.Close();
        Layout.Hidden = true;
        Shell.ShutdownComplete(this);
    }

    public override bool OnKey(char character)
    {
        if (character == (char)27 && !busy)
        {
            Button($"{L}:ButtonBack")?.SimulateClick();
            return true;
        }
        if (character == '\r' && !busy)
        {
            Button($"{L}:ButtonJoin")?.SimulateClick();
            return true;
        }
        return false;
    }
}
