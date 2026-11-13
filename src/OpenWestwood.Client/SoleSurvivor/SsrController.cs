using System.Numerics;
using OpenWestwood.Client.InGame;
using OpenWestwood.Client.UI;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.SoleSurvivor;
using Silk.NET.Input;

namespace OpenWestwood.Client.SoleSurvivor;

/// <summary>
/// Sole Survivor Remix in a game screen: the person drives their unit tank-style (W / S forward and back, A / D turn; or the
/// arrow keys), aims with the mouse and fires with Space (primary weapon) and H (secondary); the camera rides on the unit.
/// Before each life the choice panel opens: the match's unit type (and aircraft, allowed any time except in an infantry
/// match) and the unit, the dearer
/// ones bought with credits once, and the weapon / armour / speed upgrades. B opens it any time, to shop or to switch to
/// another unit where the current one stands. The HUD shows the credits, the unit's health, the score, a minimap and the
/// mode's zones or flags; Tab the scoreboard.
/// </summary>
public sealed class SsrController
{
    readonly GameScreen screen;
    readonly SoleSurvivorMatch match;
    readonly SsrPlayer me;
    readonly SsrProfile profile = SsrProfile.Current;
    readonly Action onEnd;
    readonly UiResources ui;
    readonly Logic.Multiplayer.MapMetaData? mapInfo;
    readonly Graphics.Texture? mapPicture;

    bool pickerOpen = true;
    bool scoreboard;
    SsrCategory category = SsrCategory.Vehicle;
    int scroll;
    string? lastUnit;
    string? notice;
    float noticeTime;
    float endTimer = -1;
    (float X, float Y, bool Fire1, bool Fire2, Vector3 Aim, uint Target) lastSent;
    int sentFrame = -100;

    public SsrController(GameScreen screen, SoleSurvivorMatch match, SsrPlayer me, Action onEnd,
        Logic.Multiplayer.MapMetaData? mapInfo = null, Graphics.Texture? mapPicture = null)
    {
        this.screen = screen;
        this.match = match;
        this.me = me;
        this.onEnd = onEnd;
        this.mapInfo = mapInfo;
        this.mapPicture = mapPicture;
        ui = screen.Ui;
        screen.DirectControl = match.Settings.Mode != SsrMode.Classic;
        screen.KeyFilter = OnKey;
        screen.MouseFilter = OnMouse;
        screen.DrawOverlay = Draw;
        screen.Tick = Tick;
        match.CreditsEarned += (player, credits) =>
        {
            if (player != me.Player)
                return;
            profile.Credits += credits;
            Say($"+{credits} credits");
        };
        // Start on the match's unit type (or the first allowed kind the faction has).
        category = match.Settings.UnitType ?? SsrCategory.Vehicle;
        if (me.Faction is { } faction && faction.Units.Count > 0 && Shown.Count == 0
            && faction.Units.FirstOrDefault(match.Settings.Allowed) is { } first)
            category = first.Category;
        if (me.Player.StartPosition is { } start)
            screen.LookAt(start);
    }

    void Say(string text)
    {
        notice = text;
        noticeTime = 2.5f;
    }

    GameObject? MyUnit => match.UnitOf(me);

    /// <summary>Debug: a unit to go in as at once.</summary>
    public string? AutoUnit { get; set; }

    // ---- Input ----

    static readonly Key[] DriveKeys = [Key.W, Key.A, Key.S, Key.D, Key.Up, Key.Down, Key.Left, Key.Right, Key.Space, Key.H];

    bool OnKey(Key key)
    {
        switch (key)
        {
            case Key.Tab:
                scoreboard = !scoreboard;
                return true;
            case Key.B:
                pickerOpen = !pickerOpen || MyUnit == null;
                return true;
            case Key.Enter when pickerOpen && lastUnit != null:
                TrySpawn(lastUnit);
                return true;
        }
        return DriveKeys.Contains(key);
    }

    bool OnMouse(MouseButton button, bool down)
    {
        if (!pickerOpen)
            return false;
        if (down && button == MouseButton.Left)
            PickerClick(screen.MousePosition);
        return PanelRect.Contains(screen.MousePosition) || MyUnit == null;
    }

