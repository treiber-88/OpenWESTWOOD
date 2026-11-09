using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace OpenWestwood.Client.Network;

/// <summary>
/// Opens the host's game port on the home router so players on the internet can reach it (UPnP, which most home
/// routers support and have switched on), and asks the router for this network's internet address. The opening is
/// closed again when the game is no longer hosted. Talks only to the router on the local network.
/// </summary>
public sealed class RouterPort : IDisposable
{
    readonly string controlUrl, serviceType;
    readonly int port;
    bool open;

    /// <summary>The internet address the router reports (null if it doesn't say).</summary>
    public string? ExternalAddress { get; private set; }

    RouterPort(string controlUrl, string serviceType, int port)
    {
        this.controlUrl = controlUrl;
        this.serviceType = serviceType;
        this.port = port;
    }

    /// <summary>
    /// Finds the router and opens TCP <paramref name="port"/> to this machine. Returns the opening, or null with the
    /// reason in <paramref name="problem"/> (no UPnP router answered, or it refused).
    /// </summary>
    public static RouterPort? Open(int port, out string problem)
    {
        problem = "";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            foreach (string location in Discover())
            {
                if (FindService(http, location) is not var (control, service))
                    continue;
                // This machine's address as the router sees it: the one used to reach the router.
                var routerHost = new Uri(control).Host;
                string localAddress;
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(routerHost, 1900);
                    localAddress = ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
                }
                var mapping = new RouterPort(control, service, port);
                var (ok, error) = mapping.Call(http, "AddPortMapping",
                    ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", "TCP"),
                    ("NewInternalPort", port.ToString()), ("NewInternalClient", localAddress), ("NewEnabled", "1"),
                    ("NewPortMappingDescription", "OpenWESTWOOD"), ("NewLeaseDuration", "0"));
                if (!ok)
                {
                    problem = $"The router refused to open port {port} ({error}).";
                    continue;
                }
                mapping.open = true;
                var (gotAddress, body) = mapping.Call(http, "GetExternalIPAddress");
                if (gotAddress && XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value is { Length: > 0 } ip)
                    mapping.ExternalAddress = ip;
                Console.WriteLine($"[net] router {routerHost} opened TCP {port} to {localAddress}; internet address {mapping.ExternalAddress ?? "unknown"}");
                return mapping;
            }
            // Routers that don't speak UPnP may speak NAT-PMP (Apple's and some others).
            if (NatPmp.Open(port) is { } pmp)
                return pmp;
            if (problem.Length == 0)
                problem = "Your router didn't answer (its UPnP / NAT-PMP setting is probably switched off).";
        }
        catch (Exception e)
        {
            problem = $"Could not reach the router ({e.Message}).";
        }
        return null;
    }

    /// <summary>NAT-PMP (RFC 6886): a mapping asked of the default gateway on UDP 5351, renewed while hosting.</summary>
    sealed class NatPmp
    {
        public static RouterPort? Open(int port)
        {
            foreach (var gateway in Gateways())
            {
                try
                {
                    using var udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.Client.ReceiveTimeout = 1000;
                    var to = new IPEndPoint(gateway, 5351);
                    // The external address: version 0, opcode 0.
                    byte[]? reply = Ask(udp, to, [0, 0], 12);
                    if (reply == null || reply[1] != 128 || reply[2] != 0 || reply[3] != 0)
                        continue;
                    string external = new IPAddress(reply.AsSpan(8, 4)).ToString();
                    if (Map(udp, to, port, Lifetime) is not true)
                        continue;
                    var mapping = new RouterPort("", "", port) { ExternalAddress = external, open = true, gateway = to };
                    // Renewed at half its lifetime, as the RFC asks, until the game is no longer hosted.
                    mapping.renew = new Timer(_ =>
                    {
                        try
                        {
                            using var u = new UdpClient(AddressFamily.InterNetwork);
                            u.Client.ReceiveTimeout = 1000;
                            Map(u, to, port, Lifetime);
                        }
                        catch (Exception)
                        {
                        }
                    }, null, TimeSpan.FromSeconds(Lifetime / 2), TimeSpan.FromSeconds(Lifetime / 2));
                    Console.WriteLine($"[net] NAT-PMP gateway {gateway} opened TCP {port}; internet address {external}");
                    return mapping;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        const int Lifetime = 3600;

        /// <summary>Maps (or with lifetime 0, removes) TCP <paramref name="port"/>: opcode 2.</summary>
        public static bool? Map(UdpClient udp, IPEndPoint to, int port, int lifetime)
        {
            byte[] request = new byte[12];
            request[1] = 2;
            request[4] = (byte)(port >> 8); request[5] = (byte)port;
            request[6] = (byte)(port >> 8); request[7] = (byte)port;
            request[8] = (byte)(lifetime >> 24); request[9] = (byte)(lifetime >> 16); request[10] = (byte)(lifetime >> 8); request[11] = (byte)lifetime;
            byte[]? reply = Ask(udp, to, request, 16);
            return reply == null ? null : reply[1] == 130 && reply[2] == 0 && reply[3] == 0;
        }

        static byte[]? Ask(UdpClient udp, IPEndPoint to, byte[] request, int length)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                udp.Send(request, request.Length, to);
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] reply = udp.Receive(ref from);
                    if (reply.Length >= length)
                        return reply;
                }
                catch (SocketException)
                {
                }
            }
            return null;
        }

        public static IEnumerable<IPAddress> Gateways() =>
            System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                .Distinct();
    }

    IPEndPoint? gateway;
    Timer? renew;

    /// <summary>This machine's IPv4 addresses on connections that are up (not the loopback).</summary>
    static IEnumerable<IPAddress> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();

    /// <summary>True for addresses that are not on the internet itself (the router is behind another network: carrier-grade NAT).</summary>
    public static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] < 128) || (b[0] == 169 && b[1] == 254) || b[0] == 0;
    }

    /// <summary>SSDP: asks the local network for internet gateways; returns their description URLs.</summary>
    static List<string> Discover()
    {
        var found = new List<string>();
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        // One search from each of this machine's network connections: the system's default for multicast may be
        // a virtual adapter (VPN, virtual machines) rather than the one the router is on.
        var sockets = new List<UdpClient>();
        foreach (var local in LocalAddresses())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                foreach (string st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                             "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
                {
                    byte[] request = Encoding.ASCII.GetBytes(
                        $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: {st}\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\n\r\n");
                    udp.Send(request, request.Length, target);
                }
                sockets.Add(udp);
            }
            catch (SocketException)
            {
            }
        }
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && sockets.Count > 0)
        {
            foreach (var udp in sockets)
                while (udp.Available > 0)
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        string reply = Encoding.ASCII.GetString(udp.Receive(ref from));
                        foreach (string line in reply.Split("\r\n"))
                            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase) && line[9..].Trim() is { Length: > 0 } location && !found.Contains(location))
                                found.Add(location);
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            Thread.Sleep(20);
        }
        foreach (var udp in sockets)
            udp.Dispose();
        return found;
    }

    /// <summary>The WAN connection service in a gateway's description: its control URL and service type.</summary>
    static (string Control, string Service)? FindService(HttpClient http, string location)
    {
        try
        {
            var doc = XDocument.Parse(http.GetStringAsync(location).Result);
            string? urlBase = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
            foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                string type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                string control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value ?? "";
                if (control.Length > 0 && (type.Contains("WANIPConnection") || type.Contains("WANPPPConnection")))
                    return (new Uri(new Uri(urlBase is { Length: > 0 } b ? b : location), control).ToString(), type);
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    (bool Ok, string Body) Call(HttpClient http, string action, params (string Name, string Value)[] arguments)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        body.Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        foreach (var (name, value) in arguments)
            body.Append($"<{name}>{System.Security.SecurityElement.Escape(value)}</{name}>");
        body.Append($"</u:{action}></s:Body></s:Envelope>");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
            using var response = http.Send(request);
            string text = response.Content.ReadAsStringAsync().Result;
            if (response.IsSuccessStatusCode)
                return (true, text);
            string? reason = XDocument.Parse(text).Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            return (false, reason ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    public void Dispose()
    {
        if (!open)
            return;
        open = false;
        renew?.Dispose();
        if (gateway != null)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ReceiveTimeout = 1000;
                NatPmp.Map(udp, gateway, port, 0);
            }
            catch (Exception)
            {
            }
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Call(http, "DeletePortMapping", ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", "TCP"));
        }
        catch (Exception)
        {
        }
    }
}
