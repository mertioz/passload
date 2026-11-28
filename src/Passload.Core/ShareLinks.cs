using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Passload.Core;

public static partial class ShareLinks
{
    private static readonly Uri Portal = new("https://transfer.aymen.site/");

    [GeneratedRegex(@"^#v1\.[0-9]{13}\.([A-Za-z0-9_-]{1,512})(?:\.[A-Za-z0-9_-]{87}\.[A-Za-z0-9_-]{43})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PortalFragment();

    internal static Uri Publish(Uri direct, DateTimeOffset expires, string? room, string? key)
    {
        var address = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(direct.AbsoluteUri));
        var fragment = $"#v1.{expires.ToUnixTimeMilliseconds()}.{address}";
        if (room is not null) fragment += $".{room}.{key}";
        return new Uri(Portal, fragment);
    }

    public static string DirectAddress(string input)
    {
        var link = input.Trim();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Query) != Portal.AbsoluteUri ||
            PortalFragment().Match(uri.Fragment) is not { Success: true } match)
            return link;

        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(match.Groups[1].Value)); }
        catch (FormatException) { throw new FormatException(Locale.Text("InvalidPassloadLink")); }
    }
}

internal sealed record WebRtcInvitation(string Room, string AccessKey, string OwnerKey)
{
    public static WebRtcInvitation Create()
    {
        using var owner = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var point = owner.ExportParameters(false).Q;
        return new WebRtcInvitation(
            WebEncoders.Base64UrlEncode([4, .. point.X!, .. point.Y!]),
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            WebEncoders.Base64UrlEncode(owner.ExportPkcs8PrivateKey()));
    }
}