    void Tick(float seconds)
    {
        var game = screen.Game;
        if (noticeTime > 0)
            noticeTime -= seconds;
        if (AutoUnit != null && MyUnit == null && match.CanSpawn(me))
            TrySpawn(AutoUnit);
        if (MyUnit == null && !me.Out)
            pickerOpen = true;
        if (MyUnit is { } unit)
        {
            // Tank-style, whichever way the unit faces: W forward, S back, A turn left, D turn right.
            float turn = 0, throttle = 0;
            if (screen.IsHeld(Key.W) || screen.IsHeld(Key.Up)) throttle += 1;
            if (screen.IsHeld(Key.S) || screen.IsHeld(Key.Down)) throttle -= 1;
            if (screen.IsHeld(Key.A) || screen.IsHeld(Key.Left)) turn += 1;
            if (screen.IsHeld(Key.D) || screen.IsHeld(Key.Right)) turn -= 1;
            var move = new Vector2(turn, throttle);
            bool overPanel = pickerOpen && PanelRect.Contains(screen.MousePosition);
            var aim = screen.GroundUnderMouse() ?? unit.Position;
            uint target = screen.ObjectUnderMouse(o => !o.IsEffectivelyDead && o.Owner != me.Player && !o.Owner.IsNeutral)?.Id ?? 0;
            bool fire1 = !overPanel && screen.IsHeld(Key.Space), fire2 = !overPanel && screen.IsHeld(Key.H);
            var input = (move.X, move.Y, fire1, fire2, aim, target);
            // (Sent when it changes, and now and then for the aim.)
            if (input.Item1 != lastSent.X || input.Item2 != lastSent.Y || fire1 != lastSent.Fire1 || fire2 != lastSent.Fire2 || target != lastSent.Target
                || game.Frame - sentFrame >= 4 && Vector3.DistanceSquared(aim, lastSent.Aim) > 4)
            {
                game.Commands.Enqueue(new SsrInputCommand(me.Player.Index, move.X, move.Y, aim, target, fire1, fire2));
                lastSent = (move.X, move.Y, fire1, fire2, aim, target);
                sentFrame = game.Frame;
            }
            if (screen.DirectControl || move.LengthSquared() > 0)
                // Locked on the unit (easing after a point that only moves on logic frames made it judder and blur).
                screen.FollowCamera(unit.Position, 1);
        }
        // The match is won: a moment for the banner, then back to the menus.
        if (match.WinningTeam != null && endTimer < 0)
        {
            endTimer = 6;
            profile.Save();
        }
        if (endTimer > 0)
        {
            endTimer -= seconds;
            if (endTimer <= 0)
            {
                profile.Save();
                onEnd();
            }
        }
    }

    void TrySpawn(string template)
    {
        if (!match.CanSpawn(me))
            return;
        me.WeaponLevel = profile.WeaponLevel;
        me.ArmorLevel = profile.ArmorLevel;
        me.SpeedLevel = profile.SpeedLevel;
        if (match.Spawn(me, template) is { } unit)
        {
            lastUnit = template;
            pickerOpen = false;
            screen.LookAt(unit.Position);
        }
    }

    /// <summary>Changes to another unit where the current one stands (with the upgrades bought since).</summary>
    void TrySwitch(SsrUnit unit)
    {
        if (unit.Template == me.Unit && MyUnit != null)
        {
            pickerOpen = false;
            return;
        }
        me.WeaponLevel = profile.WeaponLevel;
        me.ArmorLevel = profile.ArmorLevel;
        me.SpeedLevel = profile.SpeedLevel;
        if (match.Switch(me, unit.Template) is { } now)
        {
            lastUnit = unit.Template;
            pickerOpen = false;
            screen.LookAt(now.Position);
            Say($"Now: {ui.Strings.Get(unit.DisplayName)}");
        }
    }

    // ---- The choice panel ----

