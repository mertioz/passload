using System.Security.Cryptography;

namespace Passload.Core;

public sealed class InternetInvitation
{
    public required string Room { get; init; }
    public required string AccessKey { get; init; }
    public required string OwnerKey { get; init; }
    public required long Until { get; init; }
    public required Uri Link { get; init; }

    public static InternetInvitation Create(Uri viewer, TimeSpan duration)
    {
        static string Url64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
        using var owner = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var point = owner.ExportParameters(false).Q;
        var room = Url64([4, .. point.X!, .. point.Y!]);
        var key = Url64(RandomNumberGenerator.GetBytes(32));
        var until = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        return new() { Room=room, AccessKey=key, OwnerKey=Url64(owner.ExportPkcs8PrivateKey()), Until=until,
            Link=new Uri(viewer, $"#p2.{room}.{key}.{until}") };
    }

    public object StartMessage(string token, bool relay) =>
        new {op="start", token, room=Room, owner=OwnerKey, key=AccessKey, until=Until, relay};
}
