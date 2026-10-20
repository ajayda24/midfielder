using System.Collections.Concurrent;
using Midfielder.Core.Clip;

namespace Midfielder.App;

/// <summary>
/// Writes the clipboard on its own thread, so nothing on the tray app's UI thread can delay or block it.
/// The thread owns a message-only window (Windows requires a clipboard owner) and sleeps in
/// MsgWaitForMultipleObjectsEx: zero CPU while idle, wakes for a queued write or for a window message such as
/// WM_DESTROYCLIPBOARD, which it must answer promptly or other programs' copies would wait on it.
/// </summary>
internal sealed class ClipboardWriter : IDisposable
{
    private readonly ConcurrentQueue<(ClipContent Content, TaskCompletionSource<long> Done)> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;

    public ClipboardWriter()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Midfielder clipboard writer" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Completes with the milliseconds the write took, or faults with the Win32 error.</summary>
    public Task<long> WriteAsync(ClipContent content)
    {
        var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((content, done));
        _signal.Set();
        return done.Task;
    }

    private void Run()
    {
        using var window = new OwnerWindow();
        _ready.Set();
        IntPtr[] handles = [_signal.SafeWaitHandle.DangerousGetHandle()];
        while (!_stop)
        {
            uint r = NativeMethods.MsgWaitForMultipleObjectsEx(1, handles, NativeMethods.INFINITE, NativeMethods.QS_ALLINPUT, NativeMethods.MWMO_INPUTAVAILABLE);
            if (_stop) break;
            if (r == NativeMethods.WAIT_OBJECT_0)
            {
                while (_queue.TryDequeue(out var job))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        WindowsClipboard.Write(job.Content, window.Handle);
                        job.Done.TrySetResult(sw.ElapsedMilliseconds);
                    }
                    catch (Exception e)
                    {
                        job.Done.TrySetException(e);
                    }
                }
            }
            NativeMethods.PumpPendingMessages();
        }
        while (_queue.TryDequeue(out var left)) left.Done.TrySetCanceled();
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
        _ready.Dispose();
    }

    private sealed class OwnerWindow : NativeWindow, IDisposable
    {
        public OwnerWindow() => CreateHandle(new CreateParams { Parent = NativeMethods.HWND_MESSAGE });

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