    Vector2 Size => screen.ScreenSize;
    float Scale => Math.Max(0.75f, Size.Y / 720f);
    Rect PanelRect => new(Size.X / 2 - 330 * Scale, Size.Y / 2 - 250 * Scale, 660 * Scale, 500 * Scale);

    const int GridColumns = 6, GridRows = 4;

    Rect CategoryTab(int i) => new(PanelRect.X + (16 + i * 120) * Scale, PanelRect.Y + 46 * Scale, 112 * Scale, 26 * Scale);
    Rect CameoSlot(int i) => new(PanelRect.X + (16 + i % GridColumns * 105) * Scale, PanelRect.Y + (84 + i / GridColumns * 82) * Scale, 96 * Scale, 72 * Scale);
    Rect UpgradeButton(int i) => new(PanelRect.X + (16 + i * 210) * Scale, PanelRect.Y + 420 * Scale, 200 * Scale, 30 * Scale);
    Rect SpawnButton => new(PanelRect.X + PanelRect.Width - 166 * Scale, PanelRect.Y + 462 * Scale, 150 * Scale, 28 * Scale);

    List<SsrUnit> Shown => me.Faction?.Units.Where(u => u.Category == category && match.Settings.Allowed(u)).ToList() ?? [];

    /// <summary>Whether this kind of unit may be played in the match (its unit type, and aircraft).</summary>
    bool KindAllowed(SsrCategory kind) => match.Settings.KindAllowed(kind);

    static readonly string[] CategoryNames = ["VEHICLES", "INFANTRY", "AIRCRAFT", "NAVAL"];
    static readonly string[] UpgradeNames = ["WEAPONS", "ARMOR", "SPEED"];

    void PickerClick(Vector2 point)
    {
        for (int i = 0; i < 4; i++)
            if (CategoryTab(i).Contains(point))
            {
                if (KindAllowed((SsrCategory)i))
                {
                    category = (SsrCategory)i;
                    scroll = 0;
                }
                else
                    Say("Not this match's unit type");
                return;
            }
        var units = Shown;
        for (int i = 0; i < GridColumns * GridRows; i++)
        {
            int index = i + scroll * GridColumns;
            if (index >= units.Count || !CameoSlot(i).Contains(point))
                continue;
            var unit = units[index];
            if (!profile.Owns(unit))
            {
                if (profile.Buy(unit))
                    Say($"Bought {ui.Strings.Get(unit.DisplayName)}");
                else
                    Say($"Needs {unit.Cost} credits");
                return;
            }
            if (MyUnit == null)
                TrySpawn(unit.Template);
            else
                TrySwitch(unit);
            return;
        }
        for (int i = 0; i < 3; i++)
            if (UpgradeButton(i).Contains(point))
            {
                if (profile.BuyUpgrade(i))
                    Say($"{UpgradeNames[i]} upgraded (from the next unit)");
                return;
            }
        if (SpawnButton.Contains(point) && lastUnit != null && MyUnit == null)
            TrySpawn(lastUnit);
        // The wheel isn't here: rows scroll with clicks at the panel's bottom edge.
        if (point.Y > CameoSlot(GridColumns * GridRows - 1).Y + CameoSlot(0).Height && point.Y < UpgradeButton(0).Y
            && units.Count > GridColumns * GridRows)
            scroll = (scroll + 1) % ((units.Count - 1) / GridColumns - GridRows + 2);
    }

    // ---- Drawing ----

    static readonly Vector4 Gold = new(1f, 0.82f, 0.35f, 1), Dim = new(0.6f, 0.6f, 0.6f, 1), Shadow = new(0, 0, 0, 1),
        PanelBack = new(0.03f, 0.04f, 0.06f, 0.88f), Frame = new(0.85f, 0.65f, 0.25f, 1);

    void Text(SpriteBatch batch, string text, float x, float y, float size, Vector4 color, bool center = false)
    {
        var font = ui.Fonts.Get("Arial", Math.Max(7, size * Scale), bold: true);
        float w = center ? font.Measure(text) : 0;
        font.DrawWithShadow(batch, text, new Vector2(MathF.Round(x - w / 2), MathF.Round(y)), color, Shadow);
    }

