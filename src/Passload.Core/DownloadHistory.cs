using System.Net;
using System.Text.Json;

namespace Passload.Core;

public enum DownloadTransport { Https, WebRtc, EncryptedRelay }

public sealed record DownloadRecord(Guid ShareId, string ShareName, string FileName, DateTimeOffset At,
    string? Address, DownloadTransport Transport);

public sealed class DownloadHistory
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const int Limit = 5000;
    private const int RewriteInterval = 100;
    private readonly object _lock = new();
    private readonly List<DownloadRecord> _records = [];
    private readonly string? _path;
    private readonly string? _previousPath;
    private List<DownloadRecord> _previous = [];
    private (long Size, DateTime Timestamp)? _previousStamp;
    private bool _needsRewrite;
    private int _writesSinceRewrite = RewriteInterval;
    private long _revision;
    public long Revision
    {
        get
        {
            lock (_lock)
            {
                RefreshPrevious();
                return _revision;
            }
        }
    }
    public bool PersistenceUnavailable { get; private set; }

    public DownloadHistory(string? path = null, string? previousPath = null)
    {
        _path = path is null ? null : Path.GetFullPath(path);
        _previousPath = previousPath is null ? null : Path.GetFullPath(previousPath);
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            _needsRewrite = ReadLog(_path, _records);
            if (_needsRewrite) Rewrite();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceUnavailable = true; }
    }

    public DownloadRecord[] Recent()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        lock (_lock)
        {
            RefreshPrevious();
            return _records.Concat(_previous).Where(item => item.At >= cutoff).OrderByDescending(item => item.At).Take(Limit).ToArray();
        }
    }

    private void RefreshPrevious()
    {
        if (_previousPath is null) return;
        try
        {
            var file = new FileInfo(_previousPath);
            (long, DateTime)? stamp = file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
            if (stamp == _previousStamp) return;
            // A previous Passload version can still be serving links. Its log stays read-only here.
            var next = new List<DownloadRecord>();
            if (stamp is not null) ReadLog(file.FullName, next);
            _previous = next;
            _previousStamp = stamp;
            _revision++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool ReadLog(string path, List<DownloadRecord> records)
    {
        var changed = false;
        var cutoff = DateTimeOffset.UtcNow - Retention;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<DownloadRecord>(line);
                if (entry is not null && entry.At >= cutoff && !string.IsNullOrWhiteSpace(entry.FileName)) records.Add(entry);
                else changed = true;
            }
            catch (JsonException) { changed = true; }
            if (records.Count > Limit)
            {
                records.RemoveAt(0);
                changed = true;
            }
        }
        return changed;
    }

    internal void Record(DownloadRecord entry)
    {
        lock (_lock)
        {
            _records.Add(entry);
            if (_records.Count > Limit)
            {
                _records.RemoveAt(0);
                _needsRewrite = true;
            }
            if (_records[0].At < entry.At - Retention)
            {
                _records.RemoveAll(item => item.At < entry.At - Retention);
                _needsRewrite = true;
            }

            if (_path is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (PersistenceUnavailable || _needsRewrite && _writesSinceRewrite >= RewriteInterval) Rewrite();
                    else
                    {
                        // A newline also separates the next record after a partial write.
                        File.AppendAllText(_path, "\n" + JsonSerializer.Serialize(entry) + "\n");
                        _writesSinceRewrite = Math.Min(_writesSinceRewrite + 1, RewriteInterval);
                    }
                    PersistenceUnavailable = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceUnavailable = true; }
            }
            Interlocked.Increment(ref _revision);
        }
    }

    internal void RenameShare(Guid id, string name)
    {
        lock (_lock)
        {
            var found = false;
            for (var i = 0; i < _records.Count; i++)
            {
                if (_records[i].ShareId != id) continue;
                _records[i] = _records[i] with { ShareName = name };
                found = true;
            }
            if (!found) return;
            if (_path is not null)
            {
                try { Rewrite(); PersistenceUnavailable = false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceUnavailable = true; }
            }
            Interlocked.Increment(ref _revision);
        }
    }

    private void Rewrite()
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, _records.Select(item => JsonSerializer.Serialize(item)));
            File.Move(temporary, _path!, true);
            _needsRewrite = false;
            _writesSinceRewrite = 0;
        }
        finally { File.Delete(temporary); }
    }
}

internal static class DownloadAddress
{
    public static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
}
