using System.Net;
using System.Net.Sockets;
using System.Text;
using Mono.Nat;

namespace Passload.Core;

internal static class UpnpDiscovery
{
    private static readonly IPAddress Multicast = IPAddress.Parse("239.255.255.250");
    private static readonly string[] Targets =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2"
    ];

    internal static async Task SearchAsync(IPAddress gateway, CancellationToken cancellation)
    {
        try
        {
            using var route = new UdpClient(AddressFamily.InterNetwork);
            route.Connect(gateway, 1900);
            var local = ((IPEndPoint)route.Client.LocalEndPoint!).Address;
            // Bind an ephemeral port so the Windows SSDP service cannot take the replies.
            using var client = new UdpClient(new IPEndPoint(local, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            var locations = new HashSet<Uri>();
            var sender = SendQueriesAsync(client, gateway, cancellation);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var reply = await client.ReceiveAsync(cancellation).ConfigureAwait(false);
                    if (!gateway.Equals(reply.RemoteEndPoint.Address)) continue;
                    var location = GatewayLocation(reply.Buffer, gateway);
                    if (location is not null && locations.Add(location))
                        NatUtility.ParseMessage(NatProtocol.Upnp, local, reply.Buffer, reply.RemoteEndPoint);
                }
            }
            finally { await sender.ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (SocketException) { }
    }

    internal static Uri? GatewayLocation(byte[] reply, IPAddress gateway)
    {
        var lines = Encoding.ASCII.GetString(reply).Split("\r\n");
        if (!lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !lines[0].StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase)) return null;
        var header = lines.FirstOrDefault(line => line.StartsWith("location:", StringComparison.OrdinalIgnoreCase));
        if (header is null || !Uri.TryCreate(header[9..].Trim(), UriKind.Absolute, out var location) ||
            location.Scheme != Uri.UriSchemeHttp || location.UserInfo.Length != 0 || location.Fragment.Length != 0 ||
            !IPAddress.TryParse(location.IdnHost, out var address) || !gateway.Equals(address)) return null;
        return location;
    }

    private static async Task SendQueriesAsync(UdpClient client, IPAddress gateway, CancellationToken cancellation)
    {
        foreach (var address in new[] { Multicast, gateway })
        {
            var destination = new IPEndPoint(address, 1900);
            foreach (var target in Targets)
            {
                var message = $"M-SEARCH * HTTP/1.1\r\nHOST: {destination}\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n";
                try { await client.SendAsync(Encoding.ASCII.GetBytes(message), destination, cancellation).ConfigureAwait(false); }
                catch (SocketException) { break; }
            }
        }
    }
}