    void Draw(SpriteBatch batch)
    {
        DrawWorldMarks(batch);
        DrawHud(batch);
        DrawMinimap(batch);
        if (scoreboard)
            DrawScoreboard(batch);
        if (pickerOpen)
            DrawPicker(batch);
        if (noticeTime > 0 && notice != null)
            Text(batch, notice, Size.X / 2, Size.Y * 0.18f, 14, Gold, center: true);
        if (match.WinningTeam is { } winner)
        {
            // (Freeplay: each player is their own side.)
            bool won = winner == me.Team;
            string loser = match.Settings.Teams ? $"TEAM {winner + 1} WINS"
                : match.Players.FirstOrDefault(p => p.Team == winner) is { } best ? $"{best.Player.DisplayName.ToUpperInvariant()} WINS" : "DEFEATED";
            Text(batch, won ? "VICTORY" : loser, Size.X / 2, Size.Y * 0.4f, 34, Gold, center: true);
        }
    }

    void DrawHud(SpriteBatch batch)
    {
        float s = Scale, x = 12 * s, y = 10 * s;
        batch.FillRect(new Rect(x - 6 * s, y - 4 * s, 250 * s, 112 * s), new Vector4(0, 0, 0, 0.55f));
        Text(batch, $"CREDITS  {profile.Credits}", x, y, 13, Gold);
        if (MyUnit is { } unit)
        {
            Text(batch, ui.Strings.Get(unit.Template.DisplayName), x, y + 20 * s, 11, Vector4.One);
            float health = unit.MaxHealth > 0 ? Math.Clamp(unit.Health / unit.MaxHealth, 0, 1) : 0;
            var bar = new Rect(x, y + 38 * s, 220 * s, 8 * s);
            batch.FillRect(bar, new Vector4(0.15f, 0.15f, 0.15f, 1));
            batch.FillRect(bar with { Width = bar.Width * health }, health > 0.5f ? new Vector4(0.2f, 0.85f, 0.2f, 1) : health > 0.25f ? new Vector4(0.95f, 0.8f, 0.1f, 1) : new Vector4(0.9f, 0.15f, 0.1f, 1));
        }
        else
            Text(batch, me.Out ? "OUT OF LIVES" : $"RESPAWN {(match.RespawnWait(me) > 0 ? $"IN {match.RespawnWait(me):0.0}s" : "READY")}", x, y + 20 * s, 11, Dim);
        Text(batch, $"WPN {profile.WeaponLevel}  ARM {profile.ArmorLevel}  SPD {profile.SpeedLevel}", x, y + 52 * s, 10, Dim);
        string lives = match.Settings.Lives > 0 ? $"   LIVES {Math.Max(0, match.Settings.Lives - me.Deaths)}" : "";
        Text(batch, $"KILLS {me.Kills}  DEATHS {me.Deaths}{lives}", x, y + 68 * s, 10, Dim);
        Text(batch, "WASD drive  SPACE/H fire  B units  TAB scores", x, y + 86 * s, 9, Dim);
        // The mode's score, top middle.
        string score = match.Settings.Mode switch
        {
            SsrMode.ZoneDefense => $"ZONE DEFENSE   TEAM 1: {match.TeamScores[0]} / {match.Settings.ZoneScoreLimit}    TEAM 2: {match.TeamScores[1]}",
            SsrMode.CaptureTheFlag => $"CAPTURE THE FLAG   TEAM 1: {match.TeamScores[0]}    TEAM 2: {match.TeamScores[1]}   (to {match.Settings.FlagCaptureLimit})",
            SsrMode.Classic => "CLASSIC",
            SsrMode.TeamBattle => $"TEAM BATTLE   TEAM 1: {match.KillsOf(0)}    TEAM 2: {match.KillsOf(1)}" + (match.Settings.KillLimit > 0 ? $"   (to {match.Settings.KillLimit})" : ""),
            _ => "FREEPLAY" + (match.Settings.KillLimit > 0 ? $"   FIRST TO {match.Settings.KillLimit} KILLS" : ""),
        };
        if (match.SecondsLeft is { } left)
            score += $"   {(int)left / 60}:{(int)left % 60:00}";
        Text(batch, score, Size.X / 2, 8 * s, 13, Gold, center: true);
    }

