using OpenWestwood.Logic.Commands;

namespace OpenWestwood.Logic.Network;

/// <summary>
/// Keeps a network game's machines in step (lockstep, as Zero Hour's network code): every machine runs the whole
/// game, and a logic frame runs only once every player's orders for it are in. A player's orders are sent
/// <see cref="Latency"/> frames ahead, one packet a frame (empty when there are none), so the packets arrive before
/// they are needed and the game never waits unless the network is slower than the latency allows. The latency is each
/// machine's own choice and may change any time (more frames ahead on a slow link): a frame only needs a packet from
/// each player, whenever it was sent.
///
/// Every <see cref="SyncInterval"/> frames each machine sends a checksum of its game; a mismatch is a desync.
/// </summary>
public sealed class Lockstep
{
    readonly GameLogic game;
    readonly int localPlayer;
    readonly HashSet<int> players;
    readonly Dictionary<int, int> lastFrameOf = new();
    readonly Dictionary<int, Dictionary<int, GameCommand[]>> orders = new();
    readonly Dictionary<int, Dictionary<int, uint>> checksums = new();
    readonly List<GameCommand> outgoing = new();
    int sentThrough = -1;

    /// <summary>How many frames ahead this machine's orders are sent (2 to 30; 3 = 100 ms at 30 frames a second).</summary>
    public int Latency
    {
        get => latency;
        set => latency = Math.Clamp(value, 2, 30);
    }
    int latency = 3;

    public const int SyncInterval = 30;

    /// <summary>Sends this machine's orders for a frame to the others: (frame, encoded orders).</summary>
    public Action<int, byte[]>? SendOrders { get; set; }

    /// <summary>Sends this machine's checksum after a frame: (frame, checksum).</summary>
    public Action<int, uint>? SendSync { get; set; }

    /// <summary>Raised once when a checksum differs: (frame, the players whose games differ from this one).</summary>
    public event Action<int, IReadOnlyList<int>>? Desynced;

    public bool IsDesynced { get; private set; }

    /// <summary>The players whose orders the next frame still waits for (to say who the game waits on).</summary>
    public IEnumerable<int> WaitingFor => players.Where(p => NeedsOrdersFrom(p, game.Frame) && !Has(game.Frame, p));

    public Lockstep(GameLogic game, int localPlayer, IEnumerable<int> playerIndices)
    {
        this.game = game;
        this.localPlayer = localPlayer;
        players = new HashSet<int>(playerIndices) { localPlayer };
        // The player's own orders go to the network, not straight into the game.
        game.Commands.Intercept = command =>
        {
            outgoing.Add(command);
            return true;
        };
    }

    /// <summary>Sends packets for every frame up to the current frame plus the latency (the orders given so far go in the first).</summary>
    public void Pump()
    {
        while (sentThrough < game.Frame + Latency)
        {
            sentThrough++;
            var commands = outgoing.ToArray();
            outgoing.Clear();
            Receive(sentThrough, localPlayer, commands);
            SendOrders?.Invoke(sentThrough, Encode(commands));
        }
    }

    public static byte[] Encode(IReadOnlyList<GameCommand> commands)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)commands.Count);
        foreach (var c in commands)
            CommandCodec.Write(w, c);
        w.Flush();
        return ms.ToArray();
    }

    public static GameCommand[] Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        int n = r.ReadUInt16();
        var result = new GameCommand[n];
        for (int i = 0; i < n; i++)
            result[i] = CommandCodec.Read(r);
        return result;
    }

    /// <summary>Another player's orders for a frame. Orders from a player for someone else's units are dropped.</summary>
    public void Receive(int frame, int player, GameCommand[] commands)
    {
        if (frame < game.Frame)
            return;
        if (!orders.TryGetValue(frame, out var byPlayer))
            orders[frame] = byPlayer = new Dictionary<int, GameCommand[]>();
        byPlayer[player] = commands.Where(c => c.PlayerIndex == player).ToArray();
    }

    public void ReceiveEncoded(int frame, int player, byte[] data) => Receive(frame, player, Decode(data));

    /// <summary>A player left: their orders are needed up to <paramref name="lastFrame"/> (the last the host passed on), not after.</summary>
    public void PlayerLeft(int player, int lastFrame) => lastFrameOf[player] = lastFrame;

    bool NeedsOrdersFrom(int player, int frame) => !lastFrameOf.TryGetValue(player, out int last) || frame <= last;

    bool Has(int frame, int player) => orders.TryGetValue(frame, out var d) && d.ContainsKey(player);

    /// <summary>Whether the next frame may run (every player's orders for it are in).</summary>
    public bool CanStep => players.All(p => !NeedsOrdersFrom(p, game.Frame) || Has(game.Frame, p));

    /// <summary>Puts the frame's orders into the game, player by player (the same order on every machine).</summary>
    public void BeforeStep()
    {
        if (!orders.Remove(game.Frame, out var byPlayer))
            return;
        foreach (var (_, commands) in byPlayer.OrderBy(kv => kv.Key))
            foreach (var c in commands)
                game.Commands.EnqueueDirect(c);
    }

    /// <summary>After a frame: now and then, this machine's checksum goes out and is compared.</summary>
    public void AfterStep()
    {
        int frame = game.Frame - 1;
        if (frame % SyncInterval != 0)
            return;
        uint crc = game.ComputeChecksum();
        ReceiveSync(frame, localPlayer, crc);
        SendSync?.Invoke(frame, crc);
    }

    public void ReceiveSync(int frame, int player, uint crc)
    {
        if (!checksums.TryGetValue(frame, out var byPlayer))
            checksums[frame] = byPlayer = new Dictionary<int, uint>();
        byPlayer[player] = crc;
        if (!byPlayer.TryGetValue(localPlayer, out uint mine))
            return;
        var differ = byPlayer.Where(kv => kv.Value != mine).Select(kv => kv.Key).ToList();
        if (differ.Count > 0 && !IsDesynced)
        {
            IsDesynced = true;
            Desynced?.Invoke(frame, differ);
        }
        // Old checksums are forgotten once everyone's are in.
        if (players.All(p => byPlayer.ContainsKey(p) || !NeedsOrdersFrom(p, frame)))
            checksums.Remove(frame);
    }
}
