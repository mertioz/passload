using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Passload.Core;

public enum ShareScope { Internet, Local }

public sealed record SharedFile(int Id, string Path, string Name, string ZipName, long Size, DateTime ModifiedUtc)
{
    public FileStream Open()
    {
        var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != Size || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != ModifiedUtc)
                throw new IOException("Le fichier a changé depuis la création du lien.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<SharedFile> Select(IEnumerable<string> paths)
    {
        var files = new List<SharedFile>();
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entry = name;
            var suffix = 2;
            while (!archiveNames.Add(entry))
                entry = $"{System.IO.Path.GetFileNameWithoutExtension(name)} ({suffix++}){System.IO.Path.GetExtension(name)}";
            files.Add(new(files.Count, path, name, entry, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle)));
        }
        if (files.Count == 0) throw new InvalidOperationException("Choisissez au moins un fichier.");
        return files.AsReadOnly();
    }
}

public sealed record FileItem(int Id, string Name, long Size);
public sealed record Manifest(DateTimeOffset ExpiresAt, FileItem[] Files);

public sealed class Share
{
    private readonly CancellationTokenSource _end = new();
    public CancellationToken Ended { get; }
    public string Token { get; } = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public IReadOnlyList<SharedFile> Files { get; }
    public DateTimeOffset ExpiresAt { get; }
    public ShareScope Scope { get; }
    public string RouteLabel { get; }
    public Uri Link { get; }
    public bool UsesPublicWebRtc => RtcRoom is not null;
    internal PublicRtcRoom? RtcRoom { get; }
    public bool UsesRouter { get; }
    public bool Active => !Ended.IsCancellationRequested && DateTimeOffset.UtcNow < ExpiresAt;
    public long TotalSize { get; }

    internal Share(IReadOnlyList<SharedFile> files, TimeSpan duration, ShareScope scope,
        Uri origin, string fingerprint, string routeLabel, bool usesRouter, Uri? publicLink, bool publicWebRtc)
    {
        if (files.Count == 0 || duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(duration));
        Files = files;
        TotalSize = files.Sum(f => f.Size);
        ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        Ended = _end.Token;
        _end.CancelAfter(duration);
        Scope = scope;
        RouteLabel = routeLabel;
        UsesRouter = usesRouter;
        RtcRoom = publicWebRtc ? new PublicRtcRoom() : null;
        Link = publicLink ?? new Uri(origin, $"/s/{Token}#sha256={fingerprint}");
    }

    internal void End()
    {
        _end.Cancel();
        _end.Dispose();
    }
}

public sealed class ShareCatalog
{
    private readonly ConcurrentDictionary<string, Share> _shares = new(StringComparer.Ordinal);
    public Share[] Active => _shares.Values.Where(s => s.Active).OrderBy(s => s.ExpiresAt).ToArray();

    public Share Add(IReadOnlyList<SharedFile> files, TimeSpan duration, ShareScope scope,
        Uri origin, string fingerprint, string routeLabel, bool usesRouter = false, Uri? publicLink = null, bool publicWebRtc = false)
    {
        var share = new Share(files, duration, scope, origin, fingerprint, routeLabel, usesRouter, publicLink, publicWebRtc);
        if (_shares.TryAdd(share.Token, share)) return share;
        share.End();
        throw new InvalidOperationException("Impossible de générer un lien unique.");
    }

    internal Share? Find(string token) =>
        _shares.TryGetValue(token, out var share) && share.Active ? share : null;

    public void Revoke(string token)
    {
        if (_shares.TryRemove(token, out var share)) share.End();
    }

    public void Collect()
    {
        foreach (var pair in _shares)
            if (!pair.Value.Active) Revoke(pair.Key);
    }

    public void EndAll()
    {
        foreach (var token in _shares.Keys) Revoke(token);
    }
}

public static class Display
{
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To", "Po"];
    public static string Size(long bytes)
    {
        double number = bytes;
        var unit = 0;
        while (number >= 1024 && unit < Units.Length - 1) { number /= 1024; unit++; }
        return number.ToString(number < 10 && unit > 0 ? "0.#" : "0", CultureInfo.GetCultureInfo("fr-FR")) + " " + Units[unit];
    }
    public static string Count(int count) => count == 1 ? "1 fichier" : $"{count} fichiers";
}
