using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenWestwood.Client.Network;

/// <summary>
/// LAN game discovery (as Zero Hour's LAN lobby): a host announces its game once a second by UDP broadcast on
/// <see cref="Port"/>; the lobby screen listens and lists the games heard in the last few seconds.
/// </summary>
public sealed class LanBeacon : IDisposable
{
    public const int Port = 8086;
    const string Tag = "OWWLAN1|";

    readonly Func<string> describe;
    readonly Thread thread;
    volatile bool disposed;

    public LanBeacon(Func<string> describe)
    {
        this.describe = describe;
        thread = new Thread(Run) { IsBackground = true, Name = "LAN beacon" };
        thread.Start();
    }

    void Run()
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        while (!disposed)
        {
            try
            {
                string text = describe();
                if (text.Length > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(Tag + text);
                    udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, Port));
                    // Also to this machine (two copies of the game on one computer find each other).
                    udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, Port));
                }
            }
            catch (Exception)
            {
                // (No network: nothing to announce on.)
            }
            Thread.Sleep(1000);
        }
    }

    public void Dispose() => disposed = true;

    /// <summary>A game heard on the LAN.</summary>
    public sealed record LanGame(string Name, string Map, int Players, int MaxPlayers, string Address, int Port, string Fingerprint, DateTime Heard, bool Locked = false);

    /// <summary>Listens for announced games (several copies of the game on one machine can all listen).</summary>
    public sealed class Listener : IDisposable
    {
        readonly UdpClient? udp;
        readonly Dictionary<string, LanGame> games = new();
        readonly Thread? thread;
        volatile bool disposed;

        public Listener()
        {
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                thread = new Thread(Run) { IsBackground = true, Name = "LAN listener" };
                thread.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"LAN game list unavailable: {e.Message}");
            }
        }

        void Run()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!disposed && udp != null)
            {
                try
                {
                    var bytes = udp.Receive(ref from);
                    string text = Encoding.UTF8.GetString(bytes);
                    if (!text.StartsWith(Tag))
                        continue;
                    var parts = text[Tag.Length..].Split('|');
                    if (parts.Length < 6 || !int.TryParse(parts[4], out int port))
                        continue;
                    var game = new LanGame(parts[0], parts[1], int.TryParse(parts[2], out int p) ? p : 0, int.TryParse(parts[3], out int m) ? m : 0,
                        from.Address.ToString(), port, parts[5], DateTime.UtcNow, parts.Length > 6 && parts[6] == "locked");
                    lock (games)
                        games[$"{game.Address}:{port}"] = game;
                }
                catch (Exception)
                {
                    if (disposed)
                        return;
                }
            }
        }

        /// <summary>The games heard in the last four seconds (one entry per game, the loopback copy preferred).</summary>
        public List<LanGame> Games
        {
            get
            {
                lock (games)
                {
                    var now = DateTime.UtcNow;
                    foreach (var key in games.Where(kv => now - kv.Value.Heard > TimeSpan.FromSeconds(4)).Select(kv => kv.Key).ToList())
                        games.Remove(key);
                    return games.Values.GroupBy(g => (g.Name, g.Port)).Select(g => g.OrderBy(x => x.Address == "127.0.0.1" ? 0 : 1).First())
                        .OrderBy(g => g.Name).ToList();
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            udp?.Dispose();
        }
    }
}
