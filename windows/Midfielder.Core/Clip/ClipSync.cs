using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Midfielder.Protocol;

namespace Midfielder.Core.Clip;

/// <summary>
/// Platform-neutral clipboard sync logic (same behavior as the Kotlin ClipSync): conflated sending, echo
/// prevention by content hash, and catch-up on reconnect (applied only if newer).
/// The Windows app additionally ignores the clipboard event caused by its own write, because Windows may
/// re-encode images.
/// </summary>
public sealed class ClipSync : IDisposable
{
    private readonly string _localDeviceId;
    private readonly Func<ClipboardItem, ValueTask> _broadcast;
    private readonly Func<ClipContent, ClipboardItem, ValueTask> _apply;
    private readonly Func<long> _clock;
    private readonly Channel<ClipboardItem> _outgoing =
        Channel.CreateBounded<ClipboardItem>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _lock = new();
    private readonly Task _pump;
    private ClipboardItem? _latest;
    private byte[]? _lastAppliedHash;

    public ClipSync(string localDeviceId, Func<ClipboardItem, ValueTask> broadcast, Func<ClipContent, ClipboardItem, ValueTask> apply,
        Func<long>? clock = null, ClipboardHistory? history = null)
    {
        _localDeviceId = localDeviceId;
        _broadcast = broadcast;
        _apply = apply;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        History = history ?? new ClipboardHistory();
        _pump = Task.Run(PumpAsync);
    }

    public ClipboardHistory History { get; }
    public bool Enabled { get; set; } = true;

    public static bool IsInlineSize(ClipContent content) => content is not null && content.Bytes.Length <= Limits.MaxInlineClipBytes;

    /// <summary>Call from the clipboard monitor. Returns true if the content will be sent.</summary>
    public bool OnLocalChange(ClipContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Enabled || content.Bytes.IsEmpty || !IsInlineSize(content)) return false;
        if (content is TextClip && content.Bytes.Length > Limits.MaxClipTextBytes) return false;
        byte[] hash = content.Sha256;
        ClipboardItem item;
        lock (_lock)
        {
            if (_lastAppliedHash is not null && CryptographicOperations.FixedTimeEquals(_lastAppliedHash, hash)) return false;
            if (_latest is not null && CryptographicOperations.FixedTimeEquals(_latest.ContentSha256.Span, hash)) return false;
            item = ToItem(content);
            _latest = item;
        }
        History.Add(new ClipboardHistory.Entry(content, _localDeviceId, item.CreatedAtMs));
        _outgoing.Writer.TryWrite(item);
        return true;
    }

    /// <summary>Call when a peer sends an item. Returns true if it was applied.</summary>
    public async ValueTask<bool> OnRemoteAsync(ClipboardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Enabled) return false;
        var content = FromItem(item);
        if (content is null) return false;
        byte[] hash = content.Sha256;
        if (!CryptographicOperations.FixedTimeEquals(hash, item.ContentSha256.Span)) return false;
        lock (_lock)
        {
            if (_latest is not null)
            {
                if (CryptographicOperations.FixedTimeEquals(_latest.ContentSha256.Span, hash)) return false;
                if (item.CatchUp && item.CreatedAtMs <= _latest.CreatedAtMs) return false;
            }
            var stored = item.Clone();
            stored.CatchUp = false;
            _latest = stored;
            _lastAppliedHash = hash;
        }
        History.Add(new ClipboardHistory.Entry(content, item.OriginDeviceId, item.CreatedAtMs));
        await _apply(content, item).ConfigureAwait(false);
        return true;
    }

    public ClipboardItem? CatchUpItem()
    {
        lock (_lock)
        {
            if (_latest is null) return null;
            var copy = _latest.Clone();
            copy.CatchUp = true;
            return copy;
        }
    }

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        _pump.Wait(TimeSpan.FromSeconds(1));
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _broadcast(item).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed broadcast is retried by catch-up on the next connection.
            }
        }
    }

    private ClipboardItem ToItem(ClipContent content) => new()
    {
        Id = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
        OriginDeviceId = _localDeviceId,
        CreatedAtMs = _clock(),
        Kind = content is ImageClip ? ClipKind.Image : ClipKind.Text,
        Mime = content.Mime,
        ContentSha256 = ByteString.CopyFrom(content.Sha256),
        InlineData = ByteString.CopyFrom(content.Bytes.Span),
        Size = (ulong)content.Bytes.Length,
        Sensitive = content.Sensitive,
    };

    private static ClipContent? FromItem(ClipboardItem item)
    {
        var data = item.InlineData;
        if (data.IsEmpty || (ulong)data.Length != item.Size) return null;
        return item.Kind switch
        {
            ClipKind.Text when data.Length <= Limits.MaxClipTextBytes => new TextClip(Encoding.UTF8.GetString(data.Span), item.Sensitive),
            ClipKind.Image when IsPng(data.Span) => new ImageClip(data.ToByteArray(), item.Sensitive),
            _ => null,
        };
    }

    private static bool IsPng(ReadOnlySpan<byte> b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
}
