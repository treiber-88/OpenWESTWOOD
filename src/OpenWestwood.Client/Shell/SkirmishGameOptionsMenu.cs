using System.Numerics;
using OpenWestwood.Client.UI.Gui;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Client.Network;

namespace OpenWestwood.Client.Shell;

/// <summary>
/// The skirmish setup screen (the original's SkirmishGameOptionsMenu.cpp with the GUIUtil helpers), and in network
/// mode the network game's setup (LanGameOptionsMenu.wnd, the original's LanGameOptionsMenu.cpp): the host sets the
/// map, the options and the computer players, each player chooses their own colour, faction, team and start, and
/// accepts; the host starts once everyone has. The lobby lives on the host (see Network.NetHost): every change goes
/// there and comes back to everyone.
/// eight slots of player / colour / faction / team, start positions clicked on the map preview,
/// starting cash, the superweapon limit and the map choice. Start builds the game from the
/// <see cref="GameInfo"/>. Choices are remembered in Skirmish.ini, as the original's preferences.
/// </summary>
public sealed class SkirmishGameOptionsMenu : ShellMenu
{
    readonly string LayoutName;
    readonly bool networkLayout;
    Network.NetSession? net;
    GuiListBox? listChat;
    GuiTextEntry? textEntryChat;
    readonly GuiPushButton?[] buttonAccept = new GuiPushButton?[GameInfo.MaxSlots];
    int chatShown = -1;
    static readonly Vector4 White = Vector4.One;

    readonly GuiComboBox?[] comboPlayer = new GuiComboBox?[GameInfo.MaxSlots];
    readonly GuiComboBox?[] comboColor = new GuiComboBox?[GameInfo.MaxSlots];
    readonly GuiComboBox?[] comboPlayerTemplate = new GuiComboBox?[GameInfo.MaxSlots];
    readonly GuiComboBox?[] comboTeam = new GuiComboBox?[GameInfo.MaxSlots];
    readonly GuiPushButton?[] buttonMapStartPosition = new GuiPushButton?[GameInfo.MaxSlots];
    readonly List<(int Index, string Name, RuleNode Rules)> templates;
    readonly List<(string Name, uint Rgb, string TooltipName)> colors;
    GuiComboBox? comboStartingCash;
    GuiCheckBox? checkLimitSuperweapons, checkExploredMap, checkNoFog;
    GuiComboBox? comboGameSpeed;
    GuiTextEntry? textEntryPlayerName;
    GuiWindow? textMapDisplay, mapWindow, staticTextGameSpeed;
    GuiSlider? sliderGameSpeed;
    GuiPushButton? buttonStart;
    bool buttonPushed, justEntered, stillNeedsToSetOptions, isShuttingDown;
    int initialGadgetDelay = 2;
    GuiLayout? mapSelectLayout;

    GameInfo Info => Screen.SkirmishGameInfo!;

    public SkirmishGameOptionsMenu(ShellScreen screen, GuiLayout layout, bool network = false) : base(screen, layout)
    {
        networkLayout = network;
        net = network ? screen.NetSession : null;
        LayoutName = network ? "LanGameOptionsMenu.wnd" : "SkirmishGameOptionsMenu.wnd";
        var rules = screen.Ui.Content.Rules;
        templates = GameInfo.PlayerTemplates(rules);
        colors = GameInfo.Colors(rules);
        OnClick("ButtonBack", () =>
        {
            if (buttonPushed)
                return;
            buttonPushed = true;
            if (net != null)
            {
                // Leaving a network game's setup leaves the game (a host closes it for everyone).
                net.Dispose();
                net = null;
                Screen.NetSession = null;
            }
            else
                SkirmishPreferences.Write(Info, sliderGameSpeed?.Value ?? 30);
            CloseMapSelect();
            Shell.Pop();
            Screen.SkirmishGameInfo = null;
        });
        OnClick("ButtonStart", () =>
        {
            if (buttonPushed)
                return;
            if (net != null)
            {
                NetworkStartPressed();
                return;
            }
            buttonPushed = true;
            SkirmishPreferences.Write(Info, sliderGameSpeed?.Value ?? 30);
            StartPressed();
        });
        OnClick("ButtonSelectMap", OpenMapSelect);
        // (Reset clears the battle honors; those are not kept yet.)
    }

