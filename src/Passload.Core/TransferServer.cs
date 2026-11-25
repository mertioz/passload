using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Passload.Core;

public sealed class TransferServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2? _certificate;
    private static readonly string Template = LoadTemplate();
    public string Fingerprint => _certificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? "";
    public int Port { get; }

    private TransferServer(WebApplication app, X509Certificate2? certificate)
    {
        _app = app;
        _certificate = certificate;
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Port = new Uri(addresses.Single()).Port;
    }

    public static async Task<TransferServer> StartAsync(ShareCatalog catalog, int port, IEnumerable<IPAddress> addresses,
        bool loopbackOnly = false, CancellationToken cancellation = default, bool browserEngine = false,
        Func<string, Share?>? publicShares = null)
    {
        if (browserEngine && !loopbackOnly) throw new ArgumentException("Le moteur du navigateur doit rester sur la boucle locale.");
        var certificate = browserEngine ? null : LocalIdentity.Create(addresses);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxConcurrentConnections = 96;
            server.Limits.MaxRequestBodySize = 0;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(12);
            if (browserEngine) server.Listen(IPAddress.Loopback, port);
            else if (loopbackOnly) server.Listen(IPAddress.Loopback, port, endpoint => endpoint.UseHttps(certificate!));
            else server.ListenAnyIP(port, endpoint => endpoint.UseHttps(certificate!));
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        Share? Resolve(string token) => catalog.Find(token) ?? publicShares?.Invoke(token);
        app.Run(ctx => browserEngine ? ServeEngine(ctx, Resolve) : ServeAsync(ctx, catalog.Find));
        try
        {
            await app.StartAsync(cancellation).ConfigureAwait(false);
            return new TransferServer(app, certificate);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            certificate?.Dispose();
            throw;
        }
    }

    private static async Task ServeEngine(HttpContext ctx, Func<string, Share?> resolve)
    {
        if (ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != ctx.Connection.LocalPort)
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        var path = ctx.Request.Path.Value;
        const string hostPrefix = "/engine/rtc/";
        if (path is not null && path.StartsWith(hostPrefix, StringComparison.Ordinal))
        {
            var token = path[hostPrefix.Length..];
            if (token.Length == 43 && resolve(token) is { RtcRoom: { } room } share)
                await room.RunHostAsync(ctx, share.Ended);
            else ctx.Response.StatusCode = 404;
            return;
        }
        string? asset = path switch
        {
            "/engine/host.js" => "host.js",
            "/engine/wire.js" or "/recevoir/wire.js" => "wire.js",
            "/engine/pipe.js" or "/recevoir/pipe.js" => "pipe.js",
            "/engine/socket-meeting.js" => "socket-meeting.js",
            "/recevoir/" => "index.html",
            "/recevoir/style.css" => "style.css",
            "/recevoir/receive.js" => "receive.js",
            "/recevoir/incoming.js" => "incoming.js",
            "/recevoir/save.js" => "save.js",
            "/recevoir/disk.js" => "disk.js",
            _ => null
        };
        if (path != "/engine/host" && asset is null)
        {
            await ServeAsync(ctx, resolve);
            return;
        }
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' ws://127.0.0.1:* https://ppng.io; worker-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        if (path == "/engine/host")
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>Passload</title><script type=module src='/engine/host.js'></script>");
            return;
        }
        await ServeAssetAsync(ctx, asset!);
    }

    private static async Task ServeAssetAsync(HttpContext ctx, string asset)
    {
        ctx.Response.ContentType = asset switch { "style.css" => "text/css", "index.html" => "text/html; charset=utf-8", _ => "text/javascript; charset=utf-8" };
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        using var resource = typeof(TransferServer).Assembly.GetManifestResourceStream("Passload." + asset)!;
        await resource.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private static async Task ServeAsync(HttpContext ctx, Func<string, Share?> resolve)
    {
        var response = ctx.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        var head = HttpMethods.IsHead(ctx.Request.Method);
        var upgrade = ctx.WebSockets.IsWebSocketRequest;
        // An HTTP/2 WebSocket handshake uses CONNECT. Only the share's signalling route can accept it.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !head && !upgrade)
        {
            response.Headers.Allow = "GET, HEAD";
            response.StatusCode = 405;
            return;
        }
        var parts = (ctx.Request.Path.Value ?? "").Split('/');
        if (parts.Length is < 3 or > 5 || parts[1] != "s" || parts[2].Length != 43 ||
            resolve(parts[2]) is not { } share ||
            (share.Scope == ShareScope.Local && !NetworkInfo.IsLocalPeer(ctx.Connection.RemoteIpAddress)))
        {
            await Unavailable(ctx);
            return;
        }
        using var abort = share.Ended.Register(ctx.Abort);
        try
        {
            if (parts.Length == 4 && parts[3] == "rtc" && share.RtcRoom is { } room)
            {
                await room.RunGuestAsync(ctx, share.Ended);
                return;
            }
            if (upgrade)
            {
                response.StatusCode = 404;
                return;
            }
            if (parts.Length == 3 || (parts.Length == 4 && parts[3] == ""))
            {
                if (share.UsesPublicWebRtc)
                {
                    var socket = (ctx.Request.IsHttps ? "wss://" : "ws://") + ctx.Request.Host;
                    response.Headers["Content-Security-Policy"] = $"default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; worker-src 'self'; connect-src 'self' {socket}; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                }
                await Html(ctx, 200, Landing(share));
                return;
            }
            if (share.UsesPublicWebRtc && parts.Length == 5 && parts[3] == "assets" &&
                parts[4] is "public-receive.js" or "incoming.js" or "socket-meeting.js" or "wire.js" or "save.js" or "disk.js")
            {
                await ServeAssetAsync(ctx, parts[4]);
                return;
            }
            if (parts.Length == 4 && parts[3] == "manifest")
            {
                response.ContentType = "application/json; charset=utf-8";
                if (!head) await response.WriteAsJsonAsync(new Manifest(share.ExpiresAt, share.Files.Select(f => new FileItem(f.Id, f.Name, f.Size)).ToArray()), cancellationToken: ctx.RequestAborted);
                return;
            }
            if (parts.Length == 4 && parts[3] == "all.zip")
            {
                await SendZip(ctx, share, head);
                return;
            }
            if (parts.Length == 5 && parts[3] == "files" &&
                int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0 && id < share.Files.Count)
            {
                var file = share.Files[id];
                await using var stream = file.Open();
                await Results.File(stream, "application/octet-stream", file.Name,
                    new DateTimeOffset(file.ModifiedUtc), enableRangeProcessing: true).ExecuteAsync(ctx);
                return;
            }
            await Unavailable(ctx);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (response.HasStarted || ctx.RequestAborted.IsCancellationRequested || share.Ended.IsCancellationRequested)
                ctx.Abort();
            else
            {
                response.ContentLength = null;
                response.Headers.Remove(HeaderNames.ContentDisposition);
                await Html(ctx, 409, ErrorPage("Fichier indisponible", "Un fichier a été déplacé, modifié ou verrouillé. Demandez un nouveau lien à l’expéditeur."));
            }
        }
    }

    private static async Task SendZip(HttpContext ctx, Share share, bool head)
    {
        // Check the complete selection before committing attachment headers.
        foreach (var file in share.Files) { using var check = file.Open(); }
        ctx.Response.ContentType = "application/zip";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName("Passload.zip");
        ctx.Response.GetTypedHeaders().ContentDisposition = disposition;
        if (head) return;
        var token = ctx.RequestAborted;
        // ZipArchive writes small entry descriptors synchronously even when OpenAsync is used.
        // Enable this only for the ZIP response. File contents still stream asynchronously.
        if (ctx.Features.Get<IHttpBodyControlFeature>() is { } io) io.AllowSynchronousIO = true;
        await ctx.Response.StartAsync(token);
        await using var archive = await ZipArchive.CreateAsync(ctx.Response.Body, ZipArchiveMode.Create, true, Encoding.UTF8, token);
        foreach (var file in share.Files)
        {
            token.ThrowIfCancellationRequested();
            await using var input = file.Open();
            var entry = archive.CreateEntry(file.ZipName, CompressionLevel.NoCompression);
            await using var output = await entry.OpenAsync(token);
            await input.CopyToAsync(output, 65536, token);
        }
    }

    private static Task Unavailable(HttpContext ctx) => Html(ctx, 404, ErrorPage("Lien indisponible",
        "Ce lien a expiré, a été désactivé ou ne correspond à aucun partage. Demandez un nouveau lien à l’expéditeur."));

    private static async Task Html(HttpContext ctx, int status, string page)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.WriteAsync(page, ctx.RequestAborted);
    }

    private static string Landing(Share share)
    {
        var basePath = "/s/" + share.Token;
        var items = new StringBuilder();
        foreach (var file in share.Files)
        {
            var name = WebUtility.HtmlEncode(file.Name);
            var rtc = share.UsesPublicWebRtc ? $" data-item='{file.Id}' data-size='{file.Size}' data-name='{name}'" : "";
            items.Append($"<li><div><b>{name}</b><small>{Display.Size(file.Size)}</small></div><a class='pick' href='{basePath}/files/{file.Id}' aria-label='Télécharger {name}'{rtc}>Télécharger</a></li>");
        }
        var single = share.Files.Count == 1;
        var title = single ? "Un fichier partagé" : $"{share.Files.Count} fichiers partagés";
        var expiry = share.ExpiresAt.UtcDateTime.ToString("dd MMM yyyy 'à' HH:mm 'UTC'", CultureInfo.GetCultureInfo("fr-FR"));
        var zipRtc = share.UsesPublicWebRtc ? $" data-item='zip' data-size='{share.TotalSize}' data-name='Passload.zip'" : "";
        var bundle = single ? "" : $"<a class='main' href='{basePath}/all.zip'{zipRtc}>Tout télécharger en ZIP</a>";
        var status = share.UsesPublicWebRtc ? "<p id='status' role='status'>Téléchargement HTTPS disponible.</p><a id='saved' class='note' hidden></a>" : "";
        var script = share.UsesPublicWebRtc ? $"<script type='module' src='{basePath}/assets/public-receive.js'></script>" : "";
        var body = $"<h1>{title}</h1><p>{Display.Size(share.TotalSize)}</p>{status}<ul>{items}</ul>{bundle}<p class='note'>Expire le {expiry}</p>{script}";
        return Template.Replace("<!--content-->", body);
    }

    private static string ErrorPage(string title, string message) =>
        Template.Replace("<!--content-->", $"<h1>{title}</h1><p class='intro'>{message}</p>");

    private static string LoadTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Passload.Core.Recipient.html")!;
        using var reader = new StreamReader(stream);
        using var css = Assembly.GetExecutingAssembly().GetManifestResourceStream("Passload.style.css")!;
        using var theme = new StreamReader(css);
        return reader.ReadToEnd().Replace("<!--theme-->", theme.ReadToEnd());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _certificate?.Dispose();
        }
    }
}
