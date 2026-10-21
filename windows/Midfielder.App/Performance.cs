namespace Midfielder.App;

/// <summary>
/// Efficiency mode (EcoQoS) while idle in the tray; full speed whenever the user is waiting on something: an open
/// window, a transfer, audio streaming. A window in an EcoQoS process renders on slow cores at low clocks (blank for
/// seconds when opened, sluggish after sitting unfocused), so each of those holds full speed until it ends.
/// </summary>
internal static class Performance
{
    private static readonly object Gate = new();
    private static int s_holds;
    private static bool s_started;

    /// <summary>Called once the tray is up: from here on, no holds means efficiency mode.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            s_started = true;
            Apply();
        }
    }

    /// <summary>Full speed until the returned token is disposed. Disposing twice is harmless.</summary>
    public static IDisposable Hold()
    {
        lock (Gate)
        {
            s_holds++;
            Apply();
        }
        return new Token();
    }

    private static void Release()
    {
        lock (Gate)
        {
            s_holds = Math.Max(0, s_holds - 1);
            Apply();
        }
    }

    private static void Apply()
    {
        if (!s_started) return;
        try
        {
            NativeMethods.SetEfficiencyMode(s_holds == 0);
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows without power throttling: nothing to switch.
        }
    }

    private sealed class Token : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Release();
        }
    }
}
