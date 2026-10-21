using System.Diagnostics;
using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

namespace Midfielder.App;

/// <summary>Windows shell integration: autostart, Explorer's right-click and "Send to" entries, opening folders.</summary>
internal static class Shell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Midfielder";

    /// <summary>Per-user verb on every file type (HKCU, no admin). Windows 11 lists it under "Show more options".</summary>
    internal const string ContextMenuKey = @"Software\Classes\*\shell\Midfielder.SendToPhone";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExePath}\" --background");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Adds "Send to ▸ Midfielder (phone)" in Explorer's right-click menu.</summary>
    public static void EnsureSendToShortcut()
    {
        string sendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        string link = Path.Combine(sendTo, "Midfielder (phone).lnk");
        if (File.Exists(link)) return;
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        object? shell = Activator.CreateInstance(shellType);
        if (shell is null) return;
        try
        {
            dynamic s = shell;
            dynamic shortcut = s.CreateShortcut(link);
            shortcut.TargetPath = ExePath;
            shortcut.Arguments = "--send";
            shortcut.IconLocation = ExePath + ",0";
            shortcut.Description = "Send to your phone with Midfielder";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Adds or removes "Send to phone" in Explorer's right-click menu for files.</summary>
    public static void SetContextMenu(bool enabled, string? exePath = null)
    {
        if (!enabled)
        {
            Registry.CurrentUser.DeleteSubKeyTree(ContextMenuKey, throwOnMissingSubKey: false);
            return;
        }
        string exe = exePath ?? ExePath;
        using var key = Registry.CurrentUser.CreateSubKey(ContextMenuKey);
        key.SetValue("", "Send to phone");
        key.SetValue("Icon", $"\"{exe}\",0");
        // Any number of selected files; each starts a tiny Midfielder.exe that hands its path to the running app.
        key.SetValue("MultiSelectModel", "Player");
        using var command = key.CreateSubKey("command");
        command.SetValue("", $"\"{exe}\" --send \"%1\"");
    }

    /// <summary>Moves a file to the Recycle Bin (so a mistaken delete can be undone).</summary>
    public static void Recycle(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens a URI with its registered handler (ms-settings:, https:).</summary>
    public static void OpenUri(string uri)
    {
        using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public static void Reveal(string file)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }
}
