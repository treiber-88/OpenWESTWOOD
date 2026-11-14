using System.Net;
using System.Net.Sockets;
using OpenWestwood.Logic.Multiplayer;

namespace OpenWestwood.Client.Network;

/// <summary>
/// The host of a network game: every machine (the host's own too, through the loopback) connects to it. It owns the
/// lobby, checks that joining machines run the same game, makes the random choices when the game starts, and passes
/// each player's orders and checksums on to the others as soon as they arrive (from the reading thread: no frame of
/// delay). It also measures each machine's round trip, so each can choose how far ahead to send its orders.
/// </summary>
public sealed class NetHost : IDisposable
{
    public const int DefaultPort = 8087;

    sealed class Client
    {
        public required NetConnection Connection;
        public string Name = "";
        public bool HasZeroHour, HasKanesWrath;
        public bool Welcomed, Loaded;
        public int PlayerIndex = -1;
        public int LastOrdersFrame = -1;
        public int RttMs = 30;
        public byte[] Challenge = [];
    }

    /// <summary>The password players must know to join ("" for none). It never travels: joiners prove they know it.</summary>
    readonly string password;

    /// <summary>Wrong passwords per internet address, to slow down guessing.</summary>
    readonly Dictionary<string, (int Count, DateTime Since)> failures = new();

    /// <summary>The router opening for internet players (null if none was made).</summary>
    RouterPort? router;

    /// <summary>Lines for the host's own lobby chat: how players reach this game (filled in from a background thread).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> HostNotes { get; } = new();

