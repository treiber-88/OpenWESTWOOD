using OpenWestwood.Core.Installs;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Multiplayer;
using OpenWestwood.Logic.Network;

namespace OpenWestwood.Client.Network;

/// <summary>
/// This machine's part in a network game: its link to the host (the host's own machine links through the loopback
/// like everyone else), the lobby as the host last sent it, chat, the start, and in the game the bridge between
/// the network and <see cref="Lockstep"/>. Everything but ping answers is handled on the game thread in <see cref="Poll"/>.
/// </summary>
public sealed class NetSession : IDisposable
{
    readonly NetConnection connection;

    /// <summary>The host this machine runs, when it is the host.</summary>
    public NetHost? Host { get; private set; }

    public int ClientId { get; private set; } = -1;
    public NetLobby? Lobby { get; private set; }
    public bool IsHost => Host != null;
    public string? Error { get; private set; }
    public bool IsConnected => !connection.IsClosed;
    public string? CloseReason => connection.CloseReason;

    /// <summary>The lines of chat (and the host's notes), oldest first.</summary>
    public List<string> Chat { get; } = new();

    /// <summary>Raised when the host sent a new lobby.</summary>
    public event Action? LobbyChanged;

    /// <summary>Raised when the game starts: the settings with every random choice made.</summary>
    public event Action<GameInfo>? Started;

    /// <summary>Everyone has loaded: play.</summary>
    public bool Go { get; private set; }

    /// <summary>A message for the player (a player left, the game desynced...).</summary>
    public event Action<string>? Notice;

    public Lockstep? Lockstep { get; private set; }
    int localPlayerIndex = -1;
    readonly Dictionary<int, int> rtts = new();

    NetSession(NetConnection connection)
    {
        this.connection = connection;
    }

    /// <summary>Joins a game: connects, says who it is, and waits (in <see cref="Poll"/>) for the lobby.</summary>
    public static NetSession Join(string address, int port, string name, string fingerprint, string password = "")
    {
        Console.WriteLine($"[net] joining {address}:{port} as {name}");
        // (Pings are answered at once, from the reading thread, which keeps the measured round trips honest.)
        NetSession? session = null;
        var tcp = new System.Net.Sockets.TcpClient();
        if (!tcp.ConnectAsync(address, port).Wait(5000))
        {
            tcp.Dispose();
            throw new TimeoutException($"No answer from {address}:{port}.");
        }
        var link = new NetConnection(tcp, (c, type, data) => session!.OnReaderMessage(c, type, data));
        session = new NetSession(link);
        // (Hello goes when the host's challenge arrives: it carries the proof of the password.)
        session.hello = challenge => link.Send(NetMessage.Hello, w =>
        {
            w.Write(fingerprint);
            w.Write(name);
            w.Write(GameInstalls.Has(OriginalGame.ZeroHour));
            w.Write(GameInstalls.Has(OriginalGame.KanesWrath));
            byte[] proof = NetHost.Proof(password, challenge);
            w.Write((byte)proof.Length);
            w.Write(proof);
        });
        return session;
    }

    /// <summary>Hosts a game here and joins it.</summary>
    public static NetSession HostGame(string gameName, string playerName, int port, bool lan, string fingerprint,
        Func<NetLobby, string?> checkStart, Action<GameInfo, NetLobby> resolveRandoms, string password = "", bool openRouter = false)
    {
        var host = new NetHost(gameName, port, lan, checkStart, resolveRandoms, fingerprint, password, openRouter);
        var session = Join("127.0.0.1", host.Port, playerName, fingerprint, password);
        session.Host = host;
        return session;
    }

    readonly System.Collections.Concurrent.ConcurrentQueue<(NetMessage, byte[])> inbox = new();

    Action<byte[]>? hello;

    void OnReaderMessage(NetConnection c, NetMessage type, byte[] data)
    {
        if (type == NetMessage.Challenge)
        {
            var r = NetConnection.Unpack(data);
            byte[] challenge = r.ReadBytes(16);
            hello?.Invoke(challenge);
            hello = null;
            return;
        }
        if (type == NetMessage.Ping)
        {
            c.Send(NetMessage.Pong, data);
            return;
        }
        inbox.Enqueue((type, data));
    }

    /// <summary>Handles what came in (call every frame, in the menus and in the game).</summary>
    public void Poll()
    {
        while (Host?.HostNotes.TryDequeue(out string? note) == true)
            Chat.Add(note);
        while (inbox.TryDequeue(out var item))
        {
            var (type, data) = item;
            try
            {
                Handle(type, data);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Network message {type}: {e.Message}");
            }
        }
        Lockstep?.Pump();
    }