    void DrawWorldMarks(SpriteBatch batch)
    {
        // Zone Defense: each zone's ring (blue: the team's own to hold; red: the other team's, to stand in). Capture the
        // Flag: where each flag is.
        foreach (var (team, c) in match.Zones)
        {
            var color = team == me.Team ? new Vector4(0.3f, 0.6f, 1, 0.9f) : new Vector4(1, 0.3f, 0.25f, 0.9f);
            Vector2? previous = null;
            for (int i = 0; i <= 48; i++)
            {
                float a = i * MathF.Tau / 48;
                var p = c + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * match.Settings.ZoneRadius;
                var at = screen.WorldToScreen(p with { Z = screen.Game.Terrain.GroundHeight(p) + 2 });
                if (previous is { } from && at is { } to)
                    batch.DrawLine(from, to, color, 2);
                previous = at;
            }
        }
        foreach (var flag in match.Flags)
            if (screen.WorldToScreen(flag.Position with { Z = flag.Position.Z + 30 }) is { } at)
            {
                var color = flag.Team == me.Team ? new Vector4(0.3f, 0.6f, 1, 1) : new Vector4(1, 0.3f, 0.25f, 1);
                batch.FillTriangle(at, at + new Vector2(14, 5), at + new Vector2(0, 10), color);
                batch.DrawLine(at, at + new Vector2(0, 26), color, 2);
                Text(batch, flag.Team == me.Team ? "YOUR FLAG" : "ENEMY FLAG", at.X, at.Y - 16, 9, color, center: true);
            }
    }

    // ---- The minimap ----

    Rect MinimapRect => new(Size.X - 232 * Scale, Size.Y - 232 * Scale, 220 * Scale, 220 * Scale);

