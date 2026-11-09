using System.Numerics;
using OpenWestwood.Client.UI;
using OpenWestwood.Client.UI.Gui;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Client.InGame;

/// <summary>
/// The general's promotions page (the original's GeneralsExpPoints.wnd, opened with the control bar's star
/// button): the side's rank 1, 3 and 8 promotions (its PurchaseScienceCommandSetRank1/3/8), the points to
/// spend, the rank and the experience bar. A promotion can be bought when it isn't owned yet, its
/// prerequisites are owned and there are points enough (ControlBar::populatePurchaseScience).
/// </summary>
public sealed class GeneralsPromotions
{
    readonly UiResources ui;
    readonly GameLogic game;
    readonly Player player;
    readonly List<(GuiPushButton Window, CommandButton? Button)> slots = new();
    int pointsSeen;

    public GuiLayout Layout { get; }

    public bool IsOpen => !Layout.Hidden;

    /// <summary>The star button flashes when there are new points to spend (the original's m_genStarFlash).</summary>
    public bool Flashing => player.SciencePurchasePoints > 0 && player.SciencePurchasePoints > pointsSeen;

    public string? Tooltip { get; private set; }

    public GeneralsPromotions(UiResources ui, GameLogic game, Player player)
    {
        this.ui = ui;
        this.game = game;
        this.player = player;
        Layout = GuiLayout.Load(ui.Content.FileSystem, "Window/GeneralsExpPoints.wnd");
        Layout.Hidden = true;
        foreach (var (rank, count) in new[] { (1, 4), (3, 15), (8, 4) })
        {
            var buttons = CommandSets.Buttons(game.Catalog, player.Faction?.GetString($"PurchaseScienceCommandSetRank{rank}") ?? "");
            for (int i = 0; i < count; i++)
            {
                if (Layout.Find<GuiPushButton>($"ButtonRank{rank}Number{i}") is not { } window)
                    continue;
                var button = buttons.FirstOrDefault(b => b.Slot == i + 1).Button;
                int index = slots.Count;
                slots.Add((window, button));
                window.CustomDraw = (w, context) => DrawSlot(index, w, context);
                window.Clicked += _ => Purchase(index);
            }
        }
        if (Layout.Find<GuiPushButton>("ButtonExit") is { } exit)
            exit.Clicked += _ => Close();
    }

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public void Open()
    {
        Layout.Hidden = false;
        pointsSeen = player.SciencePurchasePoints;
        Update();
    }

    public void Close() => Layout.Hidden = true;

    static string[] Sciences(CommandButton button) =>
        RuleValue.Words(button.Science).Where(s => !s.Equals("None", StringComparison.OrdinalIgnoreCase)).ToArray();

    bool CanBuy(string science) =>
        !player.HasScience(science) && player.HasPrereqsForScience(science) && player.SciencePurchaseCost(science) <= player.SciencePurchasePoints;

    /// <summary>The button's sciences in order: the first one not owned that can be bought (ControlBarCommandProcessing).</summary>
    void Purchase(int index)
    {
        if (slots[index].Button is not { } button)
            return;
        var science = Sciences(button).FirstOrDefault(CanBuy);
        if (science != null)
            game.Commands.Enqueue(new PurchaseScienceCommand(player.Index, science));
    }

    public void Update()
    {
        Tooltip = null;
        if (!IsOpen)
            return;
        pointsSeen = Math.Max(pointsSeen, player.SciencePurchasePoints);
        foreach (var (window, button) in slots)
        {
            string first = button != null ? Sciences(button).FirstOrDefault() ?? "" : "";
            bool hidden = button == null || button.HasOption("SCRIPT_ONLY") || first.Length == 0
                || player.IsScienceHidden(first) || !player.HasRootPrereqsForScience(first);
            window.Hidden = hidden;
            window.Enabled = !hidden && !player.IsScienceDisabled(first) && Sciences(button!).Any(CanBuy);
            if (!hidden && window.Hilited)
                Tooltip = TooltipFor(button!, first);
        }

        if (Layout.Find<GuiStaticText>("StaticTextRankPointsAvailable") is { } points)
            points.Text = player.SciencePurchasePoints.ToString();
        if (Layout.Find<GuiStaticText>("StaticTextTitle") is { } title)
            title.Text = ui.Strings.Get($"SCIENCE:Rank{player.RankLevel}");
        if (Layout.Find<GuiProgressBar>("ProgressBarExperience") is { } bar)
        {
            int down = game.Ranks.SkillPointsForRank(player.RankLevel);
            int up = game.Ranks.SkillPointsForRank(player.RankLevel + 1);
            bar.Progress = up == int.MaxValue || up <= down ? 1f : Math.Clamp((player.SkillPoints - down) / (float)(up - down), 0, 1);
        }
    }

    string TooltipFor(CommandButton button, string science)
    {
        string title = ui.Strings.Get(button.TextLabel).Replace("&", "");
        string description = button.DescriptLabel.Length > 0 ? ui.Strings.Get(button.DescriptLabel) : "";
        int cost = player.SciencePurchaseCost(science);
        string costText = player.HasScience(science) ? "" : $"\n{cost} {(cost == 1 ? "point" : "points")}";
        return $"{title}{costText}\n{description}";
    }

    void DrawSlot(int index, GuiWindow window, GuiContext context)
    {
        if (slots[index].Button is not { } button)
            return;
        // Owned promotions keep their colour (WIN_STATUS_ALWAYS_COLOR); ones out of reach are greyed.
        bool owned = Sciences(button).FirstOrDefault() is { } first && player.HasScience(first);
        var tint = window.IsEnabled || owned ? Vector4.One : new Vector4(0.4f, 0.4f, 0.4f, 1);
        ui.Images.Draw(context.Batch, button.ButtonImage, window.ScreenRect, tint);
        if (window.Hilited && window.IsEnabled)
            context.Batch.DrawRect(window.ScreenRect, Vector4.One, 2);
    }
}
