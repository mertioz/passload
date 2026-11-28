using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Passload.Core;

public sealed partial class ReceiveClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _root;

    [GeneratedRegex("^/s/[A-Za-z0-9_-]{43}/?$", RegexOptions.CultureInvariant)]
    private static partial Regex SharePath();
    [GeneratedRegex("^#sha256=([a-fA-F0-9]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex Fingerprint();

    public ReceiveClient(string link)
    {
        if (!Uri.TryCreate(ShareLinks.DirectAddress(link), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || !SharePath().IsMatch(uri.AbsolutePath) ||
            Fingerprint().Match(uri.Fragment) is not { Success: true } match)
            throw new FormatException(Locale.Text("CompleteLinkRequired"));
        var pin = Convert.FromHexString(match.Groups[1].Value);
        _root = new UriBuilder(uri) { Fragment = "", Path = uri.AbsolutePath.TrimEnd('/') + "/" }.Uri;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null &&
                    CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), pin)
            }
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }

    public async Task<Manifest> InspectAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(new Uri(_root, "manifest"), timeout.Token).ConfigureAwait(false);
        CheckStatus(response);
        var manifest = await response.Content.ReadFromJsonAsync<Manifest>(timeout.Token).ConfigureAwait(false);
        if (manifest?.Files is not { Length: > 0 } || manifest.Files.Where((f, i) =>
            f is null || f.Id != i || string.IsNullOrWhiteSpace(f.Name) || f.Size < 0).Any())
            throw new InvalidDataException(Locale.Text("InvalidManifest"));
        return manifest;
    }

    public async Task DownloadAsync(FileItem? item, string destination, IProgress<long>? progress, CancellationToken cancellation, bool overwrite = false)
    {
        var target = Path.GetFullPath(destination);
        var part = Path.Combine(Path.GetDirectoryName(target)!, ".passload-" + Guid.NewGuid().ToString("N") + ".part");
        var address = new Uri(_root, item is null ? "all.zip" : $"files/{item.Id}");
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        CheckStatus(response);
        var ownsPart = false;
        try
        {
            long received = 0;
            using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                ownsPart = true;
                await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                var buffer = new byte[65536];
                var update = Stopwatch.StartNew();
                int count;
                while ((count = await body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    received += count;
                    if (update.ElapsedMilliseconds >= 150) { progress?.Report(received); update.Restart(); }
                }
                await file.FlushAsync(cancellation).ConfigureAwait(false);
            }
            if (item is not null && received != item.Size) throw new IOException(Locale.Text("TransferInterrupted"));
            if (OperatingSystem.IsWindows())
            {
                // Preserve Windows' normal origin marker without recording the private share URL.
                try { await File.WriteAllTextAsync(part + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", cancellation).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or NotSupportedException) { /* Filesystems such as FAT do not support alternate streams. */ }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(part, target, overwrite);
            ownsPart = false;
            progress?.Report(received);
        }
        finally { if (ownsPart) File.Delete(part); }
    }

    public static string SuggestedName(string remoteName)
    {
        var name = Path.GetFileName(remoteName.Replace('\\', '/')).Trim().TrimEnd('.');
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c));
        if (name.Length > 200) name = name[..200];
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "NUL" or "AUX" or "PRN" || Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$")) name = "_" + name;
        return string.IsNullOrWhiteSpace(name) ? "fichier" : name;
    }

    private static void CheckStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException(Locale.Text("ShareEnded"));
        if (response.StatusCode == HttpStatusCode.Conflict) throw new IOException(Locale.Text("SenderFileChanged"));
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException(Locale.Text("SenderResponseCode", (int)response.StatusCode));
    }

    public void Dispose() => _http.Dispose();
}