    /// <summary>The map (its picture), the zones and flags, everyone's unit (yours gold, your team's blue, the rest red) and
    /// the view.</summary>
    void DrawMinimap(SpriteBatch batch)
    {
        var r = MinimapRect;
        batch.FillRect(new Rect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6), new Vector4(0, 0, 0, 0.8f));
        var game = screen.Game;
        var meta = mapInfo ?? new Logic.Multiplayer.MapMetaData
        {
            FileName = "",
            ExtentMin = Vector3.Zero,
            ExtentMax = new Vector3(game.Terrain.WorldWidth, game.Terrain.WorldHeight, 0),
        };
        var (ul, lr) = Shell.MapPreviewDrawer.FindDrawPositions(r, meta);
        var inner = new Rect(ul.X, ul.Y, lr.X - ul.X, lr.Y - ul.Y);
        if (mapPicture != null)
            batch.DrawTexture(mapPicture, inner);
        else
            batch.FillRect(inner, new Vector4(0.18f, 0.2f, 0.16f, 1));
        Vector2 At(Vector3 world) => Shell.MapPreviewDrawer.MapToScreen(r, meta, world);
        float perWorld = (lr.X - ul.X) / Math.Max(1, meta.ExtentMax.X - meta.ExtentMin.X);
        foreach (var (team, c) in match.Zones)
        {
            var color = team == me.Team ? new Vector4(0.3f, 0.6f, 1, 0.9f) : new Vector4(1, 0.3f, 0.25f, 0.9f);
            var centre = At(c);
            float radius = match.Settings.ZoneRadius * perWorld;
            Vector2? previous = null;
            for (int i = 0; i <= 24; i++)
            {
                float a = i * MathF.Tau / 24;
                var p = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
                if (previous is { } from)
                    batch.DrawLine(from, p, color, 1);
                previous = p;
            }
        }
        foreach (var flag in match.Flags)
        {
            var p = At(flag.Position);
            var color = flag.Team == me.Team ? new Vector4(0.3f, 0.6f, 1, 1) : new Vector4(1, 0.3f, 0.25f, 1);
            batch.FillTriangle(p + new Vector2(0, -8), p + new Vector2(7, -5), p + new Vector2(0, -2), color);
            batch.DrawLine(p + new Vector2(0, -8), p, color, 1);
        }
        bool teams = match.Settings.Teams;
        foreach (var p in match.Players)
        {
            if (match.UnitOf(p) is not { } unit)
                continue;
            var at = At(unit.Position);
            bool mine = p == me, friend = teams && p.Team == me.Team;
            var color = mine ? Gold : friend ? new Vector4(0.35f, 0.65f, 1, 1) : new Vector4(1, 0.25f, 0.2f, 1);
            float size = (mine ? 6 : 4) * Scale;
            batch.FillRect(new Rect(at.X - size / 2, at.Y - size / 2, size, size), color);
        }
        // Classic: the bases too (their buildings, small and dim).
        if (match.Settings.Mode == SsrMode.Classic)
            foreach (var o in game.Objects)
                if (!o.IsEffectivelyDead && o.IsKindOf(Logic.Core.Kind.Structure) && !o.Owner.IsNeutral)
                {
                    var at = At(o.Position);
                    var color = o.Owner == me.Player ? new Vector4(0.9f, 0.75f, 0.3f, 0.7f) : new Vector4(0.8f, 0.25f, 0.2f, 0.7f);
                    batch.FillRect(new Rect(at.X - 2, at.Y - 2, 4, 4), color);
                }
        batch.DrawRect(inner, Frame);
    }

    void DrawScoreboard(SpriteBatch batch)
    {
        float s = Scale;
        var r = new Rect(Size.X / 2 - 220 * s, 60 * s, 440 * s, (40 + match.Players.Count * 20) * s);
        batch.FillRect(r, PanelBack);
        batch.DrawRect(r, Frame);
        Text(batch, "PLAYER", r.X + 12 * s, r.Y + 8 * s, 11, Gold);
        Text(batch, "KILLS", r.X + 270 * s, r.Y + 8 * s, 11, Gold);
        Text(batch, "DEATHS", r.X + 340 * s, r.Y + 8 * s, 11, Gold);
        int row = 0;
        foreach (var p in match.Players.OrderByDescending(p => p.Kills))
        {
            float y = r.Y + (30 + row++ * 20) * s;
            var color = p == me ? Gold : Vector4.One;
            string team = match.Settings.Teams ? $" (team {p.Team + 1})" : "";
            Text(batch, $"{p.Player.DisplayName}{team}", r.X + 12 * s, y, 10, color);
            Text(batch, p.Kills.ToString(), r.X + 270 * s, y, 10, color);
            Text(batch, p.Deaths.ToString(), r.X + 340 * s, y, 10, color);
        }
    }

    void DrawPicker(SpriteBatch batch)
    {
        float s = Scale;
        var r = PanelRect;
        batch.FillRect(r, PanelBack);
        batch.DrawRect(r, Frame, 2);
        string title = me.Faction is { } f ? $"{f.Game.ToUpperInvariant()}  -  {ui.Strings.Get(f.DisplayName).ToUpperInvariant()}" : "CHOOSE YOUR UNIT";
        Text(batch, title, r.X + 16 * s, r.Y + 12 * s, 15, Gold);
        Text(batch, $"CREDITS {profile.Credits}", r.X + r.Width - 16 * s - ui.Fonts.Get("Arial", 13 * s, true).Measure($"CREDITS {profile.Credits}"), r.Y + 12 * s, 13, Gold);
        var mouse = screen.MousePosition;
        for (int i = 0; i < 4; i++)
        {
            var tab = CategoryTab(i);
            bool any = KindAllowed((SsrCategory)i) && me.Faction?.Units.Any(u => u.Category == (SsrCategory)i) == true;
            batch.FillRect(tab, (int)category == i ? new Vector4(0.45f, 0.32f, 0.08f, 1) : new Vector4(0.12f, 0.12f, 0.14f, 1));
            batch.DrawRect(tab, Frame);
            Text(batch, CategoryNames[i], tab.X + tab.Width / 2, tab.Y + 5 * s, 11, any ? Vector4.One : Dim, center: true);
        }
        var units = Shown;
        if (units.Count == 0)
            Text(batch, "None of these in this faction.", r.X + r.Width / 2, r.Y + 200 * s, 12, Dim, center: true);
        for (int i = 0; i < GridColumns * GridRows; i++)
        {
            int index = i + scroll * GridColumns;
            if (index >= units.Count)
                break;
            var unit = units[index];
            var slot = CameoSlot(i);
            bool owned = profile.Owns(unit);
            var picture = new Rect(slot.X, slot.Y, slot.Width, slot.Height - 16 * s);
            if (ui.Images.Find(unit.ButtonImage) is { } image)
                ui.Images.Draw(batch, image, picture, owned ? Vector4.One : new Vector4(0.35f, 0.35f, 0.35f, 1));
            else
                batch.FillRect(picture, new Vector4(0.2f, 0.2f, 0.2f, 1));
            batch.DrawRect(slot, unit.Template == lastUnit ? Gold : slot.Contains(mouse) ? Vector4.One : new Vector4(0.4f, 0.4f, 0.4f, 1));
            Text(batch, owned ? Short(ui.Strings.Get(unit.DisplayName)) : $"BUY {unit.Cost}", slot.X + slot.Width / 2, slot.Y + slot.Height - 14 * s, 8,
                owned ? Vector4.One : profile.Credits >= unit.Cost ? Gold : new Vector4(0.9f, 0.3f, 0.25f, 1), center: true);
        }
        if (units.Count > GridColumns * GridRows)
            Text(batch, "(click here for more)", r.X + r.Width / 2, CameoSlot(GridColumns * GridRows - 1).Y + CameoSlot(0).Height + 2 * s, 9, Dim, center: true);
        int[] levels = [profile.WeaponLevel, profile.ArmorLevel, profile.SpeedLevel];
        for (int i = 0; i < 3; i++)
        {
            var b = UpgradeButton(i);
            bool maxed = levels[i] >= SsrProfile.MaxUpgradeLevel;
            batch.FillRect(b, new Vector4(0.1f, 0.12f, 0.16f, 1));
            batch.DrawRect(b, b.Contains(mouse) ? Gold : Frame);
            Text(batch, maxed ? $"{UpgradeNames[i]} {levels[i]} (MAX)" : $"{UpgradeNames[i]} {levels[i]} -> {levels[i] + 1}: {SsrProfile.UpgradeCost(levels[i])}",
                b.X + b.Width / 2, b.Y + 8 * s, 10, maxed ? Dim : profile.Credits >= SsrProfile.UpgradeCost(levels[i]) ? Gold : Vector4.One, center: true);
        }
        var spawn = SpawnButton;
        bool can = MyUnit == null && lastUnit != null && match.CanSpawn(me);
        batch.FillRect(spawn, can ? new Vector4(0.15f, 0.4f, 0.15f, 1) : new Vector4(0.15f, 0.15f, 0.15f, 1));
        batch.DrawRect(spawn, Frame);
        string label = MyUnit != null ? "B: BACK TO BATTLE" : me.Out ? "OUT OF LIVES" : match.RespawnWait(me) > 0 ? $"WAIT {match.RespawnWait(me):0.0}" : lastUnit != null ? "ENTER: AGAIN" : "PICK A UNIT";
        Text(batch, label, spawn.X + spawn.Width / 2, spawn.Y + 7 * s, 10, Vector4.One, center: true);
        Text(batch, MyUnit != null ? "Click a unit to switch to it where you stand. Locked units are bought once with credits;"
            : "Click a unit to go in as it. Locked units are bought once with credits;", r.X + 16 * s, r.Y + 458 * s, 9, Dim);
        Text(batch, "upgrades count for every unit.", r.X + 16 * s, r.Y + 474 * s, 9, Dim);
    }

    static string Short(string text) => text.Length > 16 ? text[..15] + "." : text;
}
