using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using Mono.Nat;

namespace Passload.Core;

internal sealed class RouterLease
{
    private const int LifetimeSeconds = 600;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
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

    public static async Task<(RouterLease? Lease, bool SharedAddress)> DiscoverAsync(NetworkInfo network, int port, CancellationToken cancellation)
    {
        if (network.Gateways.Length == 0) return (null, false);
        var devices = Channel.CreateUnbounded<INatDevice>();
        var seen = new HashSet<(IPEndPoint, NatProtocol)>();
        var sharedAddress = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        void OnFound(object? sender, DeviceEventArgs args)
        {
            if (network.Gateways.Contains(args.Device.DeviceEndpoint.Address))
                devices.Writer.TryWrite(args.Device);
        }
        NatUtility.DeviceFound += OnFound;
        try
        {
            NatUtility.StartDiscovery();
            await foreach (var device in devices.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!seen.Add((device.DeviceEndpoint, device.NatProtocol))) continue;
                try
                {
                    var external = await device.GetExternalIPAsync().WaitAsync(RequestTimeout, deadline.Token).ConfigureAwait(false);
                    if (!NetworkInfo.IsPublic(external)) { sharedAddress = true; continue; }
                    var lease = await CreateAsync(device, external, port, deadline.Token).ConfigureAwait(false);
                    return (lease, sharedAddress);
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
            NatUtility.StopDiscovery();
            devices.Writer.TryComplete();
        }
        return (null, sharedAddress);
    }

    internal static async Task<RouterLease> CreateAsync(INatDevice device, IPAddress external, int port, CancellationToken cancellation)
    {
        var owner = "Passload-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        var publicPort = port;
        if (device.NatProtocol == NatProtocol.Upnp)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await device.GetSpecificMappingAsync(Protocol.Tcp, publicPort).WaitAsync(RequestTimeout, cancellation).ConfigureAwait(false);
                }
                catch (MappingException e) when ((int)e.ErrorCode == 714) { break; }
                if (attempt == 4) throw new IOException("Aucun port disponible sur la box.");
                publicPort = RandomNumberGenerator.GetInt32(49152, 65535);
            }
        }
        // Request finite leases only. A delayed response or a crashed PC cannot leave a permanent rule.
        var request = new Mapping(Protocol.Tcp, port, publicPort, LifetimeSeconds, owner);
        var actual = await device.CreatePortMapAsync(request).WaitAsync(RequestTimeout, cancellation).ConfigureAwait(false);
        return new RouterLease(device, actual, external, owner);
    }

    public async Task<bool> RenewAsync()
    {
        try
        {
            var external = await _device.GetExternalIPAsync().WaitAsync(RequestTimeout).ConfigureAwait(false);
            if (NetworkInfo.Origin(external, _mapping.PublicPort) != Origin) return false;
            if (await OtherOwnerAsync().ConfigureAwait(false)) return false;
            var request = new Mapping(Protocol.Tcp, _mapping.PrivatePort, _mapping.PublicPort, LifetimeSeconds, _owner);
            _mapping = await _device.CreatePortMapAsync(request).WaitAsync(RequestTimeout).ConfigureAwait(false);
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

    private async Task<bool> OtherOwnerAsync()
    {
        if (_device.NatProtocol != NatProtocol.Upnp) return false;
        try
        {
            var current = await _device.GetSpecificMappingAsync(Protocol.Tcp, _mapping.PublicPort).WaitAsync(RequestTimeout).ConfigureAwait(false);
            return current.Description != _owner || current.PrivatePort != _mapping.PrivatePort;
        }
        catch (MappingException e) when ((int)e.ErrorCode == 714) { return false; }
    }

    public async Task ReleaseAsync()
    {
        try
        {
            if (!await OtherOwnerAsync().ConfigureAwait(false))
                await _device.DeletePortMapAsync(_mapping).WaitAsync(RequestTimeout).ConfigureAwait(false);
        }
        catch { /* A disconnected gateway will age out the finite lease. */ }
    }
}
