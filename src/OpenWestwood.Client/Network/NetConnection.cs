using System.Collections.Concurrent;
using System.Net.Sockets;

namespace OpenWestwood.Client.Network;

/// <summary>The kinds of message between the players' machines and the host.</summary>
public enum NetMessage : byte
{
    Hello = 1,       // client -> host: protocol, command list and rules fingerprints, name, installed games
    Welcome = 2,     // host -> client: the client's id
    Reject = 3,      // host -> client: why it can't join
    Lobby = 4,       // host -> all: the lobby (slots, map, options, who is ready)
    LobbyRequest = 5,// client -> host: change my slot (colour, faction, team, start, ready)
    Chat = 6,        // anyone -> all: a line of chat
    Start = 7,       // host -> all: the game's settings, every random choice already made
    Loaded = 8,      // client -> host: the game is built
    Go = 9,          // host -> all: everyone is loaded, play
    Orders = 10,     // player -> all: that player's orders for one frame
    Sync = 11,       // player -> all: a checksum of the game after a frame
    Leave = 12,      // host -> all: a player left, and the last frame of theirs that was passed on
    Ping = 13,
    Pong = 14,
    Latency = 15,    // host -> all: everyone's round trip to the host (ms), for each machine's order latency
    HostOptions = 16,// host-side lobby edits from the host's own menu (slot states, map, cash...)
    Challenge = 17,  // host -> client, first thing: a random value to answer with proof of the password (and whether one is needed)
}

/// <summary>
/// One TCP link carrying framed messages ([length][type][payload]). A background thread reads; <see cref="Receive"/>
/// hands the messages to the game thread. Sending is thread-safe. Nagle's delay is off: orders go at once.
/// </summary>
public sealed class NetConnection : IDisposable
{
    readonly TcpClient client;
    readonly NetworkStream stream;
    readonly ConcurrentQueue<(NetMessage Type, byte[] Data)> inbox = new();
    readonly object sendLock = new();
    readonly Thread reader;
    volatile bool closed;

    public int Id { get; set; } = -1;
    public bool IsClosed => closed;
    public string? CloseReason { get; private set; }
    public string RemoteAddress { get; }

    /// <summary>Raised on the reading thread for each message (the host relays from there); null: queue them.</summary>
    public Action<NetConnection, NetMessage, byte[]>? OnMessage { get; set; }

    public NetConnection(TcpClient client, Action<NetConnection, NetMessage, byte[]>? onMessage = null)
    {
        OnMessage = onMessage; // (set before reading starts, so no message slips past it)
        this.client = client;
        client.NoDelay = true;
        stream = client.GetStream();
        RemoteAddress = client.Client.RemoteEndPoint?.ToString() ?? "?";
        reader = new Thread(Read) { IsBackground = true, Name = "net reader " + RemoteAddress };
        reader.Start();
    }

    public static NetConnection Connect(string host, int port, int timeoutMs = 5000)
    {
        var client = new TcpClient();
        if (!client.ConnectAsync(host, port).Wait(timeoutMs))
        {
            client.Dispose();
            throw new TimeoutException($"No answer from {host}:{port}.");
        }
        return new NetConnection(client);
    }

    void Read()
    {
        var header = new byte[5];
        try
        {
            while (!closed)
            {
                ReadExactly(header, 5);
                int length = BitConverter.ToInt32(header, 0);
                if (length < 0 || length > 16 << 20)
                    throw new InvalidDataException("Bad message length.");
                var data = new byte[length];
                ReadExactly(data, length);
                var type = (NetMessage)header[4];
                if (OnMessage != null)
                    OnMessage(this, type, data);
                else
                    inbox.Enqueue((type, data));
            }
        }
        catch (Exception e)
        {
            Close(e is EndOfStreamException ? "The other side closed the link." : e.Message);
        }
    }

    void ReadExactly(byte[] buffer, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = stream.Read(buffer, done, count - done);
            if (n <= 0)
                throw new EndOfStreamException();
            done += n;
        }
    }

    public bool Receive(out NetMessage type, out byte[] data)
    {
        if (inbox.TryDequeue(out var item))
        {
            (type, data) = item;
            return true;
        }
        type = 0;
        data = [];
        return false;
    }

    /// <summary>Debug (--net-delay): every message waits this long before it goes (a slow internet link on one PC).</summary>
    public static int SimulatedDelayMs { get; set; }

    readonly System.Collections.Concurrent.BlockingCollection<(long Due, byte[] Frame)>? delayed =
        SimulatedDelayMs > 0 ? new() : null;
    Thread? delayThread;

    public void Send(NetMessage type, byte[] payload)
    {
        if (closed)
            return;
        var frame = new byte[5 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), payload.Length);
        frame[4] = (byte)type;
        payload.CopyTo(frame, 5);
        if (delayed != null)
        {
            lock (sendLock)
                if (delayThread == null)
                {
                    delayThread = new Thread(SendDelayed) { IsBackground = true, Name = "net delay" };
                    delayThread.Start();
                }
            delayed.Add((Environment.TickCount64 + SimulatedDelayMs, frame));
            return;
        }
        WriteFrame(frame);
    }

    void SendDelayed()
    {
        foreach (var (due, frame) in delayed!.GetConsumingEnumerable())
        {
            long wait = due - Environment.TickCount64;
            if (wait > 0)
                Thread.Sleep((int)wait);
            if (closed)
                return;
            WriteFrame(frame);
        }
    }

    void WriteFrame(byte[] frame)
    {
        try
        {
            lock (sendLock)
                stream.Write(frame, 0, frame.Length);
        }
        catch (Exception e)
        {
            Close(e.Message);
        }
    }

    public void Send(NetMessage type, Action<BinaryWriter> write) => Send(type, Pack(write));

    public static byte[] Pack(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
            write(w);
        return ms.ToArray();
    }

    public static BinaryReader Unpack(byte[] data) => new(new MemoryStream(data));

    public void Close(string reason)
    {
        if (closed)
            return;
        closed = true;
        CloseReason = reason;
        try { client.Close(); } catch (Exception) { }
    }

    public void Dispose() => Close("Closed.");
}
