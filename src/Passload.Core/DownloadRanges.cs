namespace Passload.Core;

internal sealed class DownloadRanges
{
    private const int MaxPending = 256;
    private const int MaxIntervals = 64;
    private static readonly TimeSpan KeepPartial = TimeSpan.FromMinutes(30);
    private readonly Dictionary<(string Address, int File), Pending> _pending = new();

    private sealed class Pending(DateTimeOffset now)
    {
        public DateTimeOffset LastSeen = now;
        public List<(long Start, long End)> Parts { get; } = [];
    }

    public void Clear(string address, int file)
    {
        lock (_pending) _pending.Remove((address, file));
    }

    // End is exclusive. This lets adjacent ranges merge without special cases for empty files.
    public bool IsComplete(string address, int file, long start, long end, long length, DateTimeOffset now)
    {
        lock (_pending)
        {
            var key = (address, file);
            if (_pending.TryGetValue(key, out var pending) && now - pending.LastSeen > KeepPartial)
            {
                _pending.Remove(key);
                pending = null;
            }
            if (pending is null)
            {
                if (_pending.Count >= MaxPending)
                    _pending.Remove(_pending.MinBy(pair => pair.Value.LastSeen).Key);
                _pending[key] = pending = new Pending(now);
            }
            var parts = pending.Parts;
            var index = 0;
            while (index < parts.Count && parts[index].End < start) index++;
            while (index < parts.Count && parts[index].Start <= end)
            {
                start = Math.Min(start, parts[index].Start);
                end = Math.Max(end, parts[index].End);
                parts.RemoveAt(index);
            }
            if (start == 0 && end == length)
            {
                _pending.Remove(key);
                return true;
            }
            if (parts.Count >= MaxIntervals) parts.Clear();
            parts.Insert(Math.Min(index, parts.Count), (start, end));
            pending.LastSeen = now;
            return false;
        }
    }
}
