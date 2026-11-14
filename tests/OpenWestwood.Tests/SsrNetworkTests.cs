using OpenWestwood.Client.Network;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Logic.SoleSurvivor;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>
/// Sole Survivor Remix over the network: a host and a joiner (two sessions in this process, through the loopback) go
/// through the lobby as the online screen does, start, build the match each on their own, and play it in lockstep, each
/// going in as a unit and driving and firing; the two games must stay identical (checksums) all the way.
/// </summary>
public class SsrNetworkTests(ITestOutputHelper output)
{
    static Client.GameContent? content;

    void PollUntil(string what, Func<bool> done, params NetSession[] sessions)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < until)
        {
            foreach (var s in sessions)
                s.Poll();
            Thread.Sleep(5);
        }
        if (!done())
            foreach (var s in sessions)
                output.WriteLine($"{what}: client {s.ClientId} error {s.Error} lobby extra '{s.Lobby?.Info.Extra}' accepted {string.Join(",", s.Lobby?.Accepted ?? [])} slots {string.Join(",", s.Lobby?.Info.Slots.Select(x => $"{x.State}:{x.Name}:{x.PlayerTemplate}") ?? [])}");
        Assert.True(done(), "timed out waiting on the network: " + what);
    }

    [Fact]
    public void TwoPlayersStayInStep()
    {
        try { content ??= Client.GameContent.Load(); }
        catch (DirectoryNotFoundException) { return; }
        var c = content;
        if (!File.Exists(Path.Combine(c.RootFolder, "content", "zerohour", "INIZH.big"))) return;
        var maps = Client.Shell.ShellScreen.BuildMapCache(c);
        using var host = NetSession.HostGame("[SSR] Test", "Host", 0, lan: false, "test-fingerprint", _ => null, (_, _) => { });
        using var guest = NetSession.Join("127.0.0.1", host.Host!.Port, "Guest", "test-fingerprint");
        PollUntil("join", () => host.Lobby != null && guest.Lobby != null && guest.ClientId > 0 && guest.Lobby.SlotOf(guest.ClientId) >= 0, host, guest);

        // The host's settings in the lobby (as the online screen sends them), then each person's faction and readiness.
        var setup = new Client.SoleSurvivor.SsrMatchSetup { Mode = SsrMode.Freeplay, Computers = 2, UnitType = SsrCategory.Vehicle, Map = "Maps/Tournament Desert/Tournament Desert.map" };
        var info = host.Lobby!.Clone().Info;
        info.Map = setup.Map;
        info.Extra = setup.Encode();
        host.SendHostOptions(info);
        var templates = GameInfo.PlayerTemplates(c.Rules);
        int america = templates.FindIndex(t => t.Name == "FactionAmerica"), china = templates.FindIndex(t => t.Name == "FactionChina");
        PollUntil("options", () => guest.Lobby?.Info.Extra == info.Extra, host, guest);
        host.RequestSlot(-1, america, -1, -1, true);
        guest.RequestSlot(-1, china, -1, -1, false);
        PollUntil("faction", () => host.Lobby!.Info.Slots[1].PlayerTemplate == china && guest.Lobby!.Info.Slots[1].PlayerTemplate == china, host, guest);
        // (READY: a press of its own once the faction is in, as in the lobby screen.)
        guest.RequestSlot(-1, china, -1, -1, true);
        PollUntil("ready", () => host.Lobby!.Accepted.Count(a => a) == 2 && host.Lobby.Info.Slots[1].PlayerTemplate == china, host, guest);

        GameInfo? hostStart = null, guestStart = null;
        host.Started += i => hostStart = i;
        guest.Started += i => guestStart = i;
        host.RequestStart();
        PollUntil("start", () => hostStart != null && guestStart != null, host, guest);

        // Each machine builds the match from what it was sent.
        (Logic.GameLogic Game, SoleSurvivorMatch Match, SsrPlayer Me) Build(NetSession session, GameInfo started)
        {
            var s = Client.SoleSurvivor.SsrOnlineScreen.FromLobby(started, c.Rules, maps);
            var (game, _, match) = s.Build(c);
            string local = started.Slots[session.Lobby!.SlotOf(session.ClientId)].Name;
            var me = match.Players.First(p => !p.IsComputer && p.Player.DisplayName == local);
            game.Players.Local = me.Player;
            session.Attach(game, me.Player, match.Players.Where(p => !p.IsComputer).Select(p => p.Player));
            return (game, match, me);
        }
        var a = Build(host, hostStart!);
        var b = Build(guest, guestStart!);
        Assert.Equal(a.Match.Players.Count, b.Match.Players.Count);
        Assert.Equal("FactionChina", b.Me.Faction?.Name);
        output.WriteLine($"players: {string.Join(", ", a.Match.Players.Select(p => $"{p.Player.Name} {p.Faction?.Name}"))}");

        // Play: each goes in as a unit, then drives and fires now and then.
        string UnitFor(SsrPlayer p) => p.Faction!.Units.First(u => u.Category == SsrCategory.Vehicle && u.Power > 0).Template;
        a.Game.Commands.Enqueue(new SsrUnitCommand(a.Me.Player.Index, UnitFor(a.Me), 1, 0, 0));
        b.Game.Commands.Enqueue(new SsrUnitCommand(b.Me.Player.Index, UnitFor(b.Me), 0, 2, 0));
        const int Frames = 20 * LogicTime.FramesPerSecond;
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while ((a.Game.Frame < Frames || b.Game.Frame < Frames) && DateTime.UtcNow < deadline)
        {
            foreach (var (session, side) in new[] { (host, a), (guest, b) })
            {
                session.Poll();
                if (side.Game.Frame >= Frames || session.Lockstep is not { } lockstep || !lockstep.CanStep)
                    continue;
                int f = side.Game.Frame;
                if (f % 45 == 10 && side.Match.UnitOf(side.Me) is { } unit)
                    side.Game.Commands.Enqueue(new SsrInputCommand(side.Me.Player.Index, (f / 45) % 3 - 1, 1, unit.Position + new System.Numerics.Vector3(100, 50, 0), 0, f % 90 == 10, false));
                lockstep.BeforeStep();
                side.Game.Update();
                lockstep.AfterStep();
            }
        }
        output.WriteLine($"frames {a.Game.Frame} / {b.Game.Frame}; desynced {host.Lockstep!.IsDesynced} / {guest.Lockstep!.IsDesynced}");
        Assert.Equal(Frames, a.Game.Frame);
        Assert.Equal(Frames, b.Game.Frame);
        Assert.False(host.Lockstep.IsDesynced || guest.Lockstep.IsDesynced, "the two games went out of step");
        Assert.Equal(a.Game.ComputeChecksum(), b.Game.ComputeChecksum());
        // Both people's units are in, the same on both machines, with the upgrades each machine sent.
        foreach (var (pa, pb) in a.Match.Players.Zip(b.Match.Players))
            Assert.Equal(a.Match.UnitOf(pa)?.Position, b.Match.UnitOf(pb)?.Position);
        Assert.NotNull(a.Match.UnitOf(a.Me));
        Assert.Equal(2, b.Match.Players.First(p => p.Player.DisplayName == "Guest").ArmorLevel);
        Assert.Equal(1, a.Match.Players.First(p => p.Player.DisplayName == "Host").WeaponLevel);
    }
}
