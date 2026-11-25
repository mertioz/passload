using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Passload.Core;

namespace Passload.Cli;

internal sealed class PortableUpdates : IAsyncDisposable
{
    private readonly Installation _installation;
    private readonly CancellationTokenSource _transferEnded = new();
    private readonly CancellationTokenSource _network;
    private readonly Task<Pending?> _pending;

    private PortableUpdates(Installation installation, CancellationToken stop)
    {
        _installation = installation;
        _network = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _pending = CheckPeriodicallyAsync();
    }

    public static PortableUpdates? Start(CancellationToken stop)
    {
        try { return FindInstallation() is { } installation ? new PortableUpdates(installation, stop) : null; }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static async Task CheckNowAsync(CancellationToken stop)
    {
        var installation = FindInstallation() ?? throw new InvalidOperationException("Utilisez l’exécutable distribué pour Linux ou macOS.");
        var asset = await GitHubUpdates.CheckAsync(installation.AssetName, stop);
        if (asset is null)
        {
            Console.WriteLine($"Passload {GitHubUpdates.CurrentVersion.ToString(3)} est à jour.");
            return;
        }

        using var package = await StageAsync(asset, installation.AssetName, stop);
        if (installation.Managed)
        {
            package.Keep = true;
            Console.WriteLine($"Installateur {asset.Version.ToString(3)} vérifié. Ouvrez-le avec le gestionnaire de paquets de votre système.");
            Console.WriteLine(package.File);
            return;
        }
        await InstallAsync(installation, package, stop);
    }

    private async Task<Pending?> CheckPeriodicallyAsync()
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(_network.Token, _transferEnded.Token);
        while (!delay.IsCancellationRequested)
        {
            try
            {
                using var check = CancellationTokenSource.CreateLinkedTokenSource(_network.Token);
                check.CancelAfter(TimeSpan.FromSeconds(5));
                var asset = await GitHubUpdates.CheckAsync(_installation.AssetName, check.Token);
                if (asset is not null)
                {
                    if (!_installation.Managed) return await StageAsync(asset, _installation.AssetName, _network.Token);
                    Console.Error.WriteLine($"Passload {asset.Version.ToString(3)} est disponible. Utilisez passload --check-update pour récupérer l’installateur.");
                    return null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException) { }

            try { await Task.Delay(TimeSpan.FromHours(1), delay.Token); }
            catch (OperationCanceledException) { break; }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _transferEnded.Cancel();
        _network.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var package = await _pending;
            if (package is not null) await InstallAsync(_installation, package, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("La mise à jour n’a pas été installée. " + ex.Message);
        }
        finally
        {
            _network.Dispose();
            _transferEnded.Dispose();
        }
    }

    private static async Task<Pending> StageAsync(ReleaseAsset asset, string name, CancellationToken stop)
    {
        Console.Error.WriteLine($"Téléchargement de Passload {asset.Version.ToString(3)}…");
        var folder = Directory.CreateTempSubdirectory("passload-update-");
        var package = new Pending(asset.Version, folder, Path.Combine(folder.FullName, name));
        try
        {
            await GitHubUpdates.DownloadAsync(asset, package.File, stop);
            return package;
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    private static async Task InstallAsync(Installation installation, Pending package, CancellationToken stop)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var changed = await ReplaceAsync(installation.Executable, installation.Original, package.File, stop);
        Console.Error.WriteLine(changed
            ? $"Passload {package.Version.ToString(3)} est installé pour le prochain lancement."
            : "L’exécutable a changé pendant la vérification. Relancez Passload pour utiliser la version installée.");
    }

    [UnsupportedOSPlatform("windows")]
    internal static async Task<bool> ReplaceAsync(string executable, FileStamp original, string archive, CancellationToken stop)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var mutex = Path.Combine(directory, $".{Path.GetFileName(executable)}.update.lock");
        using var lease = new FileStream(mutex, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (FileStamp.Read(executable) != original) return false;

        var temporary = Path.Combine(directory, $".passload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var file = File.OpenRead(archive);
            await using var compressed = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(compressed);
            while (await tar.GetNextEntryAsync(cancellationToken: stop) is { } entry)
            {
                if (entry.Length > GitHubUpdates.MaximumSize) throw new InvalidDataException("Un fichier de l’archive dépasse la taille autorisée.");
                if (entry.Name != "passload") continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is not { } input || entry.Length == 0)
                    throw new InvalidDataException("L’archive ne contient pas d’exécutable Passload valide.");
                await using (var output = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                }))
                {
                    await input.CopyToAsync(output, stop);
                    if (output.Length != entry.Length) throw new InvalidDataException("L’exécutable de l’archive est incomplet.");
                }

                const UnixFileMode permissions = (UnixFileMode)0x1ff;
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(executable) & permissions);
                stop.ThrowIfCancellationRequested();
                if (FileStamp.Read(executable) != original) return false;
                File.Move(temporary, executable, overwrite: true);
                return true;
            }
            throw new InvalidDataException("L’archive ne contient pas l’exécutable Passload.");
        }
        finally { File.Delete(temporary); }
    }

    private static Installation? FindInstallation()
    {
        var platform = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : null;
        var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        if (platform is null || arch is null || GitHubUpdates.BundlePath is not { } path) return null;
        var executable = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var directory = Path.GetDirectoryName(executable)!;
        var format = directory switch
        {
            "/usr/lib/passload" => File.ReadAllText(Path.Combine(directory, "package-format")).Trim() switch
            {
                "deb" => "deb", "rpm" => "rpm",
                _ => throw new InvalidDataException("Format de paquet Linux inconnu.")
            },
            "/usr/local/libexec/passload" => "pkg",
            _ => "tar.gz"
        };
        return new Installation(executable, $"passload-{platform}-{arch}.{format}", format != "tar.gz", FileStamp.Read(executable));
    }

    internal readonly record struct FileStamp(long Length, DateTime Modified)
    {
        public static FileStamp Read(string file)
        {
            var info = new FileInfo(file);
            return new FileStamp(info.Length, info.LastWriteTimeUtc);
        }
    }

    private sealed record Installation(string Executable, string AssetName, bool Managed, FileStamp Original);

    private sealed record Pending(Version Version, DirectoryInfo Directory, string File) : IDisposable
    {
        public bool Keep { get; set; }
        public void Dispose()
        {
            if (Keep) return;
            try { System.IO.File.Delete(File); Directory.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
