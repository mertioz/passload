using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Passload.Core;

public sealed record NetworkInfo(IPAddress? LanAddress, IPAddress? PublicV4, IPAddress? PublicV6, IPAddress[] Gateways, IPAddress[] Addresses)
{
    public const int DefaultPort = 47831;

    public static NetworkInfo Read()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .OrderByDescending(n => n.GatewayAddresses.Count > 0).ToArray();
        var addresses = interfaces.SelectMany(n => n.UnicastAddresses)
            .Where(a => !OperatingSystem.IsWindows() || a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
            .Select(a => a.Address).Where(a => !IPAddress.IsLoopback(a)).Distinct().ToArray();
        var v4 = PreferredAddress(AddressFamily.InterNetwork, "192.0.2.1", addresses);
        var v6 = PreferredAddress(AddressFamily.InterNetworkV6, "2001:db8::1", addresses);
        var lan = v4 ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
        return new(lan, v4 is not null && IsPublic(v4) ? v4 : null,
            v6 is not null && IsPublic(v6) ? v6 : addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && IsPublic(a)),
            interfaces.SelectMany(n => n.GatewayAddresses).Select(g => g.Address).Distinct().ToArray(), addresses);
    }

    private static IPAddress? PreferredAddress(AddressFamily family, string route, IPAddress[] addresses)
    {
        // UDP Connect only asks the OS for the route. No datagram is sent.
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(route), 9);
            var chosen = ((IPEndPoint)socket.LocalEndPoint!).Address;
            if (addresses.Contains(chosen)) return chosen;
        }
        catch (SocketException) { }
        return addresses.FirstOrDefault(a => a.AddressFamily == family && !a.IsIPv6LinkLocal);
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 16)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8)
                && !(b[0] == 0x3f && (b[1] & 0xf0) == 0xf0);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99))) &&
            !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    internal static bool IsLocalPeer(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || !IsPublic(address)) return true;
        // Some LANs assign globally routed addresses, particularly with IPv6.
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.AddressFamily == address.AddressFamily && a.PrefixLength > 0 &&
                System.Net.IPNetwork.Parse($"{Prefix(a.Address, a.PrefixLength)}/{a.PrefixLength}").Contains(address));
    }

    private static IPAddress Prefix(IPAddress address, int length)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] &= (byte)(0xff << (8 - Math.Clamp(length - 8 * i, 0, 8)));
        return new IPAddress(bytes);
    }

    public static Uri Origin(IPAddress address, int port) => new UriBuilder("https", address.ToString(), port).Uri;

    public static string? NormalizePublicHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var message = Locale.Text("InvalidDomain");
        string host;
        try { host = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(input.Trim().TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new FormatException(message); }
        if (!host.Contains('.') || Uri.CheckHostName(host) != UriHostNameType.Dns || host.EndsWith(".local", StringComparison.Ordinal))
            throw new FormatException(message);
        return host;
    }

    internal static async Task<Uri> BindPublicHostAsync(Uri origin, string host, CancellationToken cancellation)
    {
        if (origin.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)) return origin;
        if (!IPAddress.TryParse(origin.IdnHost, out var address))
            throw new InvalidOperationException(Locale.Text("AddressCertificateMismatch"));
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false); }
        catch (SocketException ex) { throw new InvalidOperationException(Locale.Text("DnsUnavailable", host), ex); }
        if (!addresses.Contains(address))
            throw new InvalidOperationException(Locale.Text("DnsWrongAddress", host));
        return new UriBuilder(origin) { Host = host }.Uri;
    }

    public static Uri? ManualOrigin(string? input, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        var message = Locale.Text("InvalidPublicAddress");
        if (IPAddress.TryParse(input.Trim('[', ']'), out var raw))
            return IsPublic(raw) ? Origin(raw, defaultPort) : throw new FormatException(message);
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" ||
            uri.IsLoopback || uri.Port is < 1 or > 65535 || input.Contains('\\') || input.Any(char.IsWhiteSpace)) throw new FormatException(message);
        if (IPAddress.TryParse(uri.IdnHost, out var ip))
        {
            if (!IsPublic(ip)) throw new FormatException(message);
        }
        else if (uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains('.') || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(message);
        var authority = input[(input.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var hasPort = authority.LastIndexOf(':') > authority.LastIndexOf(']');
        return new UriBuilder(uri) { Port = hasPort ? uri.Port : defaultPort }.Uri;
    }
}
