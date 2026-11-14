using System.Numerics;
using OpenWestwood.Client.Network;
using OpenWestwood.Client.Platform;
using OpenWestwood.Client.Shell;
using OpenWestwood.Client.UI;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Logic.SoleSurvivor;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenWestwood.Client.SoleSurvivor;

/// <summary>
/// OpenWESTWOOD: Sole Survivor Remix over the network (LAN, or the internet by address), on the same network code as the
/// skirmish lobby (NetSession / NetHost / Lockstep). The host's setup (mode, map, unit type, limits, computers) is the
/// game's; it travels in the lobby's GameInfo.Extra. Each person picks their own game and faction (and team, in the team
/// modes) and says they're ready; the host starts, and every machine builds the same match (SsrMatchSetup from the host's
/// seed). Each person's credits and upgrades are their own profile's.
/// </summary>
public sealed class SsrOnlineScreen : IScreen
{
    readonly GL gl;
    readonly IInputContext input;
    readonly IWindow window;
    readonly UiResources ui;
    readonly MapCache mapCache;
    readonly List<Action> unsubscribe = new();
    readonly IReadOnlyList<SsrFaction> factions;
    readonly List<(int Index, string Name, Core.Rules.RuleNode Rules)> templates;
    readonly SsrMatchSetup? hostSetup;
    LanBeacon.Listener? listener;
    NetSession? session;
    int width = 1280, height = 720;
    Vector2 mouse;

    string address = NetworkPreferences.LastAddress;
    string name = NetworkPreferences.Name;
    string chatLine = "";
    enum Field { None, Name, Address, Chat }
    Field focus = Field.None;
    string? status;

    int game, faction, team;
    bool ready, hostOptionsSent, slotSent;
    bool started;

