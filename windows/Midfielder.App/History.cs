using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Midfielder.App;

internal enum TransferDirection { Sent, Received }

internal enum TransferStatus { Done, Failed, Cancelled }

/// <summary>One finished file. <see cref="Location"/> is the full path (received files, or the sent original).</summary>
internal sealed record TransferRecord(
    string Id, TransferDirection Direction, string Name, string Mime, long Size, string Peer, long TimeMs,
    TransferStatus Status, string Location)
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };

    [JsonIgnore]
    public bool IsImage => Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(System.IO.Path.GetExtension(Name));

    [JsonIgnore]
    public DateTime Time => DateTimeOffset.FromUnixTimeMilliseconds(TimeMs).LocalDateTime;
}

/// <summary>Totals per direction for clips, files and bytes.</summary>
internal sealed record Counts(long ClipsSent = 0, long ClipsReceived = 0, long FilesSent = 0, long FilesReceived = 0, long BytesSent = 0, long BytesReceived = 0)
{
    [JsonIgnore] public long Clips => ClipsSent + ClipsReceived;
    [JsonIgnore] public long Files => FilesSent + FilesReceived;
    [JsonIgnore] public long Bytes => BytesSent + BytesReceived;

    public static Counts operator +(Counts a, Counts b) => new(
        a.ClipsSent + b.ClipsSent, a.ClipsReceived + b.ClipsReceived, a.FilesSent + b.FilesSent,
        a.FilesReceived + b.FilesReceived, a.BytesSent + b.BytesSent, a.BytesReceived + b.BytesReceived);
}

/// <summary>
/// File history (newest first, at most <see cref="MaxRecords"/>) and usage counters with one bucket per local day
/// (last <see cref="KeepDays"/> days). Saved to %LOCALAPPDATA%\Midfielder\history.json a second after the last
/// change, so a burst of events costs one write. Thread-safe; <see cref="Changed"/> fires on the caller's thread.
/// </summary>
internal sealed class HistoryStore : IDisposable
{
    public const int MaxRecords = 500;
    public const int KeepDays = 30;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly System.Threading.Timer _saveTimer;
    private readonly List<TransferRecord> _records = [];
    private readonly SortedDictionary<string, Counts> _days = new(StringComparer.Ordinal);
    private Counts _total = new();
    private bool _dirty;

    public HistoryStore(string? path)
    {
        _path = path;
        _saveTimer = new System.Threading.Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        if (path is not null && File.Exists(path))
        {
            try
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllBytes(path));
                if (data is not null)
                {
                    _records.AddRange((data.Records ?? []).Where(r => r.Id is not null && r.Name is not null).Take(MaxRecords));
                    _total = data.Total ?? new Counts();
                    foreach (var (k, v) in data.Days ?? []) _days[k] = v;
                    TrimDays();
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                AppLog.Error("History could not be read; starting fresh", e);
            }
        }
    }

    public event Action? Changed;

    public IReadOnlyList<TransferRecord> Records()
    {
        lock (_gate) return [.. _records];
    }

    public Counts Totals()
    {
        lock (_gate) return _total;
    }

    /// <summary>The last <paramref name="n"/> days ending today, oldest first; empty days are zero.</summary>
    public IReadOnlyList<Counts> LastDays(int n, DateTime today)
    {
        lock (_gate)
        {
            return Enumerable.Range(0, n).Select(i => _days.GetValueOrDefault(Key(today.AddDays(i - n + 1))) ?? new Counts()).ToList();
        }
    }

    public void Add(IEnumerable<TransferRecord> records)
    {
        lock (_gate)
        {
            foreach (var r in records.Reverse())
            {
                _records.RemoveAll(x => x.Id == r.Id);
                _records.Insert(0, r);
            }
            if (_records.Count > MaxRecords) _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
        }
        OnChanged();
    }

    public int Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        int n;
        lock (_gate) n = _records.RemoveAll(r => set.Contains(r.Id));
        if (n > 0) OnChanged();
        return n;
    }

    public void ClearHistory()
    {
        lock (_gate) _records.Clear();
        OnChanged();
    }

    public void ClearStats()
    {
        lock (_gate)
        {
            _total = new Counts();
            _days.Clear();
        }
        OnChanged();
    }

    public void RecordClip(TransferDirection d, DateTime when) =>
        AddCounts(when, d == TransferDirection.Sent ? new Counts(ClipsSent: 1) : new Counts(ClipsReceived: 1));

    public void RecordFiles(TransferDirection d, int count, long bytes, DateTime when) =>
        AddCounts(when, d == TransferDirection.Sent ? new Counts(FilesSent: count, BytesSent: bytes) : new Counts(FilesReceived: count, BytesReceived: bytes));

    private void AddCounts(DateTime when, Counts c)
    {
        lock (_gate)
        {
            _total += c;
            string k = Key(when);
            _days[k] = (_days.GetValueOrDefault(k) ?? new Counts()) + c;
            TrimDays();
        }
        OnChanged();
    }

    private void TrimDays()
    {
        while (_days.Count > KeepDays) _days.Remove(_days.Keys.First());
    }

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void OnChanged()
    {
        lock (_gate) _dirty = true;
        if (_path is not null) _saveTimer.Change(1_000, Timeout.Infinite);
        Changed?.Invoke();
    }

    /// <summary>Writes pending changes now (also called on exit).</summary>
    public void Save()
    {
        if (_path is null) return;
        byte[] bytes;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            bytes = JsonSerializer.SerializeToUtf8Bytes(new Data { Records = [.. _records], Total = _total, Days = new(_days) });
        }
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving history failed", e);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Save();
    }

    private sealed class Data
    {
        public List<TransferRecord>? Records { get; set; }
        public Counts? Total { get; set; }
        public Dictionary<string, Counts>? Days { get; set; }
    }
}

/// <summary>A transfer that is still moving bytes.</summary>
internal sealed record ActiveTransfer(string Id, TransferDirection Direction, string Title, string Peer, long Done, long Total)
{
    public int Percent => Total <= 0 ? -1 : (int)(Math.Clamp(Done, 0, Total) * 100 / Total);
}

/// <summary>Running transfers for the progress UI; progress events are throttled per transfer.</summary>
internal sealed class TransferTracker(long minIntervalMs = 250)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ActiveTransfer T, long LastMs)> _active = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyList<ActiveTransfer> Active()
    {
        lock (_gate) return _active.Values.Select(v => v.T).ToList();
    }

    public bool Contains(string id)
    {
        lock (_gate) return _active.ContainsKey(id);
    }

    public ActiveTransfer? Get(string id)
    {
        lock (_gate) return _active.TryGetValue(id, out var v) ? v.T : null;
    }

    public void Start(string id, TransferDirection d, string title, string peer, long total)
    {
        lock (_gate) _active[id] = (new ActiveTransfer(id, d, title, peer, 0, total), Environment.TickCount64);
        Changed?.Invoke();
    }

    /// <summary>Returns true when the update was published (not throttled).</summary>
    public bool Progress(string id, long done, long total)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var v)) return false;
            long now = Environment.TickCount64;
            if (done < total && now - v.LastMs < minIntervalMs) return false;
            _active[id] = (v.T with { Done = done, Total = total }, now);
        }
        Changed?.Invoke();
        return true;
    }

    public void Finish(string id)
    {
        bool removed;
        lock (_gate) removed = _active.Remove(id);
        if (removed) Changed?.Invoke();
    }
}
