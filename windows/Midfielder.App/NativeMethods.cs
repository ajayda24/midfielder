using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;

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

    // --- Raw clipboard (no OLE, never blocks indefinitely) ---------------------------------------

    public const uint CF_UNICODETEXT = 13;
    public const uint CF_DIB = 8;
    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    // --- Message wait/pump for the clipboard writer thread --------------------------------------

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint nCount, [In] IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(in MSG lpMsg);

    /// <summary>Dispatches every message already queued for this thread, then returns.</summary>
    public static void PumpPendingMessages()
    {
        while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(in msg);
            DispatchMessage(in msg);
        }
    }

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

    // --- Auto-lock and the screen-edge drop -----------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>Installs a low-level hook; <paramref name="proc"/> is a function pointer the caller keeps alive.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial IntPtr SetWindowsHookEx(int idHook, IntPtr proc, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? moduleName);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // --- Taskbar identity of a window -------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Value2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    private static readonly Guid AppUserModelProps = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private const ushort VT_LPWSTR = 31;

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid riid, out IntPtr store);

    /// <summary>
    /// Gives the window its own taskbar identity (AppUserModel ID, relaunch command, icon and name), so its taskbar
    /// button always shows Midfielder's icon, also when the exe runs with the sparse package's identity.
    /// </summary>
    public static void SetWindowAppId(IntPtr hwnd, string? exePath)
    {
        if (hwnd == IntPtr.Zero || exePath is null) return;
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, in IID_IPropertyStore, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return;
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            Set(store, 5, "Midfielder.App");                    // System.AppUserModel.ID
            Set(store, 2, $"\"{exePath}\" --show");             // RelaunchCommand
            Set(store, 3, $"{exePath},0");                      // RelaunchIconResource
            Set(store, 4, "Midfielder");                        // RelaunchDisplayNameResource
            store.Commit();
            Marshal.ReleaseComObject(store);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            AppLog.Info("Couldn't set the window's taskbar identity: " + e.Message);
        }
    }

    private static void Set(IPropertyStore store, uint id, string value)
    {
        var key = new PROPERTYKEY { FormatId = AppUserModelProps, PropertyId = id };
        var v = new PROPVARIANT { Vt = VT_LPWSTR, Value = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            store.SetValue(ref key, ref v);
        }
        finally
        {
            Marshal.FreeCoTaskMem(v.Value);
        }
    }

    public const uint ASFW_ANY = 0xFFFFFFFF;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    private const int DWMWA_CLOAK = 13;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Hides a window from the screen (and the taskbar) without hiding it from Windows.</summary>
    public static void Cloak(IntPtr hwnd, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }
}
