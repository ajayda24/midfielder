namespace Midfielder.Core.Clip;

/// <summary>Recent items, newest first, bounded by count and total bytes. Sensitive items are never stored.</summary>
public sealed class ClipboardHistory
{
    private readonly int _maxItems;
    private readonly long _maxTotalBytes;
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byHash = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private long _bytes;

    public ClipboardHistory(int maxItems = 100, long maxTotalBytes = 32L * 1024 * 1024)
    {
        if (maxItems <= 0 || maxTotalBytes <= 0) throw new ArgumentException("limits must be positive");
        _maxItems = maxItems;
        _maxTotalBytes = maxTotalBytes;
    }

    public sealed record Entry(ClipContent Content, string OriginDeviceId, long AtMs);

    public void Add(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Content.Sensitive) return;
        long size = entry.Content.Bytes.Length;
        if (size > _maxTotalBytes) return;
        lock (_lock)
        {
            if (_byHash.Remove(entry.Content.Sha256Hex, out var existing))
            {
                _order.Remove(existing);
                _bytes -= existing.Value.Content.Bytes.Length;
            }
            _byHash[entry.Content.Sha256Hex] = _order.AddFirst(entry);
            _bytes += size;
            while ((_order.Count > _maxItems || _bytes > _maxTotalBytes) && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _byHash.Remove(oldest.Value.Content.Sha256Hex);
                _bytes -= oldest.Value.Content.Bytes.Length;
            }
        }
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_lock) return [.. _order];
    }

    public void Clear()
    {
        lock (_lock)
        {
            _order.Clear();
            _byHash.Clear();
            _bytes = 0;
        }
    }

    public int Count { get { lock (_lock) return _order.Count; } }
    public long TotalBytes { get { lock (_lock) return _bytes; } }
}