    /// <summary>The proof a joiner sends: HMAC-SHA256 of the host's random challenge, keyed by the password.</summary>
    public static byte[] Proof(string password, byte[] challenge) =>
        password.Length == 0 ? [] : System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password), challenge);

    readonly object gate = new();
    readonly TcpListener listener;
    readonly Dictionary<int, Client> clients = new();
    readonly Func<NetLobby, string?> checkStart;
    readonly Action<GameInfo, NetLobby> resolveRandoms;
    readonly Thread acceptThread, timerThread;
    readonly LanBeacon? beacon;
    int nextId;
    bool inGame, disposed;

    public NetLobby Lobby { get; } = new();
    public int Port { get; }

    /// <summary>The fingerprint a joining machine must match (protocol, orders and rules).</summary>
    public static string Fingerprint(GameContent content) =>
        $"{Protocol}/{Logic.Network.CommandCodec.Version:X8}/{RulesFingerprint(content):X8}";

    public const int Protocol = 4;

    /// <param name="checkStart">The host's own check before starting (null: fine; else the reason not to).</param>
    /// <param name="resolveRandoms">Settles every random faction, colour and start position in the settings.</param>
    public NetHost(string gameName, int port, bool lan, Func<NetLobby, string?> checkStart, Action<GameInfo, NetLobby> resolveRandoms, string fingerprint,
        string password = "", bool openRouter = false)
    {
        this.password = password;
        this.checkStart = checkStart;
        this.resolveRandoms = resolveRandoms;
        expectedFingerprint = fingerprint;
        Lobby.GameName = gameName;
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"[net] hosting \"{gameName}\" on port {Port}");
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "net host accept" };
        acceptThread.Start();
        timerThread = new Thread(TimerLoop) { IsBackground = true, Name = "net host timer" };
        timerThread.Start();
        if (lan)
            beacon = new LanBeacon(() => Describe());
        if (openRouter)
            new Thread(OpenRouter) { IsBackground = true, Name = "net host router" }.Start();
    }

    /// <summary>Opens the port on the router and tells the host what address to give the other players.</summary>
    void OpenRouter()
    {
        var opened = RouterPort.Open(Port, out string problem);
        if (disposed)
        {
            opened?.Dispose();
            return;
        }
        router = opened;
        string lockNote = password.Length > 0 ? " and the password" : "";
        if (opened?.ExternalAddress is { } address && !RouterPort.IsPrivate(address))
            HostNotes.Enqueue($"Internet players can join with Direct Connect: address {address}:{Port}{lockNote}.");
        else if (opened?.ExternalAddress is { } inner)
            HostNotes.Enqueue($"Your router opened port {Port}, but your internet provider puts your router behind its own network ({inner}), so internet players probably can't reach this game. That needs a public internet address from your provider, or a virtual LAN program.");
        else if (opened != null)
            HostNotes.Enqueue($"Your router opened port {Port}. Internet players join with Direct Connect using your internet address and port {Port}{lockNote}.");
        else
            HostNotes.Enqueue($"Internet players can't reach this game yet: {problem} Forward TCP port {Port} on your router to this computer, then give them your internet address{lockNote}.");
    }

    readonly string expectedFingerprint;

    string Describe()
    {
        lock (gate)
            return inGame ? "" : $"{Lobby.GameName}|{Lobby.Info.Map}|{Lobby.Info.NumPlayers}|{Lobby.Info.Slots.Count(s => s.State != SlotState.Closed)}|{Port}|{expectedFingerprint}|{(password.Length > 0 ? "locked" : "")}";
    }

    void AcceptLoop()
    {
        while (!disposed)
        {
            try
            {
                var tcp = listener.AcceptTcpClient();
                lock (gate)
                {
                    var client = new Client { Connection = null! };
                    int id = nextId++;
                    client.Connection = new NetConnection(tcp, (c, type, data) => OnMessage(client, type, data)) { Id = id };
                    clients[id] = client;
                    // First, a random challenge: the joiner answers with proof that it knows the password.
                    client.Challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                    client.Connection.Send(NetMessage.Challenge, w => { w.Write(client.Challenge); w.Write(password.Length > 0); });
                }
            }
            catch (Exception)
            {
                if (disposed)
                    return;
            }
        }
    }

    void TimerLoop()
    {
        int tick = 0;
        while (!disposed)
        {
            Thread.Sleep(500);
            tick++;
            lock (gate)
            {
                DropClosed();
                foreach (var c in clients.Values.Where(c => c.Welcomed))
                    c.Connection.Send(NetMessage.Ping, w => w.Write(System.Diagnostics.Stopwatch.GetTimestamp()));
                if (tick % 2 == 0)
                {
                    var table = clients.Values.Where(c => c.Welcomed).Select(c => (c.Connection.Id, c.RttMs)).ToList();
                    Broadcast(NetMessage.Latency, w =>
                    {
                        w.Write((byte)table.Count);
                        foreach (var (id, rtt) in table)
                        {
                            w.Write((byte)id);
                            w.Write((ushort)Math.Min(rtt, 60000));
                        }
                    });
                }
            }
        }
    }

    void DropClosed()
    {
        foreach (var (id, c) in clients.Where(kv => kv.Value.Connection.IsClosed).ToList())
        {
            clients.Remove(id);
            if (!c.Welcomed)
                continue;
            if (inGame)
            {
                if (c.PlayerIndex >= 0)
                    Broadcast(NetMessage.Leave, w => { w.Write((byte)c.PlayerIndex); w.Write(c.LastOrdersFrame); w.Write(c.Name); });
            }
            else
            {
                int slot = Lobby.SlotOf(id);
                if (slot >= 0)
                {
                    Lobby.Info.Slots[slot].SetState(SlotState.Open);
                    Lobby.SlotClient[slot] = -1;
                    Lobby.Accepted[slot] = false;
                }
                Broadcast(NetMessage.Chat, w => { w.Write(""); w.Write($"{c.Name} left the game."); });
                UpdateInstalls();
                SendLobby();
            }
        }
    }

    void Broadcast(NetMessage type, Action<BinaryWriter> write, Client? except = null)
    {
        var payload = NetConnection.Pack(write);
        foreach (var c in clients.Values)
            if (c != except && c.Welcomed)
                c.Connection.Send(type, payload);
    }

    void SendLobby() => Broadcast(NetMessage.Lobby, Lobby.Write);

    void UpdateInstalls()
    {
        var welcomed = clients.Values.Where(c => c.Welcomed).ToList();
        Lobby.AllHaveZeroHour = welcomed.All(c => c.HasZeroHour);
        Lobby.AllHaveKanesWrath = welcomed.All(c => c.HasKanesWrath);
    }

    void OnMessage(Client client, NetMessage type, byte[] data)
    {
        // Orders and checksums go on at once, untouched (the hot path).
        if (type is NetMessage.Orders or NetMessage.Sync)
        {
            if (type == NetMessage.Orders && data.Length >= 5)
            {
                client.LastOrdersFrame = BitConverter.ToInt32(data, 0);
                client.PlayerIndex = data[4];
            }
            lock (gate)
                foreach (var c in clients.Values)
                    if (c != client && c.Welcomed)
                        c.Connection.Send(type, data);
            return;
        }
        if (type == NetMessage.Pong)
        {
            long sent = BitConverter.ToInt64(data, 0);
            client.RttMs = (int)((System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000 / System.Diagnostics.Stopwatch.Frequency);
            return;
        }
        lock (gate)
        {
            try
            {
                Handle(client, type, NetConnection.Unpack(data));
            }
            catch (Exception e)
            {
                client.Connection.Close($"Bad message: {e.Message}");
            }
        }
    }

    void Handle(Client client, NetMessage type, BinaryReader r)
    {
        switch (type)
        {
            case NetMessage.Hello:
            {
                string fingerprint = r.ReadString();
                string name = r.ReadString().Trim();
                client.HasZeroHour = r.ReadBoolean();
                client.HasKanesWrath = r.ReadBoolean();
                if (fingerprint != expectedFingerprint)
                {
                    Reject(client, "That game runs a different version of OpenWESTWOOD (or different mods).");
                    return;
                }
                byte[] proof = r.ReadBytes(r.ReadByte());
                if (password.Length > 0 && !CheckPassword(client, proof))
                    return;
                if (inGame)
                {
                    Reject(client, "That game has already started.");
                    return;
                }
                int slot = Array.FindIndex(Lobby.Info.Slots, s => s.State == SlotState.Open);
                // The host's own machine (the first to connect) takes the first slot.
                if (client.Connection.Id == 0)
                    slot = 0;
                if (slot < 0)
                {
                    Reject(client, "That game is full.");
                    return;
                }
                if (name.Length == 0)
                    name = "Player";
                // Names must differ (each machine finds its own player by name).
                string unique = name;
                for (int n = 2; Lobby.Info.Slots.Any(s => s.IsHuman && s.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); n++)
                    unique = $"{name} {n}";
                client.Name = unique;
                client.Welcomed = true;
                Lobby.Info.Slots[slot].SetState(SlotState.Player, unique);
                Lobby.SlotClient[slot] = client.Connection.Id;
                Lobby.Accepted[slot] = client.Connection.Id == 0;
                client.Connection.Send(NetMessage.Welcome, w => w.Write((byte)client.Connection.Id));
                UpdateInstalls();
                Broadcast(NetMessage.Chat, w => { w.Write(""); w.Write($"{unique} joined the game."); }, client);
                SendLobby();
                return;
            }

            case NetMessage.LobbyRequest:
            {
                int slot = Lobby.SlotOf(client.Connection.Id);
                if (slot < 0 || inGame)
                    return;
                var s = Lobby.Info.Slots[slot];
                int color = r.ReadInt16(), template = r.ReadInt16(), team = r.ReadSByte(), start = r.ReadSByte();
                bool accept = r.ReadBoolean();
                bool changed = s.Color != color || s.PlayerTemplate != template || s.TeamNumber != team || s.StartPos != start;
                if (color < 0 || !Lobby.Info.IsColorTaken(color, slot))
                    s.Color = color;
                s.PlayerTemplate = template;
                s.TeamNumber = team;
                if (start < 0 || !Lobby.Info.Slots.Where((o, i) => i != slot && o.IsOccupied).Any(o => o.StartPos == start))
                    s.StartPos = start;
                Lobby.Accepted[slot] = client.Connection.Id == 0 || (accept && !changed);
                SendLobby();
                return;
            }

            case NetMessage.HostOptions:
            {
                if (client.Connection.Id != 0 || inGame)
                    return;
                var edited = NetLobby.ReadInfo(r);
                var info = Lobby.Info;
                bool changed = edited.Map != info.Map || edited.StartingCash != info.StartingCash || edited.SuperweaponRestriction != info.SuperweaponRestriction
                    || edited.ExploredMap != info.ExploredMap || edited.NoFogOfWar != info.NoFogOfWar || edited.GameSpeed != info.GameSpeed
                    || edited.Extra != info.Extra;
                info.Extra = edited.Extra;
                info.Map = edited.Map;
                info.StartingCash = edited.StartingCash;
                info.SuperweaponRestriction = edited.SuperweaponRestriction;
                info.ExploredMap = edited.ExploredMap;
                info.NoFogOfWar = edited.NoFogOfWar;
                info.GameSpeed = edited.GameSpeed;
                for (int i = 0; i < GameInfo.MaxSlots; i++)
                {
                    int owner = Lobby.SlotClient[i];
                    var from = edited.Slots[i];
                    var to = info.Slots[i];
                    if (owner > 0)
                    {
                        // Another player's slot: the host may only open it (which sends them away).
                        if (from.State is SlotState.Open or SlotState.Closed && clients.TryGetValue(owner, out var kicked))
                        {
                            Reject(kicked, "The host removed you from the game.");
                            to.SetState(from.State);
                            Lobby.SlotClient[i] = -1;
                            Lobby.Accepted[i] = false;
                        }
                        continue;
                    }
                    if (owner == 0 || from.State != SlotState.Player)
                    {
                        changed |= to.State != from.State || to.Color != from.Color || to.PlayerTemplate != from.PlayerTemplate || to.TeamNumber != from.TeamNumber || to.StartPos != from.StartPos;
                        if (owner != 0)
                            to.SetState(from.State, from.Name);
                        to.Color = from.Color;
                        to.PlayerTemplate = from.PlayerTemplate;
                        to.TeamNumber = from.TeamNumber;
                        to.StartPos = from.StartPos;
                        if (to.IsAI && from.Name.Length > 0)
                            to.Name = from.Name;
                    }
                }
                // Changed settings must be accepted again (as the original's host changes un-accept everyone).
                if (changed)
                    for (int i = 0; i < GameInfo.MaxSlots; i++)
                        Lobby.Accepted[i] = Lobby.SlotClient[i] == 0;
                SendLobby();
                return;
            }

            case NetMessage.Chat:
            {
                string text = r.ReadString();
                if (text.Length > 0)
                    Broadcast(NetMessage.Chat, w => { w.Write(client.Name); w.Write(text.Length > 200 ? text[..200] : text); });
                return;
            }

            case NetMessage.Start:
            {
                if (client.Connection.Id != 0 || inGame)
                    return;
                string? problem = null;
                for (int i = 0; i < GameInfo.MaxSlots; i++)
                    if (Lobby.SlotClient[i] >= 0 && !Lobby.Accepted[i])
                        problem = $"{Lobby.Info.Slots[i].Name} has not accepted the settings yet.";
                if (!Lobby.IsMapAllowed(Lobby.Info.Map))
                    problem = "Not every player has the game this map comes from.";
                problem ??= checkStart(Lobby);
                if (problem != null)
                {
                    client.Connection.Send(NetMessage.Chat, w => { w.Write(""); w.Write(problem); });
                    return;
                }
                inGame = true;
                var info = Lobby.Info;
                info.Seed = (ulong)Environment.TickCount64 ^ ((ulong)Random.Shared.Next() << 32);
                resolveRandoms(info, Lobby);
                foreach (var c in clients.Values)
                    c.Loaded = false;
                Broadcast(NetMessage.Start, w => NetLobby.WriteInfo(w, info));
                return;
            }

            case NetMessage.Loaded:
            {
                client.Loaded = true;
                if (clients.Values.Where(c => c.Welcomed).All(c => c.Loaded))
                    Broadcast(NetMessage.Go, _ => { });
                return;
            }
        }
    }

    /// <summary>
    /// Checks a joiner's password proof. The host's own machine (connected through the loopback) is always let in.
    /// After 5 wrong tries in 10 minutes from one address, that address is turned away until the 10 minutes are up.
    /// </summary>
    bool CheckPassword(Client client, byte[] proof)
    {
        string from = client.Connection.RemoteAddress;
        int colon = from.LastIndexOf(':');
        string ip = colon > 0 ? from[..colon] : from;
        if (client.Connection.Id == 0 && IPAddress.TryParse(ip.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))
            return true;
        if (failures.TryGetValue(ip, out var f) && DateTime.UtcNow - f.Since > TimeSpan.FromMinutes(10))
            failures.Remove(ip);
        if (failures.TryGetValue(ip, out f) && f.Count >= 5)
        {
            Reject(client, "Too many wrong passwords. Try again in a few minutes.");
            return false;
        }
        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(proof, Proof(password, client.Challenge)))
        {
            failures.Remove(ip);
            return true;
        }
        failures[ip] = (f.Count + 1, f.Count == 0 ? DateTime.UtcNow : f.Since);
        Reject(client, "Wrong password.");
        return false;
    }

    void Reject(Client client, string reason)
    {
        client.Connection.Send(NetMessage.Reject, w => w.Write(reason));
        client.Welcomed = false;
        // (Closed once the message has gone: the timer drops it.)
        var connection = client.Connection;
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); connection.Close(reason); });
    }

    /// <summary>A fingerprint of the rules: the names and sizes of every rules file of the mod.</summary>
    static uint RulesFingerprint(GameContent content)
    {
        uint hash = 2166136261;
        string folder = Path.Combine(content.RootFolder, "mods");
        foreach (string file in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            foreach (char c in relative + new FileInfo(file).Length)
                hash = (hash ^ c) * 16777619;
        }
        return hash;
    }

    public void Dispose()
    {
        disposed = true;
        beacon?.Dispose();
        router?.Dispose();
        try { listener.Stop(); } catch (Exception) { }
        lock (gate)
            foreach (var c in clients.Values)
                c.Connection.Close("The host closed the game.");
    }
}
