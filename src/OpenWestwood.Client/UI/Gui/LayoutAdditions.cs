using OpenWestwood.Formats.Wnd;

namespace OpenWestwood.Client.UI.Gui;

/// <summary>
/// OpenWESTWOOD's few additions to the original .wnd layouts, made as they load (the files themselves stay
/// untouched): the main menu's single player panel gets a "MORE CAMPAIGNS" button, a copy of its Skirmish
/// button, for the other games' campaigns; the skirmish and network game setups get a game speed choice and the
/// "Explored map" / "No fog of war" boxes (copies of their starting cash list and superweapon box).
/// <see cref="TransitionCopies"/> gives an added window the window transitions of the window it copies.
/// </summary>
public static class LayoutAdditions
{
    public const string MoreCampaignsButton = "MainMenu.wnd:ButtonMoreCampaigns";
    public const string SoleSurvivorButton = "MainMenu.wnd:ButtonSoleSurvivor";

    /// <summary>Added window => the original window whose transitions it shares.</summary>
    public static readonly Dictionary<string, string> TransitionCopies = new(StringComparer.OrdinalIgnoreCase)
    {
        [MoreCampaignsButton] = "MainMenu.wnd:ButtonSkirmish",
        [SoleSurvivorButton] = "MainMenu.wnd:ButtonSkirmish",
    };