    void Handle(NetMessage type, byte[] data)
    {
        var r = NetConnection.Unpack(data);
        switch (type)
        {
            case NetMessage.Welcome:
                ClientId = r.ReadByte();
                break;
            case NetMessage.Reject:
                Error = r.ReadString();
                Notice?.Invoke(Error);
                break;
            case NetMessage.Lobby:
                Lobby = NetLobby.Read(r);
                LobbyChanged?.Invoke();
                break;
            case NetMessage.Chat:
            {
                string from = r.ReadString(), text = r.ReadString();
                Chat.Add(from.Length > 0 ? $"{from}: {text}" : text);
                if (Chat.Count > 200)
                    Chat.RemoveAt(0);
                break;
            }
            case NetMessage.Start:
                Started?.Invoke(NetLobby.ReadInfo(r));
                break;
            case NetMessage.Go:
                Go = true;
                break;
            case NetMessage.Orders:
            {
                int frame = r.ReadInt32();
                int player = r.ReadByte();
                Lockstep?.ReceiveEncoded(frame, player, r.ReadBytes(data.Length - 5));
                break;
            }
            case NetMessage.Sync:
            {
                int frame = r.ReadInt32();
                int player = r.ReadByte();
                Lockstep?.ReceiveSync(frame, player, r.ReadUInt32());
                break;
            }
            case NetMessage.Leave:
            {
                int player = r.ReadByte();
                int lastFrame = r.ReadInt32();
                string name = r.ReadString();
                Lockstep?.PlayerLeft(player, lastFrame);
                Notice?.Invoke($"{name} left the game.");
                break;
            }
            case NetMessage.Latency:
            {
                int n = r.ReadByte();
                rtts.Clear();
                for (int i = 0; i < n; i++)
                    rtts[r.ReadByte()] = r.ReadUInt16();
                AdjustLatency();
                break;
            }
        }
    }

    /// <summary>
    /// How many frames ahead this machine sends its orders: enough for them to reach the farthest player through the
    /// host (my half round trip plus theirs, plus some slack), so nobody waits for them. A link that gets slower
    /// lengthens it, one that gets quicker shortens it.
    /// </summary>
    void AdjustLatency()
    {
        if (Lockstep == null || rtts.Count == 0)
            return;
        int mine = rtts.GetValueOrDefault(ClientId, 0);
        int worstOther = rtts.Where(kv => kv.Key != ClientId).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        float ms = mine / 2f + worstOther / 2f + 45;
        Lockstep.Latency = (int)MathF.Ceiling(ms / (1000f / Logic.Core.LogicTime.FramesPerSecond)) + 1;
    }

    // ---- Lobby requests ----

    public void SendChat(string text) => connection.Send(NetMessage.Chat, w => w.Write(text));

    /// <summary>My slot's choices (and whether I accept the settings).</summary>
    public void RequestSlot(int color, int template, int team, int startPos, bool accept) =>
        connection.Send(NetMessage.LobbyRequest, w =>
        {
            w.Write((short)color);
            w.Write((short)template);
            w.Write((sbyte)team);
            w.Write((sbyte)startPos);
            w.Write(accept);
        });

    /// <summary>The host's edits: the computer and open slots, the map and the options.</summary>
    public void SendHostOptions(GameInfo info) => connection.Send(NetMessage.HostOptions, w => NetLobby.WriteInfo(w, info));

    public void RequestStart() => connection.Send(NetMessage.Start, _ => { });

    // ---- In the game ----

    /// <summary>Joins the built game to the network: this machine plays <paramref name="localPlayer"/>.</summary>
    public void Attach(GameLogic game, Logic.Players.Player localPlayer, IEnumerable<Logic.Players.Player> humans)
    {
        localPlayerIndex = localPlayer.Index;
        Lockstep = new Lockstep(game, localPlayer.Index, humans.Select(p => p.Index))
        {
            SendOrders = (frame, orders) => connection.Send(NetMessage.Orders, w =>
            {
                w.Write(frame);
                w.Write((byte)localPlayerIndex);
                w.Write(orders);
            }),
            SendSync = (frame, crc) => connection.Send(NetMessage.Sync, w =>
            {
                w.Write(frame);
                w.Write((byte)localPlayerIndex);
                w.Write(crc);
            }),
        };
        Lockstep.Desynced += (frame, players) => Notice?.Invoke($"The game is out of step with the other players (frame {frame}).");
        AdjustLatency();
        connection.Send(NetMessage.Loaded, _ => { });
    }

    public void Dispose()
    {
        connection.Close("Left the game.");
        Host?.Dispose();
    }
}
