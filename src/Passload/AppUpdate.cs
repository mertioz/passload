using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;
using Passload.Core;

namespace Passload;

internal sealed record UpdatePackage(Version Version, string File, string Hash);

internal static class AppUpdate
{
    private const string Asset = "Passload.exe";
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Passload", "updates");
    public static Version Version => GitHubUpdates.CurrentVersion;
    public static bool Available => !WindowsPackage.HasIdentity && GitHubUpdates.BundlePath is not null;

    public static async Task<UpdatePackage?> CheckAsync(CancellationToken stop)
    {
        if (!Available) return null;
        var asset = await GitHubUpdates.CheckAsync(Asset, stop).ConfigureAwait(false);
        if (asset is null) return null;
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"Passload-{Guid.NewGuid():N}.exe");
        await GitHubUpdates.DownloadAsync(asset, file, stop).ConfigureAwait(false);
        return new UpdatePackage(asset.Version, file, asset.Sha256);
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
            RefreshInstalledVersion(target);
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

    private static void RefreshInstalledVersion(string executable)
    {
        try
        {
            using var registration = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mertioz.Passload_is1", writable: true);
            if (registration?.GetValue("InstallLocation") is string location &&
                Path.TrimEndingDirectorySeparator(location).Equals(Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
                registration.SetValue("DisplayVersion", Version.ToString(3));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }
}
