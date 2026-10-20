using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Midfielder.App.Ui;
using Windows.Management.Deployment;

namespace Midfielder.App;

/// <summary>
/// The Windows 11 top-level "Send to phone" entry. Explorer only shows top-level entries from apps with package
/// identity, so Midfielder registers a tiny *sparse* package (Midfielder.msix, next to the exe) whose external
/// location is the exe's folder; the package declares MidfielderShell.dll as the context-menu handler.
/// The package is signed with Midfielder's own certificate, which Windows must trust once (one UAC prompt).
/// </summary>
internal static class ModernMenu
{
    public const string PackageName = "Midfielder";
    private const string StateFile = "modern-menu.txt";

    private static string ExeDir => Path.GetDirectoryName(Shell.ExePath)!;
    private static string Msix => Path.Combine(ExeDir, "Midfielder.msix");
    private static string Cer => Path.Combine(ExeDir, "midfielder-sideload.cer");
    private static string Handler => Path.Combine(ExeDir, "MidfielderShell.dll");
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midfielder", StateFile);

    /// <summary>Windows 11 (build 22000+) with the package files next to the exe.</summary>
    public static bool Supported =>
        Environment.OSVersion.Version.Build >= 22000 && File.Exists(Msix) && File.Exists(Cer) && File.Exists(Handler);

    public static ModernMenuStatus Status
    {
        get
        {
            if (!Supported) return ModernMenuStatus.Unavailable;
            if (FindPackage() is null) return ModernMenuStatus.Off;
            string? registeredFor = File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : null;
            return registeredFor is null || string.Equals(registeredFor, ExeDir, StringComparison.OrdinalIgnoreCase)
                ? ModernMenuStatus.On
                : ModernMenuStatus.NeedsRepair;
        }
    }

    public static async Task<string> EnableAsync()
    {
        if (!Supported) return "The Windows 11 menu needs Windows 11 and the files that come with Midfielder.exe.";
        if (!IsCertificateTrusted() && !await TrustCertificateAsync().ConfigureAwait(true))
        {
            return "Not turned on: Windows didn't get permission to trust Midfielder's certificate.";
        }
        if (FindPackage() is { } old) await RemoveAsync(old).ConfigureAwait(true);
        var options = new AddPackageOptions
        {
            ExternalLocationUri = new Uri(ExeDir + Path.DirectorySeparatorChar),
            ForceUpdateFromAnyVersion = true,
        };
        var result = await new PackageManager().AddPackageByUriAsync(new Uri(Msix), options);
        if (result.ExtendedErrorCode is { } error)
        {
            AppLog.Error($"Registering the sparse package failed: {result.ErrorText}", error);
            return "Couldn't turn it on: " + result.ErrorText;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, ExeDir);
        return "Done. Right-click any file in Explorer → Send to phone.";
    }

    public static async Task<string> DisableAsync()
    {
        if (FindPackage() is { } p) await RemoveAsync(p).ConfigureAwait(true);
        if (File.Exists(StatePath)) File.Delete(StatePath);
        return "The Windows 11 menu entry is off.";
    }

    /// <summary>The exe moved since registration: register again for the new folder (no prompt needed).</summary>
    public static async Task RepairIfMovedAsync()
    {
        try
        {
            if (Status == ModernMenuStatus.NeedsRepair && IsCertificateTrusted())
            {
                AppLog.Info("Midfielder.exe moved: re-registering the Windows 11 menu entry: " + await EnableAsync().ConfigureAwait(true));
            }
        }
        catch (Exception e)
        {
            AppLog.Error("Re-registering the Windows 11 menu entry failed", e);
        }
    }

    private static global::Windows.ApplicationModel.Package? FindPackage()
    {
        try
        {
            return new PackageManager().FindPackagesForUser(string.Empty)
                .FirstOrDefault(p => string.Equals(p.Id.Name, PackageName, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static async Task RemoveAsync(global::Windows.ApplicationModel.Package p)
    {
        var result = await new PackageManager().RemovePackageAsync(p.Id.FullName);
        if (result.ExtendedErrorCode is { } error) AppLog.Error($"Removing the sparse package failed: {result.ErrorText}", error);
    }

    private static bool IsCertificateTrusted()
    {
        using var cert = new X509Certificate2(Cer);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
    }

    /// <summary>One UAC prompt: adds Midfielder's certificate to the machine's Trusted People store.</summary>
    private static async Task<bool> TrustCertificateAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("certutil.exe", $"-addstore TrustedPeople \"{Cer}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            await p.WaitForExitAsync().ConfigureAwait(true);
            return p.ExitCode == 0 && IsCertificateTrusted();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return false; // the user said No to the UAC prompt
        }
    }
}
