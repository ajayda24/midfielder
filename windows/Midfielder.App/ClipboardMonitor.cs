namespace Midfielder.App;

/// <summary>
/// Event-driven clipboard watcher: a message-only window registered with AddClipboardFormatListener.
/// Nothing polls; the process sleeps until Windows posts WM_CLIPBOARDUPDATE.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private uint _ignoreSequence;

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

    public void IgnoreCurrentSequence() => _ignoreSequence = NativeMethods.GetClipboardSequenceNumber();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            uint seq = NativeMethods.GetClipboardSequenceNumber();
            if (seq != _ignoreSequence) Changed?.Invoke(this, EventArgs.Empty);
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