    string Text(string label) => Screen.Ui.Strings.Get(label);

    // ---- Init (SkirmishGameOptionsMenuInit) ----

    public override void Init()
    {
        buttonPushed = false;
        if (networkLayout)
        {
            InitNetwork();
            return;
        }
        Screen.SkirmishGameInfo ??= new GameInfo();
        var info = Info;
        foreach (var slot in info.Slots)
            slot.SetState(SlotState.Open);
        InitGadgets();

        var prefs = SkirmishPreferences.Load(Screen.Ui.Content.Rules, Screen.MapCache);
        info.Slots[0].SetState(SlotState.Player, prefs.UserName);
        info.Slots[0].Color = prefs.PreferredColor;
        info.Slots[0].PlayerTemplate = prefs.PreferredFaction;
        info.Slots[1].SetState(SlotState.EasyAI, Text("GUI:EasyAI"));
        prefs.ApplySlotList(info);
        info.Seed = (ulong)Environment.TickCount64;
        info.StartingCash = prefs.StartingCash;
        info.SuperweaponRestriction = prefs.SuperweaponRestricted;
        info.ExploredMap = prefs.ExploredMap;
        info.NoFogOfWar = prefs.NoFogOfWar;
        info.GameSpeed = prefs.GameSpeed;
        info.Map = prefs.PreferredMap;
        if (sliderGameSpeed != null)
            sliderGameSpeed.Value = Math.Clamp(prefs.Fps, 15, 61);
        SetFpsText(sliderGameSpeed?.Value ?? 30);
        if (buttonStart != null)
            buttonStart.Text = Text("GUI:Start");

        Layout.Hidden = false;
        Screen.ShowShellMap(true);
        UpdateSkirmishGameOptions();
        UpdateSlotList();
        justEntered = true;
        initialGadgetDelay = 2;
        if (Window($"{LayoutName}:SubParent") is { } sub)
            sub.Hidden = true;
    }

