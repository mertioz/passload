using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Passload.Core;

public sealed record ReleaseAsset(Version Version, Uri Address, long Size, string Sha256);

public static class GitHubUpdates
{
    private const string Latest = "https://api.github.com/repos/mertioz/passload/releases/latest";
    private const string Downloads = "https://github.com/mertioz/passload/releases/download/";
    public const long MaximumSize = 256 * 1024 * 1024;
    public static Version CurrentVersion { get; } = Assembly.GetEntryAssembly()!.GetName().Version!;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static GitHubUpdates() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Passload/" + CurrentVersion.ToString(3));

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty location identifies an executable that was published as a bundle.")]
    public static string? BundlePath => Assembly.GetEntryAssembly()?.Location == "" ? Environment.ProcessPath : null;

    public static async Task<ReleaseAsset?> CheckAsync(string assetName, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var metadata = await Http.SendAsync(request, stop).ConfigureAwait(false);
        metadata.EnsureSuccessStatusCode();
        var release = await metadata.Content.ReadFromJsonAsync<Release>(cancellationToken: stop).ConfigureAwait(false);
        if (release?.Tag is not { } tag || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var parsed))
            throw new InvalidDataException("La version publiée sur GitHub est illisible.");
        var next = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        if (next <= CurrentVersion) return null;

        var asset = release.Assets?.SingleOrDefault(item => item.Name == assetName);
        if (asset is not { Size: > 0 and <= MaximumSize } || asset.Digest is not { Length: 71 } digest ||
            !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Le fichier de cette plateforme et son empreinte SHA-256 sont requis dans la version publiée.");

        var address = new Uri(Downloads + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(assetName));
        return new ReleaseAsset(next, address, asset.Size, digest[7..].ToUpperInvariant());
    }

    public static async Task DownloadAsync(ReleaseAsset asset, string destination, CancellationToken stop)
    {
        var created = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var download = await Http.GetAsync(asset.Address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            download.EnsureSuccessStatusCode();
            await using var input = await download.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                var buffer = new byte[64 * 1024];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    length += count;
                    if (length > asset.Size) throw new InvalidDataException("Le fichier téléchargé dépasse la taille annoncée.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                if (length != asset.Size) throw new InvalidDataException("Le téléchargement de la mise à jour est incomplet.");
            }
            await using var content = File.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(content, timeout.Token).ConfigureAwait(false));
            if (actual != asset.Sha256) throw new InvalidDataException("L’empreinte SHA-256 de la mise à jour est incorrecte.");
        }
        catch
        {
            if (created) File.Delete(destination);
            throw;
        }
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, Asset[]? Assets);
    private sealed record Asset(string Name, long Size, string? Digest);
}
