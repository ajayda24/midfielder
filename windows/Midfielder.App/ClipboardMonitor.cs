using System.Drawing;
using System.Windows.Forms;
namespace Midfielder.App;

/// <summary>
/// Event-driven clipboard watcher: a message-only window registered with AddClipboardFormatListener.
/// Nothing polls; the process sleeps until Windows posts WM_CLIPBOARDUPDATE.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private volatile uint _ignoreSequence;
    private long _suppressUntil;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Parent = NativeMethods.HWND_MESSAGE });
        if (!NativeMethods.AddClipboardFormatListener(Handle))
            throw new InvalidOperationException("AddClipboardFormatListener failed");
    }

    /// <summary>Raised on the UI thread after the clipboard changed (not for our own writes).</summary>
    public event EventHandler? Changed;

    /// <summary>Call right after writing the clipboard ourselves so that update is not re-sent.</summary>
    public IntPtr OwnerHandle => Handle;

    /// <summary>Ignore every clipboard change for a moment (while our own write is in progress).</summary>
    public void SuppressFor(TimeSpan duration) => Interlocked.Exchange(ref _suppressUntil, Environment.TickCount64 + (long)duration.TotalMilliseconds);

    public void IgnoreCurrentSequence() => _ignoreSequence = NativeMethods.GetClipboardSequenceNumber();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            uint seq = NativeMethods.GetClipboardSequenceNumber();
            if (seq != _ignoreSequence && Environment.TickCount64 > Interlocked.Read(ref _suppressUntil)) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
