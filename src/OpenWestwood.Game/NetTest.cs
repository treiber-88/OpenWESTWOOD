using System.Numerics;
using OpenWestwood.Client;
using OpenWestwood.Client.Network;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Multiplayer;

namespace OpenWestwood.Game;

/// <summary>
/// A network game without a window, to test multiplayer (two or more copies of the game, on one PC or several):
///
///   OpenWESTWOOD --net-test host [--players 2] [--faction FactionAmerica] [--ai "FactionKWScrin,FactionTDNod"]
///                [--map "Maps/..."] [--minutes 3] [--port 8087]
///   OpenWESTWOOD --net-test join [--address 127.0.0.1] [--faction FactionGLA] [--minutes 3]
///   (both: [--net-delay ms] to make every message that much late, as on a slow internet link)
///
/// The host waits for the players, sets the game up, and starts it once all have accepted. Every machine then plays it
/// in real time (30 frames a second), giving its own units random orders (moves, attack-moves, units to build), and
/// at the end prints how many frames ran, how long it waited for the network, whether the games stayed in step, and
/// the game's checksum at the last frame (the same on every machine if they did).
/// </summary>
static class NetTest
{
    public static int Run(GameContent content, CommandLine options)
    {
        string mode = options.Get("net-test") ?? "host";
        bool host = mode == "host";
        string name = options.Get("name") ?? (host ? "Host" : "Guest" + Environment.ProcessId % 100);
        int port = int.TryParse(options.Get("port"), out int p) ? p : NetHost.DefaultPort;
        float minutes = float.TryParse(options.Get("minutes"), System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : 3;
        if (int.TryParse(options.Get("net-delay"), out int delay))
            NetConnection.SimulatedDelayMs = delay;
        string fingerprint = NetHost.Fingerprint(content);
        var maps = Client.Shell.ShellScreen.BuildMapCache(content);
        var templates = GameInfo.PlayerTemplates(content.Rules);
        int Template(string faction) => templates.FindIndex(t => t.Name.Equals(faction, StringComparison.OrdinalIgnoreCase));
        void Log(string text) => Console.WriteLine($"[net {name}] {text}");

        using var session = host
            ? NetSession.HostGame("Test game", name, port, lan: true, fingerprint, _ => null, (info, lobby) => NetGame.ResolveRandoms(content, maps, info, lobby),
                options.Get("password") ?? "", openRouter: options.Has("internet"))
            : NetSession.Join(options.Get("address") ?? "127.0.0.1", port, name, fingerprint, options.Get("password") ?? "");
        session.Notice += text => Log("notice: " + text);
        int notesShown = 0;
        GameInfo? started = null;
        session.Started += info => started = info;

        // ---- The lobby ----
        int myTemplate = Template(options.Get("faction") ?? (host ? "FactionAmerica" : "FactionGLA"));
        int wantPlayers = int.TryParse(options.Get("players"), out int n) ? n : 2;
        var ais = (options.Get("ai") ?? "FactionKWScrin,FactionTDNod").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool optionsSent = false;
        int lastAcceptRequest = -1;
        while (started == null)
        {
            session.Poll();
            for (; notesShown < session.Chat.Count; notesShown++)
                Log("chat: " + session.Chat[notesShown]);
            if (session.Error != null || !session.IsConnected)
            {
                Log($"could not take part: {session.Error ?? session.CloseReason}");
                return 1;
            }
            if (watch.Elapsed.TotalSeconds > 60)
            {
                Log("no game started within a minute");
                return 1;
            }
            var lobby = session.Lobby;
            if (lobby == null || session.ClientId < 0)
            {
                Thread.Sleep(20);
                continue;
            }
            int mySlot = lobby.SlotOf(session.ClientId);
            if (host)
            {
                int humans = lobby.Info.Slots.Count(s => s.IsHuman);
                if (!optionsSent && humans >= wantPlayers)
                {
                    // The map, the computer players, and the host's own faction.
                    var edit = NetLobby.ReadInfo(NetConnection.Unpack(NetConnection.Pack(w => NetLobby.WriteInfo(w, lobby.Info))));
                    edit.Map = options.Get("map") ?? "Maps/Tournament Desert/Tournament Desert.map";
                    if (maps.Find(edit.Map) is { } meta && meta.NumPlayers < humans + ais.Length)
                        ais = ais.Take(Math.Max(0, meta.NumPlayers - humans)).ToArray();
                    int slot = 0;
                    foreach (string ai in ais)
                    {
                        while (slot < GameInfo.MaxSlots && edit.Slots[slot].IsHuman)
                            slot++;
                        if (slot >= GameInfo.MaxSlots)
                            break;
                        edit.Slots[slot].SetState(SlotState.MedAI, $"Computer {slot}");
                        edit.Slots[slot].PlayerTemplate = Template(ai);
                        slot++;
                    }
                    session.SendHostOptions(edit);
                    session.RequestSlot(-1, myTemplate, -1, -1, accept: true);
                    optionsSent = true;
                    Log($"{humans} players in; map {edit.Map}, computer players {string.Join(", ", ais)}");
                }
                if (optionsSent && humans >= wantPlayers && Enumerable.Range(0, GameInfo.MaxSlots).All(i => lobby.SlotClient[i] < 0 || lobby.Accepted[i])
                    && lobby.Info.Slots.Count(s => s.IsAI) == ais.Length && watch.ElapsedMilliseconds > 500)
                {
                    session.RequestStart();
                    watch.Restart();
                    while (started == null && watch.Elapsed.TotalSeconds < 10)
                    {
                        session.Poll();
                        Thread.Sleep(10);
                    }
                    if (started == null)
                        Log("the start did not come back; trying again");
                }
            }
            else if (mySlot >= 0 && !lobby.Accepted[mySlot] && lastAcceptRequest != lobby.GetHashCode())
            {
                lastAcceptRequest = lobby.GetHashCode();
                session.RequestSlot(-1, myTemplate, -1, -1, accept: true);
            }
            Thread.Sleep(20);
        }

        // ---- The game ----
        var settings = started;
        Log($"start: {string.Join(", ", settings.Slots.Where(s => s.IsOccupied).Select(s => $"{s.Name} = {templates[s.PlayerTemplate].Name} at {s.StartPos}"))}; seed {settings.Seed}");
        var (game, _, local, humanPlayers) = NetGame.Build(content, maps, settings, session.Lobby!, session.ClientId);
        session.Attach(game, local, humanPlayers);
        var lockstep = session.Lockstep!;
        bool desynced = false;
        lockstep.Desynced += (frame, players) => { desynced = true; Log($"DESYNC at frame {frame} with players {string.Join(",", players)}"); };
        var random = new Random(Environment.ProcessId);
        int endFrame = (int)(minutes * 60 * LogicTime.FramesPerSecond);
        double stallSeconds = 0, longestStall = 0, currentStall = 0;
        int orders = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0, owed = 0;
        int nextReport = LogicTime.FramesPerSecond * 30;
        while (game.Frame < endFrame)
        {
            double now = clock.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            owed = Math.Min(owed + dt, 0.25);
            session.Poll();
            if (!session.IsConnected)
            {
                Log($"lost the link: {session.CloseReason}");
                break;
            }
            bool stalled = false;
            int ran = 0;
            while (owed >= LogicTime.SecondsPerFrame && ran++ < 5 && game.Frame < endFrame)
            {
                if (!lockstep.CanStep)
                {
                    stalled = true;
                    break;
                }
                lockstep.BeforeStep();
                game.Update();
                lockstep.AfterStep();
                owed -= LogicTime.SecondsPerFrame;
                if (game.Frame % 45 == 0)
                    orders += GiveRandomOrders(game, local, random);
                if (game.Frame >= nextReport)
                {
                    nextReport += LogicTime.FramesPerSecond * 30;
                    Log($"frame {game.Frame}: latency {lockstep.Latency} frames, waited {stallSeconds:F2} s so far, {game.ObjectsOwnedBy(local).Count()} objects of mine");
                }
            }
            if (stalled)
            {
                currentStall += dt;
                stallSeconds += dt;
                longestStall = Math.Max(longestStall, currentStall);
            }
            else
                currentStall = 0;
            Thread.Sleep(1);
        }
        // The others may still need this machine's last orders: keep answering a moment.
        var linger = System.Diagnostics.Stopwatch.StartNew();
        while (linger.ElapsedMilliseconds < 2000)
        {
            session.Poll();
            Thread.Sleep(5);
        }
        Log($"done: {game.Frame} frames in {clock.Elapsed.TotalSeconds:F1} s (real time {endFrame / (double)LogicTime.FramesPerSecond:F1} s), "
            + $"waited for the network {stallSeconds:F2} s in all, longest {longestStall * 1000:F0} ms, latency {lockstep.Latency} frames, "
            + $"{orders} orders given, in step: {(desynced ? "NO" : "yes")}");
        Log($"CHECKSUM frame {game.Frame} = {game.ComputeChecksum():X8}");
        return desynced ? 2 : 0;
    }

    /// <summary>A few random orders for this player's units: moves and attack-moves, and units to build.</summary>
    static int GiveRandomOrders(GameLogic game, Logic.Players.Player me, Random random)
    {
        int given = 0;
        var mine = game.ObjectsOwnedBy(me).Where(o => !o.IsEffectivelyDead).ToList();
        var mobile = mine.Where(o => o.IsMobile && o.AI != null && !o.IsKindOf(Kind.Harvester) && !o.IsKindOf("DOZER")).ToList();
        if (mobile.Count > 0)
        {
            var units = mobile.OrderBy(_ => random.Next()).Take(random.Next(1, 6)).Select(o => o.Id).ToArray();
            var to = new Vector3(random.NextSingle() * game.Terrain.WorldWidth, random.NextSingle() * game.Terrain.WorldHeight, 0);
            game.Commands.Enqueue(new MoveCommand(me.Index, units, to, AttackMove: random.Next(2) == 0));
            given++;
        }
        foreach (var factory in mine.Where(o => !o.IsUnderConstruction && o.FindModule<Logic.Modules.Production.ProductionUpdate>() is { PlacesStructures: false } pu && pu.Queue.Count == 0))
        {
            var options = Logic.Templates.CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD").ToList();
            if (options.Count == 0)
                continue;
            game.Commands.Enqueue(new ProduceUnitCommand(me.Index, factory.Id, options[random.Next(options.Count)]));
            given++;
        }
        return given;
    }
}
