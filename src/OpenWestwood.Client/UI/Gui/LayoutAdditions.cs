using OpenWestwood.Formats.Wnd;

namespace OpenWestwood.Client.UI.Gui;

/// <summary>
/// OpenWESTWOOD's few additions to the original .wnd layouts, made as they load (the files themselves stay
/// untouched): the main menu's single player panel gets a "MORE CAMPAIGNS" button, a copy of its Skirmish
/// button, for the other games' campaigns. <see cref="TransitionCopies"/> gives an added window the window
/// transitions of the window it copies.
/// </summary>
public static class LayoutAdditions
{
    public const string MoreCampaignsButton = "MainMenu.wnd:ButtonMoreCampaigns";

    /// <summary>Added window => the original window whose transitions it shares.</summary>
    public static readonly Dictionary<string, string> TransitionCopies = new(StringComparer.OrdinalIgnoreCase)
    {
        [MoreCampaignsButton] = "MainMenu.wnd:ButtonSkirmish",
    };

    public static void Apply(string fileName, List<WndWindow> roots, string text)
    {
        if (fileName.Equals("MainMenu.wnd", StringComparison.OrdinalIgnoreCase))
            AddMoreCampaigns(roots, text);
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
