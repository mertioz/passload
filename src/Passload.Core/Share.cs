using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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
public sealed record Manifest(DateTimeOffset ExpiresAt, FileItem[] Files, string? Name = null, string? Description = null);
public sealed record ShareDetails(string? Title, string? Description);

public sealed class Share
{
    private readonly CancellationTokenSource _end = new();
    private readonly DownloadHistory _history;
    private readonly DownloadRanges _ranges = new();
    private readonly object _detailsLock = new();
    private ShareDetails _details = new(null, null);
    private readonly WebRtcInvitation? _invitation;
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 1200;
    public CancellationToken Ended { get; }
    public Guid HistoryId { get; } = Guid.NewGuid();
    public ShareDetails Details => Volatile.Read(ref _details);
    public string DefaultName => Files.Count == 1 ? Files[0].Name : $"{Files[0].Name} +{Files.Count - 1}";
    public string Name => Details.Title ?? DefaultName;
    public string Token { get; } = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public IReadOnlyList<SharedFile> Files { get; }
    public DateTimeOffset ExpiresAt { get; }
    public ShareScope Scope { get; }
    public string RouteLabel { get; }
    public Uri Link { get; }
    public Uri PublishedLink { get; }
    public bool UsesTrustedHttps { get; }
    public bool UsesPublicWebRtc => RtcRoom is not null;
    internal PublicRtcRoom? RtcRoom { get; }
    public bool UsesRouter { get; }
    public bool Active => !Ended.IsCancellationRequested && DateTimeOffset.UtcNow < ExpiresAt;
    public long TotalSize { get; }

    internal Share(IReadOnlyList<SharedFile> files, TimeSpan duration, ShareScope scope,
        Uri origin, string fingerprint, string routeLabel, bool usesRouter, bool publicWebRtc, DownloadHistory history,
        bool trustedHttps)
    {
        if (files.Count == 0 || duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(duration));
        Files = files;
        _history = history;
        TotalSize = files.Sum(f => f.Size);
        ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        Ended = _end.Token;
        _end.CancelAfter(duration);
        Scope = scope;
        RouteLabel = routeLabel;
        UsesRouter = usesRouter;
        UsesTrustedHttps = trustedHttps;
        RtcRoom = publicWebRtc ? new PublicRtcRoom() : null;
        Link = new Uri(origin, $"/s/{Token}#sha256={fingerprint}");
        if (publicWebRtc && scope == ShareScope.Internet) _invitation = WebRtcInvitation.Create();
        PublishedLink = scope == ShareScope.Internet ? ShareLinks.Publish(Link, ExpiresAt, _invitation?.Room, _invitation?.AccessKey) : Link;
    }

    public object WebRtcStartMessage(bool sharedPage)
    {
        var until = ExpiresAt.ToUnixTimeMilliseconds();
        if (sharedPage && _invitation is { } invite)
            return new { op = "start", token = Token, until, room = invite.Room, key = invite.AccessKey, owner = invite.OwnerKey };
        return new { op = "start", token = Token, until, silent = !sharedPage && _invitation is not null };
    }

    public void UpdateDetails(string? title, string? description)
    {
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (title is not null && (title.Length > MaxTitleLength || title.Any(char.IsControl)))
            throw new ArgumentException($"Le nom doit tenir sur une ligne de {MaxTitleLength} caractères maximum.");
        if (description is not null && (description.Length > MaxDescriptionLength ||
            description.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))))
            throw new ArgumentException($"La description doit contenir au maximum {MaxDescriptionLength} caractères.");

        lock (_detailsLock)
        {
            var changedTitle = _details.Title != title;
            Volatile.Write(ref _details, new ShareDetails(title, description));
            if (changedTitle) _history.RenameShare(HistoryId, title ?? DefaultName);
        }
    }

    internal void RecordHttpsDownload(int file, IPAddress? remote, long? from = null, long? to = null)
    {
        var address = DownloadAddress.Normalize(remote);
        if (address is not null && IPAddress.IsLoopback(address)) return;
        var now = DateTimeOffset.UtcNow;
        var identity = address?.ToString();
        if (file >= 0 && from is long start && to is long last)
        {
            var length = Files[file].Size;
            if (start < 0 || last < start || last >= length) return;
            if (start != 0 || last + 1 != length)
            {
                if (identity is not null && _ranges.IsComplete(identity, file, start, last + 1, length, now))
                    RecordDownload(file, identity, DownloadTransport.Https, now);
                return;
            }
        }
        if (file >= 0 && identity is not null) _ranges.Clear(identity, file);
        RecordDownload(file, identity, DownloadTransport.Https, now);
    }

    public void RecordWebRtcDownload(int file, string? address, bool relayed)
    {
        var ip = IPAddress.TryParse(address, out var parsed) ? DownloadAddress.Normalize(parsed) : null;
        if (ip is not null && IPAddress.IsLoopback(ip)) return;
        RecordDownload(file, ip?.ToString(), relayed ? DownloadTransport.EncryptedRelay : DownloadTransport.WebRtc,
            DateTimeOffset.UtcNow);
    }

    private void RecordDownload(int file, string? address, DownloadTransport transport, DateTimeOffset now)
    {
        if (file < -1 || file >= Files.Count) return;
        var name = file == -1 ? "Passload.zip" : Files[file].Name;
        lock (_detailsLock) _history.Record(new DownloadRecord(HistoryId, Name, name, now, address, transport));
    }

    internal void End()
    {
        _end.Cancel();
        _end.Dispose();
    }
}

public sealed class ShareCatalog(DownloadHistory? history = null)
{
    private readonly ConcurrentDictionary<string, Share> _shares = new(StringComparer.Ordinal);
    public DownloadHistory History { get; } = history ?? new DownloadHistory();
    public Share[] Active => _shares.Values.Where(s => s.Active).OrderBy(s => s.ExpiresAt).ToArray();

    public Share Add(IReadOnlyList<SharedFile> files, TimeSpan duration, ShareScope scope,
        Uri origin, string fingerprint, string routeLabel, bool usesRouter = false, bool publicWebRtc = false,
        bool trustedHttps = false)
    {
        var share = new Share(files, duration, scope, origin, fingerprint, routeLabel, usesRouter, publicWebRtc, History,
            trustedHttps);
        if (_shares.TryAdd(share.Token, share)) return share;
        share.End();
        throw new InvalidOperationException("Impossible de générer un lien unique.");
    }

    public Share? Find(string token) =>
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
