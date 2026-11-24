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
    private readonly X509Certificate2 _certificate;
    private static readonly string Template = LoadTemplate();
    public string Fingerprint => _certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public int Port { get; }

    private TransferServer(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        _certificate = certificate;
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Port = new Uri(addresses.Single()).Port;
    }

    public static async Task<TransferServer> StartAsync(ShareCatalog catalog, int port, IEnumerable<IPAddress> addresses,
        bool loopbackOnly = false, CancellationToken cancellation = default)
    {
        var certificate = LocalIdentity.Create(addresses);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxConcurrentConnections = 96;
            server.Limits.MaxRequestBodySize = 0;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(12);
            if (loopbackOnly) server.Listen(IPAddress.Loopback, port, endpoint => endpoint.UseHttps(certificate));
            else server.ListenAnyIP(port, endpoint => endpoint.UseHttps(certificate));
        });
        var app = builder.Build();
        app.Run(ctx => ServeAsync(ctx, catalog));
        try
        {
            await app.StartAsync(cancellation).ConfigureAwait(false);
            return new TransferServer(app, certificate);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            certificate.Dispose();
            throw;
        }
    }

    private static async Task ServeAsync(HttpContext ctx, ShareCatalog catalog)
    {
        var response = ctx.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        var head = HttpMethods.IsHead(ctx.Request.Method);
        if (!HttpMethods.IsGet(ctx.Request.Method) && !head)
        {
            response.Headers.Allow = "GET, HEAD";
            response.StatusCode = 405;
            return;
        }
        var parts = (ctx.Request.Path.Value ?? "").Split('/');
        if (parts.Length is < 3 or > 5 || parts[1] != "s" || parts[2].Length != 43 ||
            catalog.Find(parts[2]) is not { } share ||
            (share.Scope == ShareScope.Local && !NetworkInfo.IsLocalPeer(ctx.Connection.RemoteIpAddress)))
        {
            await Unavailable(ctx);
            return;
        }
        using var abort = share.Ended.Register(ctx.Abort);
        try
        {
            if (parts.Length == 3 || (parts.Length == 4 && parts[3] == ""))
            {
                await Html(ctx, 200, Landing(share));
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
            var ext = Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant();
            if (ext.Length > 6) ext = ext[..6];
            items.Append($"<li><span class='ext'>{WebUtility.HtmlEncode(ext.Length == 0 ? "FIC" : ext)}</span><div><b>{WebUtility.HtmlEncode(file.Name)}</b><small>{Display.Size(file.Size)}</small></div><a class='pick' href='{basePath}/files/{file.Id}' aria-label='Télécharger {WebUtility.HtmlEncode(file.Name)}'>Télécharger <span aria-hidden='true'>↓</span></a></li>");
        }
        var single = share.Files.Count == 1;
        var title = single ? "Un fichier vous attend." : "Des fichiers vous attendent.";
        var expiry = share.ExpiresAt.UtcDateTime.ToString("dd MMM yyyy 'à' HH:mm 'UTC'", CultureInfo.GetCultureInfo("fr-FR"));
        var body = $"<p class='meta'>{Display.Count(share.Files.Count)} <i>·</i> {Display.Size(share.TotalSize)}</p><h1>{title}</h1><p class='intro'>Téléchargez directement depuis l’ordinateur de l’expéditeur.</p><a class='main' href='{basePath}/{(single ? "files/0" : "all.zip")}'>{(single ? "Télécharger le fichier" : "Tout télécharger en ZIP")} <span>↓</span></a><ul>{items}</ul><footer>Disponible jusqu’au {expiry}</footer>";
        return Template.Replace("<!--content-->", body);
    }

    private static string ErrorPage(string title, string message) =>
        Template.Replace("<!--content-->", $"<h1>{title}</h1><p class='intro'>{message}</p>");

    private static string LoadTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Passload.Core.Recipient.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
            _certificate.Dispose();
        }
    }
}
