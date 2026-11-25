using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace Passload.Core;

internal sealed class PublicRtcRoom
{
    private const int MaxGuests = 8;
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _guests = new();
    private Connection? _host;

    public async Task RunHostAsync(HttpContext context, CancellationToken ended)
    {
        if (!CanUpgrade(context)) return;
        var host = new Connection();
        lock (_gate)
        {
            if (_host is not null)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
            host.Post(new { op = "ready" });
            _host = host;
        }

        try { await host.RunAsync(context, ended, input => SendToGuest(host, input)); }
        finally
        {
            lock (_gate)
            {
                _host = null;
                foreach (var guest in _guests.Values) guest.Stop();
                _guests.Clear();
            }
        }
    }

    public async Task RunGuestAsync(HttpContext context, CancellationToken ended)
    {
        if (!CanUpgrade(context)) return;
        var guest = new Connection();
        var peer = Guid.NewGuid().ToString("N");
        var address = DownloadAddress.Normalize(context.Connection.RemoteIpAddress);
        var preview = address is not null && IPAddress.IsLoopback(address);
        Connection host;
        lock (_gate)
        {
            if (_host is not { } connected || !connected.Active)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (_guests.Count >= MaxGuests)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            host = connected;
            if (!host.Post(new { op = "joined", peer, address = address?.ToString(), preview }))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            guest.Post(new { op = "ready", peer });
            _guests.Add(peer, guest);
        }

        try
        {
            var signals = 0;
            await guest.RunAsync(context, ended, input =>
            {
                if (++signals > 128) { guest.Stop(); return; }
                if (GetSignal(input, out var signal) && !host.Post(new { op = "signal", peer, signal })) guest.Stop();
            });
        }
        finally
        {
            lock (_gate)
            {
                if (_guests.Remove(peer)) host.Post(new { op = "left", peer });
            }
        }
    }

    private void SendToGuest(Connection host, JsonElement input)
    {
        if (!GetSignal(input, out var signal) ||
            !input.TryGetProperty("peer", out var target) || target.ValueKind != JsonValueKind.String) return;
        var peer = target.GetString();
        if (peer is not { Length: 32 }) return;
        lock (_gate)
            if (ReferenceEquals(_host, host) && _guests.TryGetValue(peer, out var guest) &&
                !guest.Post(new { op = "signal", peer, signal })) guest.Stop();
    }

    private static bool GetSignal(JsonElement input, out JsonElement signal)
    {
        signal = default;
        return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("signal", out signal) &&
            signal.ValueKind == JsonValueKind.Object;
    }

    private static bool CanUpgrade(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        else if (!string.Equals(context.Request.Headers.Origin, $"{context.Request.Scheme}://{context.Request.Host}",
            StringComparison.OrdinalIgnoreCase))
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else return true;
        return false;
    }

    private sealed class Connection
    {
        private const int MaxSignalBytes = 24 * 1024;
        private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(32);
        private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Active => !_stopping.Task.IsCompleted;

        public bool Post<T>(T message) => Active && _outbox.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(message));

        public void Stop() => _stopping.TrySetResult();

        public async Task RunAsync(HttpContext context, CancellationToken ended, Action<JsonElement> receive)
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, ended);
                var reader = ReadAsync(socket, receive, cancellation.Token);
                var writer = WriteAsync(socket, cancellation.Token);
                await Task.WhenAny(reader, writer, _stopping.Task);
                await cancellation.CancelAsync();
                await Task.WhenAll(reader, writer);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or IOException) { }
            finally { Stop(); }
        }

        private static async Task ReadAsync(WebSocket socket, Action<JsonElement> receive, CancellationToken cancellation)
        {
            var buffer = new byte[MaxSignalBytes];
            var length = 0;
            while (!cancellation.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellation);
                if (result.MessageType != WebSocketMessageType.Text) return;
                length += result.Count;
                if (!result.EndOfMessage)
                {
                    if (length >= buffer.Length) return;
                    continue;
                }
                using var input = JsonDocument.Parse(buffer.AsMemory(0, length));
                receive(input.RootElement);
                length = 0;
            }
        }

        private async Task WriteAsync(WebSocket socket, CancellationToken cancellation)
        {
            await foreach (var packet in _outbox.Reader.ReadAllAsync(cancellation))
                await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Text, true, cancellation);
        }
    }
}
