using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Mono.Nat;

namespace Passload.Core;

internal enum RouterFailure { NoReply, NoPublicAddress, PortRejected }

internal sealed class RouterLease
{
    private const int LifetimeSeconds = 600;
    private const int NoSuchEntry = 714;
    private const int ConflictWithOtherMechanisms = 729;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly SemaphoreSlim DiscoveryLock = new(1, 1);
    private readonly INatDevice _device;
    private readonly string _owner;
    private Mapping _mapping;
    public Uri Origin { get; }
    public string Label => _device.NatProtocol == NatProtocol.Pmp ? "NAT-PMP" : "UPnP";
    public DateTimeOffset RenewAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }

    private RouterLease(INatDevice device, Mapping mapping, IPAddress external, string owner)
    {
        _device = device;
        _mapping = mapping;
        _owner = owner;
        Origin = NetworkInfo.Origin(external, mapping.PublicPort);
        UpdateTimes();
    }

    public static async Task<(RouterLease? Lease, RouterFailure Failure)> DiscoverAsync(NetworkInfo network, int port, CancellationToken cancellation)
    {
        var gateways = network.Gateways.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
        if (gateways.Length == 0) return (null, RouterFailure.NoReply);
        await DiscoveryLock.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await SearchAsync(gateways, port, cancellation).ConfigureAwait(false); }
        finally { DiscoveryLock.Release(); }
    }

    private static async Task<(RouterLease? Lease, RouterFailure Failure)> SearchAsync(IPAddress[] gateways, int port, CancellationToken cancellation)
    {
        var devices = Channel.CreateUnbounded<INatDevice>();
        var seen = new HashSet<(IPEndPoint, NatProtocol)>();
        var failure = RouterFailure.NoReply;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        void OnFound(object? sender, DeviceEventArgs args)
        {
            if (gateways.Contains(args.Device.DeviceEndpoint.Address))
                devices.Writer.TryWrite(args.Device);
        }
        NatUtility.DeviceFound += OnFound;
        Task[] searches = [];
        try
        {
            NatUtility.StartDiscovery(NatProtocol.Pmp);
            searches = gateways.Select(gateway => UpnpDiscovery.SearchAsync(gateway, deadline.Token)).ToArray();
            await foreach (var device in devices.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!seen.Add((device.DeviceEndpoint, device.NatProtocol))) continue;
                try
                {
                    var external = await device.GetExternalIPAsync().WaitAsync(RequestTimeout, deadline.Token).ConfigureAwait(false);
                    if (!NetworkInfo.IsPublic(external))
                    {
                        if (failure == RouterFailure.NoReply) failure = RouterFailure.NoPublicAddress;
                        continue;
                    }
                    failure = RouterFailure.PortRejected;
                    var lease = await CreateAsync(device, external, port, deadline.Token).ConfigureAwait(false);
                    return (lease, failure);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Another protocol or gateway may still provide a usable mapping.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        finally
        {
            NatUtility.DeviceFound -= OnFound;
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(searches).ConfigureAwait(false); }
            finally
            {
                NatUtility.StopDiscovery();
                devices.Writer.TryComplete();
            }
        }
        return (null, failure);
    }

    internal static async Task<RouterLease> CreateAsync(INatDevice device, IPAddress external, int port, CancellationToken cancellation)
    {
        var owner = "Passload-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        if (device.NatProtocol != NatProtocol.Upnp)
        {
            var assigned = await MapAsync(device, port, port, owner, cancellation).ConfigureAwait(false);
            return new RouterLease(device, assigned, external, owner);
        }

        // An ISP can assign the router only a portion of the external TCP ports.
        // Try the requested port first, then one alternative in each quarter.
        int[] candidates =
        [
            port,
            RandomNumberGenerator.GetInt32(49152, 65536),
            RandomNumberGenerator.GetInt32(32768, 49152),
            RandomNumberGenerator.GetInt32(16384, 32768),
            RandomNumberGenerator.GetInt32(1024, 16384)
        ];
        foreach (var candidate in candidates.Distinct())
        {
            if (await FindAsync(device, candidate, cancellation).ConfigureAwait(false) is not null) continue;
            try
            {
                var assigned = await MapAsync(device, port, candidate, owner, cancellation).ConfigureAwait(false);
                var confirmed = await ConfirmUpnpAsync(device, assigned, owner, cancellation).ConfigureAwait(false)
                    ?? throw new IOException("La redirection temporaire n’a pas pu être confirmée.");
                return new RouterLease(device, confirmed, external, owner);
            }
            catch (MappingException error) when (error.ErrorCode == ErrorCode.ConflictInMappingEntry ||
                    (int)error.ErrorCode == ConflictWithOtherMechanisms) { }
        }
        throw new IOException("Aucun port public disponible sur la box.");
    }

    private static async Task<Mapping?> FindAsync(INatDevice device, int port, CancellationToken cancellation = default)
    {
        try { return await device.GetSpecificMappingAsync(Protocol.Tcp, port).WaitAsync(RequestTimeout, cancellation).ConfigureAwait(false); }
        catch (MappingException error) when ((int)error.ErrorCode == NoSuchEntry) { return null; }
    }

    private static bool Owned(Mapping? mapping, string owner, int privatePort) =>
        mapping is not null && mapping.Description == owner && mapping.PrivatePort == privatePort;

    private static Task<Mapping> MapAsync(INatDevice device, int privatePort, int publicPort, string owner, CancellationToken cancellation = default) =>
        device.CreatePortMapAsync(new Mapping(Protocol.Tcp, privatePort, publicPort, LifetimeSeconds, owner))
            .WaitAsync(RequestTimeout, cancellation);

    private static async Task<Mapping?> ConfirmUpnpAsync(INatDevice device, Mapping requested, string owner, CancellationToken cancellation = default)
    {
        var current = await FindAsync(device, requested.PublicPort, cancellation).ConfigureAwait(false);
        if (!Owned(current, owner, requested.PrivatePort)) return null;
        if (current!.Lifetime > 0) return current;
        // Some routers install a permanent rule even when a finite duration was requested.
        await device.DeletePortMapAsync(requested).WaitAsync(RequestTimeout).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> RenewAsync()
    {
        try
        {
            var external = await _device.GetExternalIPAsync().WaitAsync(RequestTimeout).ConfigureAwait(false);
            if (NetworkInfo.Origin(external, _mapping.PublicPort) != Origin) return false;
            if (_device.NatProtocol == NatProtocol.Upnp)
            {
                var current = await FindAsync(_device, _mapping.PublicPort).ConfigureAwait(false);
                if (current is not null && !Owned(current, _owner, _mapping.PrivatePort)) return false;
            }
            var renewed = await MapAsync(_device, _mapping.PrivatePort, _mapping.PublicPort, _owner).ConfigureAwait(false);
            if (_device.NatProtocol == NatProtocol.Upnp)
            {
                var confirmed = await ConfirmUpnpAsync(_device, renewed, _owner).ConfigureAwait(false);
                if (confirmed is null) return false;
                renewed = confirmed;
            }
            _mapping = renewed;
            if (_mapping.PublicPort != Origin.Port) return false;
            UpdateTimes();
            return true;
        }
        catch
        {
            RenewAt = DateTimeOffset.UtcNow.AddSeconds(10);
            return DateTimeOffset.UtcNow < EndsAt;
        }
    }

    private void UpdateTimes()
    {
        var seconds = Math.Clamp(_mapping.Lifetime, 1, LifetimeSeconds);
        var now = DateTimeOffset.UtcNow;
        EndsAt = now.AddSeconds(seconds);
        RenewAt = now.AddSeconds(seconds * 0.5);
    }

    public async Task ReleaseAsync()
    {
        try
        {
            if (_device.NatProtocol == NatProtocol.Upnp &&
                !Owned(await FindAsync(_device, _mapping.PublicPort).ConfigureAwait(false), _owner, _mapping.PrivatePort)) return;
            await _device.DeletePortMapAsync(_mapping).WaitAsync(RequestTimeout).ConfigureAwait(false);
        }
        catch { /* A disconnected gateway will age out the finite lease. */ }
    }
}
