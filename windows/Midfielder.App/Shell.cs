using System.Diagnostics;
using Microsoft.Win32;

namespace Midfielder.App;

/// <summary>Windows shell integration: autostart, the Explorer "Send to" entry, opening folders.</summary>
internal static class Shell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Midfielder";

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

    public static void Reveal(string file)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }
}