    /// <summary>A new check box like <paramref name="like"/>, <paramref name="row"/> rows below it.</summary>
    void InitGadgets()
    {
        comboStartingCash = Layout.Find<GuiComboBox>($"{LayoutName}:ComboBoxStartingCash");
        checkLimitSuperweapons = Layout.Find<GuiCheckBox>($"{LayoutName}:CheckboxLimitSuperweapons");
        textEntryPlayerName = Layout.Find<GuiTextEntry>($"{LayoutName}:TextEntryPlayerName");
        textMapDisplay = Window($"{LayoutName}:TextEntryMapDisplay");
        mapWindow = Window($"{LayoutName}:MapWindow");
        staticTextGameSpeed = Window($"{LayoutName}:StaticTextGameSpeed");
        sliderGameSpeed = Layout.Find<GuiSlider>($"{LayoutName}:SliderGameSpeed");
        buttonStart = Button($"{LayoutName}:ButtonStart");
        PopulateStartingCash();
        if (comboStartingCash != null)
            comboStartingCash.SelectionChanged += c =>
            {
                if (c.SelectedData is int cash)
                    Info.StartingCash = cash;
                UpdateSlotList();
                Edited();
            };
        if (checkLimitSuperweapons != null)
        {
            checkLimitSuperweapons.Clicked += _ => { Info.SuperweaponRestriction = checkLimitSuperweapons.Checked; Edited(); };
        }
        // OpenWESTWOOD's options (added to the layout as it loads: UI/Gui/LayoutAdditions).
        checkExploredMap = Layout.Find<GuiCheckBox>($"{LayoutName}:{LayoutAdditions.ExploredMapBox}");
        checkNoFog = Layout.Find<GuiCheckBox>($"{LayoutName}:{LayoutAdditions.NoFogBox}");
        comboGameSpeed = Layout.Find<GuiComboBox>($"{LayoutName}:{LayoutAdditions.GameSpeedList}");
        if (checkExploredMap != null)
            checkExploredMap.Clicked += _ => { Info.ExploredMap = checkExploredMap.Checked; Edited(); };
        if (checkNoFog != null)
            checkNoFog.Clicked += _ => { Info.NoFogOfWar = checkNoFog.Checked; Edited(); };
        if (comboGameSpeed != null)
        {
            comboGameSpeed.Reset();
            for (int i = 0; i < GameInfo.GameSpeedNames.Length; i++)
                comboGameSpeed.AddEntry(GameInfo.GameSpeedNames[i], comboGameSpeed.Definition.TextEnabled, i);
            comboGameSpeed.SetSelectedPos(0);
            comboGameSpeed.SelectionChanged += c =>
            {
                if (c.SelectedData is int speed)
                    Info.GameSpeed = speed;
                Edited();
            };
        }
        if (mapWindow != null)
            mapWindow.CustomDraw = DrawMapPreview;

        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            int index = i;
            if (i != 0 || networkLayout)
            {
                var player = comboPlayer[i] = Layout.Find<GuiComboBox>($"{LayoutName}:ComboBoxPlayer{i}");
                if (player != null)
                {
                    PopulatePlayerComboBox(player);
                    player.SetSelectedPos(0);
                    player.SelectionChanged += c =>
                    {
                        if (c.SelectedData is SlotState state)
                            Info.Slots[index].SetState(state, c.Selected!.Text);
                        UpdateSlotList();
                        Edited(index);
                    };
                }
            }
            comboColor[i] = Layout.Find<GuiComboBox>($"{LayoutName}:ComboBoxColor{i}");
            comboPlayerTemplate[i] = Layout.Find<GuiComboBox>($"{LayoutName}:ComboBoxPlayerTemplate{i}");
            comboTeam[i] = Layout.Find<GuiComboBox>($"{LayoutName}:ComboBoxTeam{i}");
            buttonMapStartPosition[i] = Layout.Find<GuiPushButton>($"{LayoutName}:ButtonMapStartPosition{i}");
            if (comboColor[i] is { } color)
                color.SelectionChanged += c => { HandleColorSelection(index, c); UpdateSlotList(); Edited(index); };
            if (comboPlayerTemplate[i] is { } template)
                template.SelectionChanged += c => { if (c.SelectedData is int t) Info.Slots[index].PlayerTemplate = t; UpdateSlotList(); Edited(index); };
            if (comboTeam[i] is { } team)
                team.SelectionChanged += c => { if (c.SelectedData is int t) Info.Slots[index].TeamNumber = t; UpdateSlotList(); Edited(index); };
            buttonAccept[i] = Layout.Find<GuiPushButton>($"{LayoutName}:ButtonAccept{i}");
            if (buttonMapStartPosition[i] is { } start)
                start.Clicked += _ => StartPositionClicked(index);
        }
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            PopulateColorComboBox(i, isObserver: false);
            comboColor[i]?.SetSelectedPos(0);
            PopulatePlayerTemplateComboBox(i);
            PopulateTeamComboBox(i);
        }
    }

    // ---- GUIUtil ----

    Vector4 RandomColor => new(1, 1, 1, 1);

    void PopulateColorComboBox(int slotIndex, bool isObserver)
    {
        if (comboColor[slotIndex] is not { } combo)
            return;
        var available = Enumerable.Repeat(true, colors.Count).ToArray();
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            var slot = Info.Slots[i];
            if (i != slotIndex && slot.Color >= 0 && slot.Color < colors.Count)
                available[slot.Color] = false;
        }
        bool wasObserver = combo.Entries.Count == 1;
        combo.Reset();
        combo.AddEntry(isObserver ? Text("GUI:None") : Text("GUI:???"), RandomColor, -1);
        if (isObserver)
        {
            combo.SetSelectedPos(0);
            return;
        }
        for (int c = 0; c < colors.Count; c++)
        {
            if (!available[c])
                continue;
            uint rgb = colors[c].Rgb;
            combo.AddEntry(Text(colors[c].TooltipName), new Vector4((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1), c);
        }
        if (wasObserver)
            combo.SetSelectedPos(0);
    }

    void PopulatePlayerTemplateComboBox(int slotIndex)
    {
        if (comboPlayerTemplate[slotIndex] is not { } combo)
            return;
        combo.Reset();
        combo.AddEntry(Text("GUI:Random"), RandomColor, GameSlot.PlayerTemplateRandom);
        var seenSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locked = Screen.Ui.Content.Rules.Section("ChallengeGenerals").Entries
            .Where(e => !RuleValue.Bool(e.Value.GetString("StartsEnabled"), true)).Select(e => e.Value.GetString("PlayerTemplate"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, name, rules) in templates)
        {
            if (!GameInfo.IsLobbyFaction(rules) || locked.Contains(name) || net?.Lobby is { } lobby && !lobby.IsFactionAllowed(rules))
                continue;
            string side = $"SIDE:{rules.GetString("Side")}";
            if (!seenSides.Add(side))
                continue;
            // Tiberian Dawn factions have no entry in the game's string table: they show their DisplayName.
            // (OpenWESTWOOD: each with its flag or emblem, the faction's LobbyFlag.)
            combo.AddEntry(GameInfo.IsTdFaction(rules) ? rules.GetString("DisplayName", name) : Text(side), RandomColor, index,
                rules.GetString("LobbyFlag") is { Length: > 0 } flag ? flag : rules.GetString("SideIconImage") is { Length: > 0 } icon ? icon : null);
        }
        combo.SetSelectedPos(0);
    }

    void PopulateTeamComboBox(int slotIndex)
    {
        if (comboTeam[slotIndex] is not { } combo)
            return;
        combo.Reset();
        combo.AddEntry(Text("Team:0"), RandomColor, -1);
        for (int c = 0; c < GameInfo.MaxSlots / 2; c++)
            combo.AddEntry(Text($"Team:{c + 1}"), RandomColor, c);
        combo.SetSelectedPos(0);
    }

    void PopulateStartingCash()
    {
        if (comboStartingCash is not { } combo)
            return;
        combo.Reset();
        string format = Text("GUI:StartingMoneyFormat");
        int current = -1;
        foreach (int cash in SkirmishPreferences.StartingMoneyChoices(Screen.Ui.Content.Rules))
        {
            int index = combo.AddEntry(FormatMoney(format, cash), combo.Definition.TextEnabled, cash);
            if (cash == Info.StartingCash)
                current = index;
        }
        if (current == -1)
            current = combo.AddEntry(FormatMoney(format, Info.StartingCash), combo.Definition.TextEnabled, Info.StartingCash);
        combo.SetSelectedPos(current);
    }

    static string FormatMoney(string format, int cash) =>
        format.Contains("%d") ? format.Replace("%d", cash.ToString()) : $"${cash}";

    void HandleColorSelection(int index, GuiComboBox combo)
    {
        if (combo.SelectedData is not int color)
            return;
        var slot = Info.Slots[index];
        if (color == slot.Color)
            return;
        if (color >= 0 && Info.IsColorTaken(color, index))
            return;
        slot.Color = color;
    }

    /// <summary>The original's start position button handling: claim a free spot, or pass it on to the next player.</summary>
    void StartPositionClicked(int position)
    {
        int playerInPos = Array.FindIndex(Info.Slots, s => s.IsOccupied && s.StartPos == position);
        if (playerInPos >= 0)
        {
            int next = NextSelectablePlayer(playerInPos + 1);
            Info.Slots[playerInPos].StartPos = -1;
            if (next >= 0)
                HandleStartPositionSelection(next, position);
            Edited(playerInPos);
            if (next >= 0)
                Edited(next);
        }
        else
        {
            int next = NextSelectablePlayer(0);
            if (next < 0)
                next = Info.LocalSlot;
            HandleStartPositionSelection(next, position);
            Edited(next);
        }
        UpdateSlotList();
    }

    int NextSelectablePlayer(int start)
    {
        for (int j = start; j < GameInfo.MaxSlots; j++)
            if (Info.Slots[j].IsOccupied && Info.Slots[j].StartPos < 0 && Info.Slots[j].PlayerTemplate != GameSlot.PlayerTemplateObserver
                && CanEditSlot(j))
                return j;
        return -1;
    }

    void HandleStartPositionSelection(int index, int position)
    {
        var slot = Info.Slots[index];
        if (position == slot.StartPos)
            return;
        if (position < 0 || !Info.Slots.Where((s, i) => i != index).Any(s => s.StartPos == position))
            slot.StartPos = position;
    }

    /// <summary>The original's UpdateSlotList: shows each slot's state in its combo boxes.</summary>
    void UpdateSlotList()
    {
        var info = Info;
        if (Screen.MapCache.Find(info.Map) is { } meta && textMapDisplay != null)
            textMapDisplay.Text = DisplayName(meta);
        if (textEntryPlayerName != null)
            textEntryPlayerName.Text = info.Slots[0].Name;
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            var slot = info.Slots[i];
            bool enabled = slot.IsOccupied && CanEditSlot(i);
            if (net != null && comboPlayer[i] is { } netPlayer)
            {
                // A player's slot shows their name; the host may open it (which sends them away). Other slots: the
                // host's choice of open, closed or a computer player.
                PopulatePlayerComboBox(netPlayer, slot.IsHuman ? slot.Name : null);
                netPlayer.SetSelectedPos(Math.Max(0, netPlayer.IndexOfData(slot.State)));
                netPlayer.Enabled = net.IsHost && (net.Lobby?.SlotClient[i] ?? -1) != net.ClientId;
            }
            else if (i != 0 && comboPlayer[i] is { } player)
                player.SetSelectedPos(player.IndexOfData(slot.State));
            if (buttonAccept[i] is { } accept)
            {
                accept.Hidden = !slot.IsHuman;
                accept.Enabled = false;
                accept.Text = net?.Lobby is { } l && l.Accepted[i] ? "\u221A" : "";
            }
            if (comboColor[i] is { } color)
            {
                color.Enabled = enabled;
                PopulateColorComboBox(i, slot.PlayerTemplate == GameSlot.PlayerTemplateObserver);
                color.SetSelectedPos(Math.Max(0, color.IndexOfData(slot.Color)));
            }
            if (comboTeam[i] is { } team)
            {
                team.Enabled = enabled;
                team.SetSelectedPos(Math.Max(0, team.IndexOfData(slot.TeamNumber)));
            }
            if (comboPlayerTemplate[i] is { } template)
            {
                template.Enabled = enabled;
                template.SetSelectedPos(Math.Max(0, template.IndexOfData(slot.PlayerTemplate)));
            }
        }
        UpdateMapStartSpots();
        if (net != null)
        {
            if (buttonStart != null)
                buttonStart.Text = net.IsHost ? Text("GUI:Start") : Text("GUI:Accept");
            if (Button($"{LayoutName}:ButtonSelectMap") is { } selectMap)
                selectMap.Hidden = !net.IsHost;
            comboStartingCash!.Enabled = net.IsHost;
            if (checkLimitSuperweapons != null)
                checkLimitSuperweapons.Enabled = net.IsHost;
            if (checkExploredMap != null)
                checkExploredMap.Enabled = net.IsHost;
            if (checkNoFog != null)
                checkNoFog.Enabled = net.IsHost;
            if (comboGameSpeed != null)
                comboGameSpeed.Enabled = net.IsHost;
        }
    }

    string DisplayName(MapMetaData meta) =>
        meta.NameLookupTag.Length > 0 ? Text(meta.NameLookupTag) : Path.GetFileNameWithoutExtension(meta.FileName);

    void UpdateMapStartSpots()
    {
        var meta = Screen.MapCache.Find(Info.Map);
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            if (buttonMapStartPosition[i] is not { } button)
                continue;
            button.Hidden = meta == null || !meta.IsMultiplayer || i >= meta.NumPlayers || meta.StartPosition(i) == null;
            button.Text = "";
        }
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            var slot = Info.Slots[i];
            if (meta != null && slot.IsOccupied && slot.StartPos >= 0 && slot.StartPos < meta.NumPlayers
                && buttonMapStartPosition[slot.StartPos] is { } button)
                button.Text = Text($"NUMBER:{i + 1}");
        }
    }

    void UpdateSkirmishGameOptions()
    {
        if (checkLimitSuperweapons != null)
            checkLimitSuperweapons.Checked = Info.SuperweaponRestriction;
        if (checkExploredMap != null)
            checkExploredMap.Checked = Info.ExploredMap;
        if (checkNoFog != null)
            checkNoFog.Checked = Info.NoFogOfWar;
        comboGameSpeed?.SetSelectedPos(Math.Clamp(Info.GameSpeed, 0, GameInfo.GameSpeeds.Length - 1));
        if (comboStartingCash != null)
            comboStartingCash.SetSelectedPos(Math.Max(0, comboStartingCash.IndexOfData(Info.StartingCash)));
        UpdateSlotList();
    }

    void SetFpsText(int sliderPos)
    {
        if (staticTextGameSpeed == null)
            return;
        staticTextGameSpeed.Enabled = sliderPos != 30;
        staticTextGameSpeed.Text = sliderPos > 60 ? "--" : sliderPos.ToString("00");
    }

    // ---- The map preview (W3DDrawMapPreview) ----

    void DrawMapPreview(GuiWindow window, GuiContext context)
    {
        // (Leaving with Back clears the setup while the menu is still fading out.)
        if (Screen.SkirmishGameInfo is not { } info)
            return;
        MapPreviewDrawer.Draw(window, context, Screen.MapCache.Find(info.Map), Screen.MapPreview(info.Map));
    }

    /// <summary>Keeps the start-spot buttons over their places on the preview (positionStartSpotControls).</summary>
    void PositionStartSpots()
    {
        if (mapWindow != null && Screen.SkirmishGameInfo is { } info && Screen.MapCache.Find(info.Map) is { } meta)
            MapPreviewDrawer.PositionStartButtons(mapWindow, meta, buttonMapStartPosition);
    }

    // ---- Starting ----

    void StartPressed()
    {
        var meta = Screen.MapCache.Find(Info.Map);
        if (meta == null)
        {
            buttonPushed = false;
            Screen.ShowMessageBox(Text("GUI:ErrorStartingGame"), Text("GUI:CantFindMap"));
            return;
        }
        if (Info.NumPlayers > meta.NumPlayers)
        {
            buttonPushed = false;
            string message = Text("GUI:TooManyPlayers");
            Screen.ShowMessageBox(Text("GUI:ErrorStartingGame"), message.Contains("%d") ? message.Replace("%d", meta.NumPlayers.ToString()) : message);
            return;
        }
        Screen.StartSkirmish(Info);
    }

    // ---- Network games (LanGameOptionsMenu) ----

    void PopulatePlayerComboBox(GuiComboBox player, string? humanName = null)
    {
        player.Reset();
        if (humanName != null)
            player.AddEntry(humanName, White, SlotState.Player);
        player.AddEntry(Text("GUI:Open"), White, SlotState.Open);
        player.AddEntry(Text("GUI:Closed"), White, SlotState.Closed);
        player.AddEntry(Text("GUI:EasyAI"), White, SlotState.EasyAI);
        player.AddEntry(Text("GUI:MediumAI"), White, SlotState.MedAI);
        player.AddEntry(Text("GUI:HardAI"), White, SlotState.BrutalAI);
    }

    /// <summary>Whether this machine may change a slot: its own, or (the host) the computer players'.</summary>
    bool CanEditSlot(int slot)
    {
        if (net?.Lobby is not { } lobby)
            return true;
        int owner = lobby.SlotClient[slot];
        return owner == net.ClientId || net.IsHost && owner < 0;
    }

    /// <summary>A change made here goes to the host: my own slot as a request, the host's edits as the settings.</summary>
    void Edited(int slot = -1)
    {
        if (net?.Lobby is not { } lobby)
            return;
        int mine = lobby.SlotOf(net.ClientId);
        if (slot >= 0 && slot == mine)
        {
            var s = Info.Slots[slot];
            net.RequestSlot(s.Color, s.PlayerTemplate, s.TeamNumber, s.StartPos, accept: net.IsHost);
        }
        else if (net.IsHost)
            net.SendHostOptions(Info);
    }

    void InitNetwork()
    {
        if (net == null)
        {
            // Back from a network game's score screen: the game is over, back to the lobby.
            Shell.Pop();
            return;
        }
        Screen.SkirmishGameInfo = new GameInfo();
        listChat = Layout.Find<GuiListBox>($"{LayoutName}:ListboxChatWindowLanGame");
        textEntryChat = Layout.Find<GuiTextEntry>($"{LayoutName}:TextEntryChat");
        InitGadgets();
        net.LobbyChanged += ApplyLobby;
        net.Started += info =>
        {
            if (net == null)
                return;
            var session = net;
            net = null; // the game takes the link over
            CloseMapSelect();
            Screen.StartNetworkGame(info, session);
        };
        if (net.Lobby != null)
            ApplyLobby();
        Layout.Hidden = false;
        Screen.ShowShellMap(true);
        justEntered = true;
        initialGadgetDelay = 2;
    }

    /// <summary>The host sent the lobby: show it.</summary>
    void ApplyLobby()
    {
        if (net?.Lobby is not { } lobby)
            return;
        // A copy to show and edit (edits go back to the host).
        Screen.SkirmishGameInfo = NetLobby.ReadInfo(NetConnection.Unpack(NetConnection.Pack(w => NetLobby.WriteInfo(w, lobby.Info))));
        // A new game: the host's last skirmish map, or else the first map every player can play.
        if (net.IsHost && (Info.Map.Length == 0 || !lobby.IsMapAllowed(Info.Map)))
        {
            string preferred = SkirmishPreferences.Load(Screen.Ui.Content.Rules, Screen.MapCache).PreferredMap;
            Info.Map = lobby.IsMapAllowed(preferred) && Screen.MapCache.Find(preferred) is { IsMultiplayer: true } ? preferred
                : Screen.MapCache.Maps.Where(m => m.IsMultiplayer && lobby.IsMapAllowed(m.FileName)).OrderBy(m => m.FileName).FirstOrDefault()?.FileName ?? "";
            if (Info.Map.Length > 0)
                Edited();
        }
        for (int i = 0; i < GameInfo.MaxSlots; i++)
            PopulatePlayerTemplateComboBox(i);
        PopulateStartingCash();
        UpdateSkirmishGameOptions();
    }

    void UpdateNetwork()
    {
        if (net == null)
            return;
        net.Poll();
        // (The start hands the link to the game: nothing more to do here.)
        if (net == null)
            return;
        if (net.Error != null || !net.IsConnected)
        {
            string reason = net.Error ?? net.CloseReason ?? "The link to the host was lost.";
            net.Dispose();
            net = null;
            Screen.NetSession = null;
            Screen.ShowMessageBox("Network game", reason);
            Shell.Pop();
            return;
        }
        if (listChat != null && chatShown != net.Chat.Count)
        {
            chatShown = net.Chat.Count;
            listChat.Items.Clear();
            listChat.Items.AddRange(net.Chat);
            listChat.SelectedIndex = listChat.Items.Count - 1;
            listChat.ScrollToSelected();
            listChat.SelectedIndex = -1;
        }
    }

    void NetworkStartPressed()
    {
        if (net?.Lobby is not { } lobby)
            return;
        if (!net.IsHost)
        {
            int mine = lobby.SlotOf(net.ClientId);
            if (mine >= 0)
            {
                var s = Info.Slots[mine];
                net.RequestSlot(s.Color, s.PlayerTemplate, s.TeamNumber, s.StartPos, accept: true);
            }
            return;
        }
        var meta = Screen.MapCache.Find(Info.Map);
        if (meta == null)
        {
            Screen.ShowMessageBox(Text("GUI:ErrorStartingGame"), Text("GUI:CantFindMap"));
            return;
        }
        if (Info.NumPlayers > meta.NumPlayers)
        {
            string message = Text("GUI:TooManyPlayers");
            Screen.ShowMessageBox(Text("GUI:ErrorStartingGame"), message.Contains("%d") ? message.Replace("%d", meta.NumPlayers.ToString()) : message);
            return;
        }
        net.RequestStart();
    }

    // ---- The map list popup ----

    void OpenMapSelect()
    {
        CloseMapSelect();
        ShowUnderlyingGuiElements(false);
        mapSelectLayout = Screen.OpenSkirmishMapSelect(Info.Map, map =>
        {
            Info.Map = map;
            foreach (var slot in Info.Slots)
                slot.StartPos = -1;
            CloseMapSelect();
            UpdateSkirmishGameOptions();
            Edited();
        }, CloseMapSelect, net?.Lobby is { } lobby ? lobby.IsMapAllowed : null);
    }

    void CloseMapSelect()
    {
        if (mapSelectLayout != null)
        {
            Screen.Gui.Remove(mapSelectLayout);
            ShowUnderlyingGuiElements(true);
        }
        mapSelectLayout = null;
    }

    /// <summary>
    /// The original's showSkirmishGameOptionsUnderlyingGUIElements: while the map list is open, the
    /// columns, map preview and buttons it covers are hidden (Reset and Back are only disabled).
    /// </summary>
    void ShowUnderlyingGuiElements(bool show)
    {
        var hidden = new List<string> { "MapWindow", "StaticTextTeam", "StaticTextFaction", "StaticTextColor" };
        foreach (string column in new[] { "ComboBoxTeam", "ComboBoxColor", "ComboBoxPlayerTemplate" })
            for (int i = 0; i < Logic.Multiplayer.GameInfo.MaxSlots; i++)
                hidden.Add(column + i);
        hidden.AddRange(["TextEntryMapDisplay", "ButtonSelectMap", "ButtonStart", "StaticTextMapPreview"]);
        foreach (string name in hidden)
            if (Window(LayoutName + ":" + name) is { } w)
                w.Hidden = !show;
        foreach (string name in new[] { "ButtonReset", "ButtonBack" })
            if (Window(LayoutName + ":" + name) is { } w)
                w.Enabled = show;
    }

    // ---- Update / shutdown ----

    public override void Update()
    {
        UpdateNetwork();
        if (justEntered)
        {
            if (initialGadgetDelay == 1)
            {
                stillNeedsToSetOptions = true;
                initialGadgetDelay = 2;
                justEntered = false;
            }
            else
                initialGadgetDelay--;
        }
        if (stillNeedsToSetOptions)
        {
            Transitions.SetGroup(networkLayout ? "LanGameOptionsFade" : "SkirmishGameOptionsMenuFade");
            stillNeedsToSetOptions = false;
        }
        if (net == null && textEntryPlayerName != null && textEntryPlayerName.Text != Info.Slots[0].Name)
            Info.Slots[0].Name = textEntryPlayerName.Text ?? "";
        if (sliderGameSpeed != null)
            SetFpsText(sliderGameSpeed.Value);
        PositionStartSpots();
        if (isShuttingDown && Shell.IsAnimFinished && Transitions.IsFinished)
        {
            isShuttingDown = false;
            Layout.Hidden = true;
            Shell.ShutdownComplete(this);
        }
    }

    public override void Shutdown(bool immediate)
    {
        GuiComboBox.Close();
        if (immediate)
        {
            Layout.Hidden = true;
            Shell.ShutdownComplete(this);
            return;
        }
        Transitions.Reverse(networkLayout ? "LanGameOptionsFade" : "SkirmishGameOptionsMenuFade");
        isShuttingDown = true;
    }

    public override bool OnKey(char character)
    {
        // Enter sends the chat line.
        if (character == '\r' && net != null && textEntryChat is { Text.Length: > 0 } chat)
        {
            net.SendChat(chat.Text);
            chat.Text = "";
            return true;
        }
        if (character == (char)27 && !buttonPushed)
        {
            Button("ButtonBack")?.SimulateClick();
            return true;
        }
        return false;
    }
}