    public static void Apply(string fileName, List<WndWindow> roots, string text)
    {
        if (fileName.Equals("MainMenu.wnd", StringComparison.OrdinalIgnoreCase))
            AddMoreCampaigns(roots, text);
        else if (fileName.Equals("SkirmishGameOptionsMenu.wnd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("LanGameOptionsMenu.wnd", StringComparison.OrdinalIgnoreCase))
            AddGameOptions(fileName, roots, text);
        else if (fileName.Equals("OptionsMenu.wnd", StringComparison.OrdinalIgnoreCase))
            AddViewOption(roots, text);
    }

    public const string WestwoodViewBox = "OptionsMenu.wnd:CheckBoxWestwoodView";

    /// <summary>
    /// The options popup: "Westwood view of TS/RA2 maps", a copy of its lowest check box, put under it (see
    /// Shell/ViewPreferences). <see cref="WireViewOption"/> hooks it up.
    /// </summary>
    static void AddViewOption(List<WndWindow> roots, string text)
    {
        if (Find(roots, WestwoodViewBox) != null)
            return;
        var boxes = new List<WndWindow>();
        void Collect(IEnumerable<WndWindow> windows)
        {
            foreach (var w in windows)
            {
                if (w.Type.Equals("CHECKBOX", StringComparison.OrdinalIgnoreCase) && !w.Status.Contains("HIDDEN"))
                    boxes.Add(w);
                Collect(w.Children);
            }
        }
        Collect(roots);
        if (boxes.Count == 0)
            return;
        // Zero Hour's: under Retaliation in the scrolling/controls panel, which has a free row; otherwise under the
        // lowest box there is.
        var lowest = boxes.FirstOrDefault(b => b.Name.Equals("OptionsMenu.wnd:Retaliation", StringComparison.OrdinalIgnoreCase))
            ?? boxes.MaxBy(b => b.Y)!;
        var parent = ParentOf(roots, lowest);
        var copy = Find(WndFile.Parse(text), lowest.Name);
        if (parent == null || copy == null)
            return;
        copy.Name = WestwoodViewBox;
        copy.Text = "Westwood view of TS/RA2 maps";
        copy.Tooltip = "";
        copy.Y = lowest.Y + lowest.Height;
        copy.Width = Math.Max(copy.Width, Math.Min(280, parent.X + parent.Width - copy.X - 4));
        parent.Children.Add(copy);
    }

    /// <summary>Hooks up the options popup's view box (shell and in-game menus alike).</summary>
    public static void WireViewOption(GuiLayout layout)
    {
        if (layout.Find<GuiCheckBox>(WestwoodViewBox) is not { } box)
            return;
        box.Checked = OpenWestwood.Client.Shell.ViewPreferences.WestwoodView;
        box.Clicked += _ => OpenWestwood.Client.Shell.ViewPreferences.WestwoodView = box.Checked;
    }

    public const string ExploredMapBox = "CheckboxExploredMap", NoFogBox = "CheckboxNoFogOfWar", GameSpeedList = "ComboBoxGameSpeed";

    /// <summary>
    /// Skirmish: the boxes go under Limit Superweapons, and the speed list takes the place of the original FPS
    /// slider. Network setup (which has no speed slider and its chat right under the options): one more row of
    /// options under the first, the chat window a little shorter to make room.
    /// </summary>
    static void AddGameOptions(string fileName, List<WndWindow> roots, string text)
    {
        string prefix = fileName + ":";
        var superweapons = Find(roots, prefix + "CheckboxLimitSuperweapons");
        var cash = Find(roots, prefix + "ComboBoxStartingCash");
        if (superweapons == null || cash == null || Find(roots, prefix + ExploredMapBox) != null)
            return;
        var parent = ParentOf(roots, superweapons);
        if (parent == null)
            return;
        var fresh = WndFile.Parse(text);
        WndWindow Copy(string name, string newName)
        {
            var copy = Find(WndFile.Parse(text), prefix + name)!;
            copy.Name = prefix + newName;
            copy.Tooltip = "";
            parent.Children.Add(copy);
            return copy;
        }
        var explored = Copy("CheckboxLimitSuperweapons", ExploredMapBox);
        explored.Text = "Explored map";
        var noFog = Copy("CheckboxLimitSuperweapons", NoFogBox);
        noFog.Text = "No fog of war";
        var speed = Copy("ComboBoxStartingCash", GameSpeedList);

        if (Find(roots, prefix + "SliderGameSpeed") is { } slider)
        {
            // Skirmish.
            int row = superweapons.Height + 4;
            explored.Y = superweapons.Y + row;
            noFog.Y = superweapons.Y + 2 * row;
            speed.X = slider.X;
            speed.Y = cash.Y;
            slider.Status.Add("HIDDEN");
            if (Find(roots, prefix + "StaticTextGameSpeed") is { } fpsText)
                fpsText.Status.Add("HIDDEN");
            return;
        }

        // Network setup.
        int y = superweapons.Y + superweapons.Height + 6;
        int shift = y + superweapons.Height + 6 - (Find(roots, prefix + "ListboxChatWindowLanGame")?.Y ?? y);
        if (Find(roots, prefix + "ListboxChatWindowLanGame") is { } chat && shift > 0)
        {
            chat.Y += shift;
            chat.Height -= shift;
        }
        speed.Y = y;
        explored.Y = noFog.Y = y;
        explored.X = superweapons.X;
        noFog.X = superweapons.X + superweapons.Width + 4;
        noFog.Width = Math.Min(noFog.Width, parent.X + parent.Width - noFog.X - 2);
        // The "Starting Money" label's twin for the speed list.
        if (FindByText(fresh, "GUI:StartingMoney") is { } label)
        {
            label.Name = prefix + "StaticTextGameSpeedLabel";
            label.Text = "GUI:GameSpeed";
            label.Y = y;
            parent.Children.Add(label);
        }
    }

    static WndWindow? FindByText(IEnumerable<WndWindow> windows, string text)
    {
        foreach (var window in windows)
        {
            if (window.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
                return window;
            if (FindByText(window.Children, text) is { } found)
                return found;
        }
        return null;
    }

    static void AddMoreCampaigns(List<WndWindow> roots, string text)
    {
        var panel = Find(roots, "MainMenu.wnd:MapBorder");
        var back = Find(roots, "MainMenu.wnd:ButtonSingleBack");
        // A separate copy of the Skirmish button: parse the file again and take it from there.
        var copy = Find(WndFile.Parse(text), "MainMenu.wnd:ButtonSkirmish");
        if (panel == null || back == null || copy == null || Find(roots, MoreCampaignsButton) != null)
            return;
        const int step = 40;
        copy.Name = MoreCampaignsButton;
        copy.Text = "MORE CAMPAIGNS";
        copy.Tooltip = "";
        copy.Y = back.Y;
        back.Y += step;
        // The buttons sit inside the panel's EarthMap backdrop; the new one goes beside them there, and both grow.
        var holder = ParentOf(roots, back) ?? panel;
        if (holder != panel)
            holder.Height += step;
        panel.Height += step;
        holder.Children.Insert(holder.Children.IndexOf(back) + 1, copy);

        // OpenWESTWOOD: Sole Survivor Remix, another game on the same engine, under it.
        if (Find(WndFile.Parse(text), "MainMenu.wnd:ButtonSkirmish") is { } survivor)
        {
            survivor.Name = SoleSurvivorButton;
            survivor.Text = "SOLE SURVIVOR REMIX";
            survivor.Tooltip = "";
            survivor.Y = back.Y;
            back.Y += step;
            if (holder != panel)
                holder.Height += step;
            panel.Height += step;
            holder.Children.Insert(holder.Children.IndexOf(back), survivor);
        }
    }

    static WndWindow? ParentOf(IEnumerable<WndWindow> windows, WndWindow child)
    {
        foreach (var window in windows)
        {
            if (window.Children.Contains(child))
                return window;
            if (ParentOf(window.Children, child) is { } found)
                return found;
        }
        return null;
    }

    static WndWindow? Find(IEnumerable<WndWindow> windows, string name)
    {
        foreach (var window in windows)
        {
            if (window.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return window;
            if (Find(window.Children, name) is { } found)
                return found;
        }
        return null;
    }
}
