using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Midfielder.App.Ui;

/// <summary>
/// Explorer's own thumbnails (IShellItemImageFactory: same cache Explorer uses, any format Windows can preview),
/// cropped to squares and kept in a small LRU (<see cref="Capacity"/> bitmaps). One background STA thread
/// fetches them, newest request first; results are stored and painted on the UI thread only.
/// Created when the Files page opens and disposed with it.
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    public const int Capacity = 150;

    private readonly Control _owner;
    private readonly int _size;
    private readonly Dictionary<string, LinkedListNode<(string Path, Bitmap Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, Bitmap Image)> _lru = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentStack<string> _requests = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;

    public ThumbnailCache(Control owner, int sizePx)
    {
        _owner = owner;
        _size = sizePx;
        _worker = new Thread(Run) { IsBackground = true, Name = "Midfielder thumbnails" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>Raised on the UI thread when a requested thumbnail is ready.</summary>
    public event Action<string>? Loaded;

    /// <summary>The thumbnail if cached; otherwise null, and it is fetched in the background.</summary>
    public Bitmap? Get(string path)
    {
        if (_map.TryGetValue(path, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
        if (!_failed.Contains(path) && _queued.Add(path))
        {
            _requests.Push(path);
            _signal.Release();
        }
        return null;
    }

    /// <summary>Drops queued requests (e.g. after scrolling far away); cached images stay.</summary>
    public void CancelPending()
    {
        _requests.Clear();
        _queued.Clear();
    }

    private void Run()
    {
        var token = _cts.Token;
        try
        {
            while (true)
            {
                _signal.Wait(token);
                if (!_requests.TryPop(out string? path)) continue;
                Bitmap? bmp = Load(path, _size);
                if (token.IsCancellationRequested)
                {
                    bmp?.Dispose();
                    return;
                }
                try
                {
                    _owner.BeginInvoke(() => Store(path, bmp));
                }
                catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
                {
                    bmp?.Dispose();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Store(string path, Bitmap? bmp)
    {
        _queued.Remove(path);
        if (_disposed)
        {
            bmp?.Dispose();
            return;
        }
        if (bmp is null)
        {
            _failed.Add(path);
            return;
        }
        if (_map.Remove(path, out var old))
        {
            _lru.Remove(old);
            old.Value.Image.Dispose();
        }
        _map[path] = _lru.AddFirst((path, bmp));
        while (_lru.Count > Capacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Path);
            last.Value.Image.Dispose();
        }
        Loaded?.Invoke(path);
    }

    /// <summary>Square, center-cropped thumbnail from the shell, or null if Windows has none.</summary>
    internal static Bitmap? Load(string path, int size)
    {
        if (!File.Exists(path)) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (NativeShell.SHCreateItemFromParsingName(path, IntPtr.Zero, in iid, out IShellItemImageFactory? factory) != 0 || factory is null) return null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;
            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            using var full = Image.FromHbitmap(hbmp);
            var square = new Bitmap(size, size);
            using var g = Graphics.FromImage(square);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float scale = Math.Max(size / (float)full.Width, size / (float)full.Height);
            float w = full.Width * scale, h = full.Height * scale;
            g.DrawImage(full, (size - w) / 2, (size - h) / 2, w, h);
            return square;
        }
        catch (Exception e) when (e is COMException or ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) NativeShell.DeleteObject(hbmp);
            if (factory is IDisposable d) d.Dispose();
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _signal.Release();
        _requests.Clear();
        foreach (var (_, image) in _lru) image.Dispose();
        _lru.Clear();
        _map.Clear();
        _cts.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int cx;
    public int cy;
}

[GeneratedComInterface]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
internal partial interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeSize size, int flags, out IntPtr phbm);
}

internal static partial class NativeShell
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid, out IShellItemImageFactory? ppv);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);
}
