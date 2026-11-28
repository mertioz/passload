using Microsoft.AspNetCore.Connections;

namespace Passload.Core;

public sealed class ShareService(DownloadHistory? history = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ShareCatalog _catalog = new(history);
    private TransferServer? _server;
    private int _preferredPort;
    private string? _publicHost;
    private RouterLease? _lease;
    private bool _closed;
    public Share[] Active => _catalog.Active;
    public int? LocalPort => _server?.Port;

    public Share? FindPublicWebRtc(string token) =>
        _catalog.Find(token) is { UsesPublicWebRtc: true } share ? share : null;

    public async Task<Share> CreateAsync(IEnumerable<string> paths, TimeSpan duration, ShareScope scope, int port,
        string? manualAddress, CancellationToken cancellation = default, Func<int, CancellationToken, Task>? authorizeInbound = null,
        bool publicWebRtc = false, string? publicHost = null, ShareDetails? details = null)
    {
        publicHost = NetworkInfo.NormalizePublicHost(publicHost);
        var files = await Task.Run(() => SharedFile.Select(paths), cancellation).ConfigureAwait(false);
        var network = NetworkInfo.Read();
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_server is not null && (_preferredPort != port || _publicHost != publicHost))
                throw new InvalidOperationException(Locale.Text("ServerSettingsBusy"));
            if (_server is null)
            {
                _server = await StartServerAsync(port, network, string.IsNullOrWhiteSpace(manualAddress), publicHost, duration, cancellation)
                    .ConfigureAwait(false);
                _preferredPort = port;
                _publicHost = publicHost;
            }
            var actualPort = _server.Port;
            var route = await RouteAsync(network, scope, actualPort, manualAddress, cancellation).ConfigureAwait(false);
            var trustedHttps = scope == ShareScope.Internet && publicHost is not null;
            var origin = trustedHttps
                ? await NetworkInfo.BindPublicHostAsync(route.Origin, publicHost!, cancellation).ConfigureAwait(false)
                : route.Origin;
            if (authorizeInbound is not null) await authorizeInbound(actualPort, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (publicHost is not null && _server.CertificateExpiry <= DateTimeOffset.UtcNow.Add(duration))
                throw new InvalidOperationException(Locale.Text("CertificateExpiresEarly"));
            return _catalog.Add(files, duration, scope, origin, _server.Fingerprint, route.Label, route.Mapped,
                publicWebRtc: publicWebRtc, trustedHttps: trustedHttps, details: details);
        }
        catch
        {
            await StopUnusedAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<TransferServer> StartServerAsync(int port, NetworkInfo network, bool mayChoosePort, string? publicHost,
        TimeSpan duration, CancellationToken cancellation)
    {
        Task<TransferServer> Listen(int value) => TransferServer.StartAsync(_catalog, value, network.Addresses,
            cancellation: cancellation, trustedHost: publicHost, duration: duration);
        try
        {
            try
            {
                return await Listen(port).ConfigureAwait(false);
            }
            catch (IOException ex) when (mayChoosePort && ex.InnerException is AddressInUseException)
            {
                return await Listen(0).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            throw new IOException(Locale.Text("TcpPortUnavailable", port), ex);
        }
    }

    private async Task<(Uri Origin, string Label, bool Mapped)> RouteAsync(NetworkInfo network, ShareScope scope, int port,
        string? manualAddress, CancellationToken cancellation)
    {
        if (scope == ShareScope.Local)
        {
            var lan = network.LanAddress ?? throw new InvalidOperationException(Locale.Text("LocalNetworkRequired"));
            return (NetworkInfo.Origin(lan, port), "PrivateNetwork", false);
        }
        if (NetworkInfo.ManualOrigin(manualAddress, port) is { } manual) return (manual, "ManualRoute", false);
        if (network.PublicV4 is { } v4) return (NetworkInfo.Origin(v4, port), "DirectIpv4", false);
        var failure = RouterFailure.NoReply;
        if (_lease is null)
            (_lease, failure) = await RouterLease.DiscoverAsync(network, port, cancellation).ConfigureAwait(false);
        if (_lease is not null) return (_lease.Origin, _lease.Label, true);
        if (network.PublicV6 is { } v6) return (NetworkInfo.Origin(v6, port), "DirectIpv6", false);
        var detail = failure switch
        {
            RouterFailure.NoPublicAddress => Locale.Text("RouterPrivateIp"),
            RouterFailure.PortRejected => Locale.Text("RouterRejected"),
            _ => Locale.Text("RouterNoReply")
        };
        throw new InvalidOperationException(detail + " " + Locale.Text("ForwardingHint"));
    }

    public async Task RevokeAsync(string token)
    {
        _catalog.Revoke(token);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopUnusedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<bool> MaintainAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            _catalog.Collect();
            var lost = _lease is not null && _catalog.Active.Any(s => s.UsesRouter) &&
                DateTimeOffset.UtcNow >= _lease.RenewAt && !await _lease.RenewAsync().ConfigureAwait(false);
            if (lost)
                foreach (var share in _catalog.Active.Where(s => s.UsesRouter)) _catalog.Revoke(share.Token);
            await StopUnusedAsync().ConfigureAwait(false);
            return lost;
        }
        finally { _gate.Release(); }
    }

    private async Task StopUnusedAsync()
    {
        var active = _catalog.Active;
        if (active.Length == 0 && _server is not null)
        {
            var server = _server;
            _server = null;
            await server.DisposeAsync().ConfigureAwait(false);
        }
        if (_lease is not null && !active.Any(s => s.UsesRouter))
        {
            var lease = _lease;
            _lease = null;
            await lease.ReleaseAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closed = true;
            _catalog.EndAll();
            await StopUnusedAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