    /// <summary>Hosting: <paramref name="setup"/> is the game's (from the setup screen). Joining: null.</summary>
    public SsrOnlineScreen(GL gl, IInputContext input, IWindow window, UiResources ui, MapCache mapCache, SsrMatchSetup? setup)
    {
        this.gl = gl;
        this.input = input;
        this.window = window;
        this.ui = ui;
        this.mapCache = mapCache;
        hostSetup = setup;
        templates = GameInfo.PlayerTemplates(ui.Content.Rules);
        factions = SsrCatalog.Factions(ui.Content.Rules, new Logic.Templates.TemplateStore(ui.Content.Rules), GameInfo.IsInstalledFaction);
        if (setup != null)
        {
            // My faction: the one chosen in the setup.
            var mine = factions.FirstOrDefault(f => f.Name == setup.Faction);
            if (mine != null)
            {
                game = Math.Max(0, Games.IndexOf(mine.Game));
                faction = Math.Max(0, GameFactions.FindIndex(f => f.Name == mine.Name));
            }
            Host();
        }
        else
            listener = new LanBeacon.Listener();
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
            Action<IKeyboard, Key, int> key = (_, kk, _) => OnKey(kk);
            Action<IKeyboard, char> ch = (_, c) => OnChar(c);
            k.KeyDown += key;
            k.KeyChar += ch;
            unsubscribe.Add(() => { k.KeyDown -= key; k.KeyChar -= ch; });
        }
    }

    // ---- The settings this game is played with ----

    SsrMatchSetup Setup => hostSetup ?? (session?.Lobby is { } lobby && SsrMatchSetup.IsSsr(lobby.Info.Extra) ? SsrMatchSetup.Decode(lobby.Info.Extra) : new SsrMatchSetup());

    bool Teams => Setup.Mode is SsrMode.ZoneDefense or SsrMode.CaptureTheFlag or SsrMode.TeamBattle;

    /// <summary>The factions I may play: with a unit of the game's type, from a game everyone has.</summary>
    List<SsrFaction> Playable
    {
        get
        {
            var setup = Setup;
            var settings = new SsrSettings { UnitType = setup.UnitType };
            return factions.Where(f => f.Units.Any(u => settings.Allowed(u) && u.Category != SsrCategory.Aircraft)
                && (session?.Lobby is not { } lobby || ui.Content.Rules.Get("PlayerTemplates", f.Name) is not { } node || lobby.IsFactionAllowed(node))).ToList();
        }
    }

    List<string> Games => SsrCatalog.Games.Where(g => Playable.Any(f => f.Game == g)).ToList();

    List<SsrFaction> GameFactions
    {
        get
        {
            var games = Games;
            return games.Count == 0 ? [] : Playable.Where(f => f.Game == games[Math.Clamp(game, 0, games.Count - 1)]).ToList();
        }
    }

    SsrFaction? MyFaction => GameFactions is { Count: > 0 } list ? list[Math.Clamp(faction, 0, list.Count - 1)] : null;

    int TemplateIndex(string factionName) => templates.FindIndex(t => t.Name == factionName);

    // ---- Hosting and joining ----

    void Host()
    {
        try
        {
            session = NetSession.HostGame($"[SSR] {name}'s game", name, NetHost.DefaultPort, lan: true, NetHost.Fingerprint(ui.Content),
                lobby => lobby.Info.Slots.Count(s => s.IsHuman) < 1 ? "Nobody is in the game." : null, (_, _) => { }, openRouter: true);
            Hook();
        }
        catch (Exception e)
        {
            status = $"Could not host: {e.Message}";
            session = null;
        }
    }

    void Join(string to, int port)
    {
        try
        {
            NetworkPreferences.Name = name;
            NetworkPreferences.LastAddress = to;
            session = NetSession.Join(to, port, name, NetHost.Fingerprint(ui.Content));
            Hook();
            listener?.Dispose();
            listener = null;
            status = null;
        }
        catch (Exception e)
        {
            status = $"Could not join {to}: {e.Message}";
            session = null;
        }
    }

    void Hook()
    {
        if (session == null)
            return;
        session.Notice += text => status = text;
        session.Started += Start;
    }

    /// <summary>The address typed: "host" or "host:port".</summary>
    void JoinTyped()
    {
        string text = address.Trim();
        if (text.Length == 0)
            return;
        int port = NetHost.DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int p))
        {
            port = p;
            text = text[..colon];
        }
        Join(text, port);
    }

    /// <summary>Sends my faction, team and readiness (the host is always ready).</summary>
    void SendSlot()
    {
        if (session is not { Lobby: not null } s || MyFaction is not { } f)
            return;
        s.RequestSlot(-1, TemplateIndex(f.Name), Teams ? team : -1, -1, s.IsHost || ready);
        slotSent = true;
    }

    public void Update(float seconds)
    {
        if (session == null)
            return;
        session.Poll();
        if (started || session.Lobby is not { } lobby)
            return;
        if (session.Error != null && !session.IsHost)
            status = session.Error;
        // The host puts the game's settings in the lobby once it is there.
        if (session.IsHost && !hostOptionsSent && hostSetup != null)
        {
            var info = lobby.Clone().Info;
            info.Map = hostSetup.Map;
            info.Extra = hostSetup.Encode();
            for (int i = 1; i < GameInfo.MaxSlots; i++)
                if (!info.Slots[i].IsHuman)
                    info.Slots[i].SetState(SlotState.Open);
            session.SendHostOptions(info);
            hostOptionsSent = true;
        }
        // Joined: my slot gets my faction (a team: the smaller one).
        if (!slotSent && session.ClientId >= 0 && lobby.SlotOf(session.ClientId) >= 0 && SsrMatchSetup.IsSsr(lobby.Info.Extra))
        {
            if (Teams && !session.IsHost)
                team = lobby.Info.Slots.Count(s => s.IsHuman && s.TeamNumber == 0) <= lobby.Info.Slots.Count(s => s.IsHuman && s.TeamNumber == 1) ? 0 : 1;
            SendSlot();
        }
        // (The host changed something: ready again needed.)
        int mySlot = lobby.SlotOf(session.ClientId);
        if (!session.IsHost && mySlot >= 0 && ready && !lobby.Accepted[mySlot])
            ready = false;
    }

    /// <summary>The host started: every machine builds the same match and plays it.</summary>
    void Start(GameInfo info)
    {
        if (started || session?.Lobby is not { } lobby)
            return;
        started = true;
        var setup = FromLobby(info, ui.Content.Rules, mapCache);
        int slot = lobby.SlotOf(session.ClientId);
        string local = slot >= 0 ? info.Slots[slot].Name : name;
        var played = session;
        session = null; // (the game owns the link now)
        GameWindow.Current?.Switch(SsrSetupScreen.Play(gl, input, window, ui, setup, played, local));
    }

    /// <summary>The match the host started, the same on every machine: the host's settings, map and seed, and each person's
    /// name, faction and team from their lobby slot (host first).</summary>
    public static SsrMatchSetup FromLobby(GameInfo info, Core.Rules.RuleSet rules, MapCache maps)
    {
        var setup = SsrMatchSetup.Decode(info.Extra);
        setup.Map = info.Map;
        setup.Seed = info.Seed;
        setup.MapInfo = maps.Find(info.Map);
        if (setup.MapInfo is { NumPlayers: > 0 } meta)
            setup.MapPlayers = meta.NumPlayers;
        var templates = GameInfo.PlayerTemplates(rules);
        string fallback = SsrCatalog.Factions(rules, new Logic.Templates.TemplateStore(rules)).FirstOrDefault()?.Name ?? "FactionAmerica";
        setup.Humans = info.Slots.Where(s => s.IsHuman).Select(s => new SsrMatchSetup.Human(s.Name,
            s.PlayerTemplate >= 0 && s.PlayerTemplate < templates.Count ? templates[s.PlayerTemplate].Name : fallback, Math.Max(0, s.TeamNumber))).ToList();
        return setup;
    }

    // ---- Input ----

    void OnKey(Key key)
    {
        if (key == Key.Escape)
        {
            if (focus != Field.None)
                focus = Field.None;
            else
                Back();
            return;
        }
        if (key == Key.Backspace)
        {
            switch (focus)
            {
                case Field.Name when name.Length > 0: name = name[..^1]; break;
                case Field.Address when address.Length > 0: address = address[..^1]; break;
                case Field.Chat when chatLine.Length > 0: chatLine = chatLine[..^1]; break;
            }
            return;
        }
        if (key == Key.Enter)
        {
            if (focus == Field.Address)
                JoinTyped();
            else if (focus == Field.Chat && chatLine.Length > 0 && session != null)
            {
                session.SendChat(chatLine);
                chatLine = "";
            }
            else if (focus == Field.Name)
                NetworkPreferences.Name = name;
        }
    }

    void OnChar(char c)
    {
        if (c < ' ' || c == 127)
            return;
        switch (focus)
        {
            case Field.Name when name.Length < 20: name += c; break;
            case Field.Address when address.Length < 60: address += c; break;
            case Field.Chat when chatLine.Length < 150: chatLine += c; break;
        }
    }

    float Scale => height / 720f;
    Rect R(float x, float y, float w, float h) => new(width / 2f + x * Scale, y * Scale, w * Scale, h * Scale);
    Rect BackButton => R(-600, 640, 280, 44);
    Rect MainButton => R(320, 640, 280, 44);
    Rect NameBox => R(-600, 130, 560, 36);
    Rect AddressBox => R(-600, 210, 400, 36);
    Rect JoinButton => R(-190, 210, 150, 36);
    Rect GameRow => R(-600, 470, 560, 36);
    Rect FactionRow => R(-600, 512, 560, 36);
    Rect TeamRow => R(-600, 554, 560, 36);
    Rect ChatBox => R(40, 590, 560, 34);
    Rect LanRow(int i) => R(-600, 290 + i * 40, 560, 36);

    void Click(Vector2 p)
    {
        focus = Field.None;
        if (BackButton.Contains(p))
        {
            Back();
            return;
        }
        if (session == null)
        {
            // Joining: the name, the address, or a game found on the LAN.
            if (NameBox.Contains(p)) focus = Field.Name;
            else if (AddressBox.Contains(p)) focus = Field.Address;
            else if (JoinButton.Contains(p)) JoinTyped();
            else if (listener != null)
            {
                var found = LanGames;
                for (int i = 0; i < found.Count && i < 7; i++)
                    if (LanRow(i).Contains(p))
                        Join(found[i].Address, found[i].Port);
            }
            return;
        }
        if (ChatBox.Contains(p))
        {
            focus = Field.Chat;
            return;
        }
        int step(Rect r) => p.X < r.X + r.Width / 2 ? -1 : 1;
        int Wrap(int v, int count, int by) => count == 0 ? 0 : ((v + by) % count + count) % count;
        if (GameRow.Contains(p))
        {
            game = Wrap(game, Games.Count, step(GameRow));
            faction = 0;
            SendSlot();
        }
        else if (FactionRow.Contains(p))
        {
            faction = Wrap(faction, GameFactions.Count, step(FactionRow));
            SendSlot();
        }
        else if (Teams && TeamRow.Contains(p))
        {
            team = 1 - team;
            SendSlot();
        }
        else if (MainButton.Contains(p))
        {
            if (session.IsHost)
                session.RequestStart();
            else
            {
                ready = !ready;
                SendSlot();
            }
        }
    }

    List<LanBeacon.LanGame> LanGames => listener?.Games.Where(g => g.Name.StartsWith("[SSR]", StringComparison.Ordinal)).ToList() ?? [];

    void Back()
    {
        session?.Dispose();
        session = null;
        GameWindow.Current?.Switch(new SsrSetupScreen(gl, input, window, ui, mapCache));
    }

    public void Resize(int w, int h)
    {
        width = Math.Max(1, w);
        height = Math.Max(1, h);
        ui.Resize(width, height);
    }

    // ---- Drawing ----

    static readonly Vector4 Gold = new(1f, 0.82f, 0.35f, 1), Shadow = new(0, 0, 0, 1), Frame = new(0.85f, 0.65f, 0.25f, 1),
        Dim = new(0.7f, 0.7f, 0.7f, 1), Panel = new(0.06f, 0.06f, 0.08f, 0.95f), Hover = new(0.14f, 0.11f, 0.05f, 0.95f);

    void Text(SpriteBatch batch, string text, float x, float y, float size, Vector4 color, bool center = false)
    {
        var font = ui.Fonts.Get("Arial", Math.Max(7, size * Scale), bold: true);
        float w = center ? font.Measure(text) : 0;
        font.DrawWithShadow(batch, text, new Vector2(MathF.Round(x - w / 2), MathF.Round(y)), color, Shadow);
    }

    void Box(SpriteBatch batch, Rect r, string label, string value, bool focused = false, bool arrows = false)
    {
        batch.FillRect(r, r.Contains(mouse) || focused ? Hover : Panel);
        batch.DrawRect(r, focused ? Gold : Frame);
        Text(batch, label, r.X + 12 * Scale, r.Y + 10 * Scale, 11, Dim);
        if (arrows)
        {
            Text(batch, "<", r.X + 170 * Scale, r.Y + 6 * Scale, 15, Gold);
            Text(batch, ">", r.X + r.Width - 24 * Scale, r.Y + 6 * Scale, 15, Gold);
            Text(batch, value, r.X + 360 * Scale, r.Y + 9 * Scale, 12, Vector4.One, center: true);
        }
        else
            Text(batch, value + (focused && DateTime.Now.Millisecond < 500 ? "|" : ""), r.X + 150 * Scale, r.Y + 9 * Scale, 12, Vector4.One);
    }

    void Button(SpriteBatch batch, Rect r, string label, bool enabled = true)
    {
        batch.FillRect(r, !enabled ? new Vector4(0.1f, 0.1f, 0.1f, 1) : r.Contains(mouse) ? new Vector4(0.45f, 0.32f, 0.08f, 1) : new Vector4(0.2f, 0.15f, 0.05f, 1));
        batch.DrawRect(r, Frame, 2);
        Text(batch, label, r.X + r.Width / 2, r.Y + r.Height / 2 - 9 * Scale, 15, enabled ? Vector4.One : Dim, center: true);
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0.02f, 0.02f, 0.03f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var batch = ui.Batch;
        batch.Begin(width, height);
        Text(batch, "SOLE SURVIVOR REMIX", width / 2f, 30 * Scale, 30, Gold, center: true);
        Text(batch, session == null ? "JOIN A NETWORK GAME" : session.IsHost ? "HOSTING A NETWORK GAME" : "NETWORK GAME", width / 2f, 72 * Scale, 14, Dim, center: true);
        if (session == null)
            DrawJoin(batch);
        else
            DrawLobby(batch);
        Button(batch, BackButton, "BACK");
        if (status != null)
            Text(batch, status, width / 2f, 692 * Scale, 11, new Vector4(1, 0.45f, 0.35f, 1), center: true);
        batch.End();
    }

    void DrawJoin(SpriteBatch batch)
    {
        Box(batch, NameBox, "YOUR NAME", name, focus == Field.Name);
        Text(batch, "ADDRESS (an internet game's host gives it, as 1.2.3.4 or 1.2.3.4:port)", NameBox.X, 190 * Scale, 10, Dim);
        Box(batch, AddressBox, "ADDRESS", address, focus == Field.Address);
        Button(batch, JoinButton, "JOIN");
        Text(batch, "GAMES ON YOUR NETWORK", NameBox.X, 266 * Scale, 11, Gold);
        var found = LanGames;
        if (found.Count == 0)
            Text(batch, "Looking for games...", NameBox.X + 12 * Scale, 300 * Scale, 11, Dim);
        for (int i = 0; i < found.Count && i < 7; i++)
        {
            var g = found[i];
            var r = LanRow(i);
            batch.FillRect(r, r.Contains(mouse) ? Hover : Panel);
            batch.DrawRect(r, Frame);
            string map = mapCache.Find(g.Map) is { } meta ? ui.Strings.Get(meta.NameLookupTag) : g.Map;
            Text(batch, $"{g.Name[5..].Trim()}   {map}   {g.Players}/{g.MaxPlayers}{(g.Locked ? "   (password)" : "")}", r.X + 12 * Scale, r.Y + 10 * Scale, 11, Vector4.One);
        }
        Text(batch, "Click a game to join it. The host's machine shows their internet address once the game is up.", width / 2f, 610 * Scale, 10, Dim, center: true);
    }

    void DrawLobby(SpriteBatch batch)
    {
        if (session!.Lobby is not { } lobby || !SsrMatchSetup.IsSsr(lobby.Info.Extra) && !session.IsHost)
        {
            Text(batch, session.IsConnected ? "Connecting..." : $"Not connected: {session.CloseReason}", width / 2f, 300 * Scale, 14, Dim, center: true);
            return;
        }
        var setup = Setup;
        string[] modes = ["FREEPLAY", "ZONE DEFENSE", "CAPTURE THE FLAG", "CLASSIC", "TEAM BATTLE"];
        string map = mapCache.Find(lobby.Info.Map) is { } meta ? ui.Strings.Get(meta.NameLookupTag) : lobby.Info.Map;
        string type = setup.UnitType switch { SsrCategory.Infantry => "INFANTRY", SsrCategory.Naval => "NAVAL (+ AIRCRAFT)", null => "ANY", _ => "VEHICLES (+ AIRCRAFT)" };
        string computers = setup.Mode == SsrMode.TeamBattle ? $"{setup.AlliedComputers} WITH THE HOST, {setup.EnemyComputers} AGAINST" : setup.Computers.ToString();
        var left = R(-600, 100, 560, 0);
        Text(batch, lobby.GameName, left.X, left.Y, 16, Gold);
        string[] lines =
        [
            $"MODE  {modes[Math.Clamp((int)setup.Mode, 0, modes.Length - 1)]}",
            $"MAP  {map}",
            $"UNITS  {type}",
            $"COMPUTER PLAYERS  {computers}",
            $"LIVES  {(setup.Lives == 0 ? "UNLIMITED" : setup.Lives)}   TIME LIMIT  {(setup.TimeLimitMinutes == 0 ? "NONE" : setup.TimeLimitMinutes + " MIN")}   KILL LIMIT  {(setup.KillLimit == 0 ? "NONE" : setup.KillLimit)}",
        ];
        for (int i = 0; i < lines.Length; i++)
            Text(batch, lines[i], left.X, left.Y + (30 + i * 20) * Scale, 11, Vector4.One);
        // The people.
        Text(batch, "PLAYERS", left.X, 250 * Scale, 12, Gold);
        int row = 0;
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            var s = lobby.Info.Slots[i];
            if (!s.IsHuman)
                continue;
            string fac = s.PlayerTemplate >= 0 && s.PlayerTemplate < templates.Count && factions.FirstOrDefault(f => f.Name == templates[s.PlayerTemplate].Name) is { } f
                ? ui.Strings.Get(f.DisplayName) : "choosing...";
            string teamText = Teams && s.TeamNumber >= 0 ? $"   TEAM {s.TeamNumber + 1}" : "";
            string readyText = lobby.SlotClient[i] == 0 ? "HOST" : lobby.Accepted[i] ? "READY" : "not ready";
            var color = lobby.SlotClient[i] == session.ClientId ? Gold : Vector4.One;
            float y = (276 + row++ * 22) * Scale;
            Text(batch, s.Name, left.X + 12 * Scale, y, 12, color);
            Text(batch, fac + teamText, left.X + 200 * Scale, y, 11, Dim);
            Text(batch, readyText, left.X + 470 * Scale, y, 11, lobby.Accepted[i] ? new Vector4(0.4f, 0.9f, 0.4f, 1) : Dim);
        }
        // My choices.
        var games = Games;
        Box(batch, GameRow, "GAME", games.Count > 0 ? games[Math.Clamp(game, 0, games.Count - 1)] : "-", arrows: true);
        Box(batch, FactionRow, "FACTION", MyFaction is { } mine ? ui.Strings.Get(mine.DisplayName) : "-", arrows: true);
        if (Teams)
            Box(batch, TeamRow, "TEAM", $"TEAM {team + 1}", arrows: true);
        // Chat (and the host's notes: the address to give internet players).
        var chat = R(40, 100, 560, 480);
        batch.FillRect(chat, Panel);
        batch.DrawRect(chat, Frame);
        var shown = session.Chat.TakeLast(20).ToList();
        for (int i = 0; i < shown.Count; i++)
            Text(batch, shown[i].Length > 90 ? shown[i][..90] + "..." : shown[i], chat.X + 10 * Scale, chat.Y + (8 + i * 23) * Scale, 10, Vector4.One);
        Box(batch, ChatBox, "CHAT", chatLine, focus == Field.Chat);
        bool everyoneReady = Enumerable.Range(0, GameInfo.MaxSlots).All(i => lobby.SlotClient[i] < 0 || lobby.Accepted[i]);
        if (session.IsHost)
            Button(batch, MainButton, "START", everyoneReady);
        else
            Button(batch, MainButton, ready ? "NOT READY" : "READY");
    }

    public void Dispose()
    {
        foreach (var action in unsubscribe)
            action();
        listener?.Dispose();
        session?.Dispose();
    }
}
