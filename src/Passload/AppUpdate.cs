using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Passload;

internal sealed record UpdatePackage(Version Version, string File, string Hash);

internal static class AppUpdate
{
    private const string Endpoint = "https://api.github.com/repos/mertioz/passload/releases/latest";
    private const string Downloads = "https://github.com/mertioz/passload/releases/download/";
    private const string Asset = "Passload.exe";
    private const long MaximumSize = 256 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Passload", "updates");
    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version!;
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty location identifies the bundled executable that can update itself.")]
    public static bool Available => Assembly.GetExecutingAssembly().Location.Length == 0;

    static AppUpdate() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Passload/" + Version.ToString(3));

    public static async Task<UpdatePackage?> CheckAsync(CancellationToken stop)
    {
        if (!Available) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var metadata = await Http.SendAsync(request, stop).ConfigureAwait(false);
        metadata.EnsureSuccessStatusCode();
        var release = await metadata.Content.ReadFromJsonAsync<Release>(cancellationToken: stop).ConfigureAwait(false);
        if (release?.Tag is not { } tag || !tag.StartsWith('v') || !System.Version.TryParse(tag[1..], out var parsed))
            throw new InvalidDataException("La version publiée sur GitHub est illisible.");
        var next = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        if (next <= Version) return null;

        var asset = release.Assets?.SingleOrDefault(item => item.Name == Asset);
        if (asset is not { Size: > 0 and <= MaximumSize } || asset.Digest is not { Length: 71 } digest ||
            !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("L’exécutable et son empreinte SHA-256 sont requis dans la version publiée.");

        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"Passload-{Guid.NewGuid():N}.exe");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var download = await Http.GetAsync(Downloads + Uri.EscapeDataString(tag) + "/" + Asset,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            download.EnsureSuccessStatusCode();
            await using var input = await download.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    length += count;
                    if (length > asset.Size) throw new InvalidDataException("L’exécutable téléchargé dépasse la taille annoncée.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                if (length != asset.Size) throw new InvalidDataException("Le téléchargement de la mise à jour est incomplet.");
            }
            await using var content = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(content, timeout.Token).ConfigureAwait(false));
            if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("L’empreinte SHA-256 de la mise à jour est incorrecte.");
            return new UpdatePackage(next, file, actual);
        }
        catch
        {
            Delete(file);
            throw;
        }
    }

    public static async Task StartInstallerAsync(UpdatePackage update, bool reopen, CancellationToken stop)
    {
        var marker = Path.ChangeExtension(update.File, ".ready");
        var apply = Path.ChangeExtension(update.File, ".apply");
        Delete(marker);
        Delete(apply);
        await using var file = File.OpenRead(update.File);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, stop));
        if (digest != update.Hash) throw new InvalidDataException("Le fichier de mise à jour a changé après son téléchargement.");

        var start = new ProcessStartInfo(update.File) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--install-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(update.Hash);
        start.ArgumentList.Add(reopen ? "show" : "stay-closed");
        using var installer = Process.Start(start) ?? throw new IOException("Impossible de lancer la mise à jour.");
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            if (installer.HasExited || elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new IOException("Impossible de préparer la mise à jour. Vérifiez que le dossier de Passload est accessible en écriture.");
            await Task.Delay(100, stop);
        }
        stop.ThrowIfCancellationRequested();
        File.WriteAllText(apply, "");
    }

    public static int Install(int previousId, string hash, bool reopen)
    {
        var source = Environment.ProcessPath!;
        if (!Path.GetDirectoryName(source)!.Equals(Folder, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileNameWithoutExtension(source).StartsWith("Passload-", StringComparison.Ordinal)) return 1;

        var marker = Path.ChangeExtension(source, ".ready");
        var apply = Path.ChangeExtension(source, ".apply");
        string? target = null, temporary = null;
        try
        {
            using var previous = Process.GetProcessById(previousId);
            var original = previous.MainModule?.FileName;
            if (original is null || original.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                FileVersionInfo.GetVersionInfo(original).ProductName != "Passload") return 1;

            using (var file = File.OpenRead(source))
                if (Convert.ToHexString(SHA256.HashData(file)) != hash) return 1;
            temporary = Path.Combine(Path.GetDirectoryName(original)!, $".passload-{Guid.NewGuid():N}.exe.tmp");
            File.Copy(source, temporary);
            File.WriteAllText(marker, "");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(apply))
            {
                if (previous.HasExited || elapsed.Elapsed > TimeSpan.FromSeconds(35)) return 1;
                Thread.Sleep(100);
            }
            if (!previous.WaitForExit(45_000)) return 1;
            target = original;
            File.Move(temporary, target, overwrite: true);
            if (reopen) Restart(target, "--updated");
            return 0;
        }
        catch
        {
            if (reopen && target is not null)
                try { Restart(target, "--update-incomplete"); } catch { }
            return 1;
        }
        finally
        {
            if (temporary is not null) Delete(temporary);
            Delete(marker);
            Delete(apply);
        }
    }

    public static void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.EnumerateFiles(Folder, "Passload-*"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Delete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Restart(string path, string status)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        start.ArgumentList.Add(status);
        using var restarted = Process.Start(start);
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, ReleaseAsset[]? Assets);
    private sealed record ReleaseAsset(string Name, long Size, string? Digest);
}
