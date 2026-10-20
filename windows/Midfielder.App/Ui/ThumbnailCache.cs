using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Midfielder.App.Ui;

/// <summary>
/// Explorer's own thumbnails (IShellItemImageFactory: the same cache Explorer uses, any format Windows can
/// preview), cropped to squares, frozen, and kept in a small LRU (<see cref="Capacity"/>). One background STA
/// thread fetches them, newest request first; the cache itself is only touched on the UI thread.
/// Created when a page that shows thumbnails opens, and disposed with it.
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    public const int Capacity = 150;

    private readonly Dispatcher _ui;
    private readonly int _size;
    private readonly Dictionary<string, LinkedListNode<(string Path, ImageSource Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, ImageSource Image)> _lru = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<ImageSource>>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentStack<string> _requests = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public ThumbnailCache(Dispatcher ui, int sizePx)
    {
        _ui = ui;
        _size = sizePx;
        var worker = new Thread(Run) { IsBackground = true, Name = "Midfielder thumbnails" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Calls <paramref name="ready"/> with the thumbnail now (cached) or later; never if there is none.</summary>
    public void Request(string path, Action<ImageSource> ready)
    {
        if (_disposed || path.Length == 0 || _failed.Contains(path)) return;
        if (_map.TryGetValue(path, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            ready(node.Value.Image);
            return;
        }
        if (_waiting.TryGetValue(path, out var list))
        {
            list.Add(ready);
            return;
        }
        _waiting[path] = [ready];
        _requests.Push(path);
        _signal.Release();
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
                var image = Load(path, _size);
                if (token.IsCancellationRequested) return;
                _ui.BeginInvoke(() => Store(path, image));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Store(string path, ImageSource? image)
    {
        _waiting.Remove(path, out var callbacks);
        if (_disposed) return;
        if (image is null)
        {
            _failed.Add(path);
            return;
        }
        _map[path] = _lru.AddFirst((path, image));
        while (_lru.Count > Capacity)
        {
            _map.Remove(_lru.Last!.Value.Path);
            _lru.RemoveLast();
        }
        foreach (var cb in callbacks ?? []) cb(image);
    }

    /// <summary>Square, center-cropped, frozen thumbnail from the shell, or null if Windows has none.</summary>
    internal static ImageSource? Load(string path, int size)
    {
        if (!File.Exists(path)) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (NativeShell.SHCreateItemFromParsingName(path, IntPtr.Zero, in iid, out IShellItemImageFactory? factory) != 0 || factory is null) return null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;
            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            var full = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            int side = Math.Min(full.PixelWidth, full.PixelHeight);
            BitmapSource square = new CroppedBitmap(full, new Int32Rect((full.PixelWidth - side) / 2, (full.PixelHeight - side) / 2, side, side));
            if (side > size)
            {
                square = new TransformedBitmap(square, new ScaleTransform(size / (double)side, size / (double)side));
            }
            // Materialize so nothing keeps a reference to the full-size bitmap, then share across threads.
            var result = new WriteableBitmap(square);
            result.Freeze();
            return result;
        }
        catch (Exception e) when (e is COMException or ArgumentException or ExternalException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) NativeShell.DeleteObject(hbmp);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _requests.Clear();
        _lru.Clear();
        _map.Clear();
        _waiting.Clear();
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
