using System.Runtime.InteropServices;

namespace Midfielder.App;

internal static partial class NativeMethods
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    // --- EcoQoS ("Efficiency mode") -------------------------------------------------------------

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// On: Windows schedules us on efficiency cores at low clock speed (EcoQoS). Off while a transfer runs.
    /// </summary>
    public static void SetEfficiencyMode(bool enabled)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
}
