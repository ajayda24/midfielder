using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Midfielder.Core.Clip;
using System.Windows.Forms;

namespace Midfielder.App;

/// <summary>Reads/writes the Windows clipboard (UI thread).</summary>
internal static class WindowsClipboard
{
    private const int MaxImageSide = 8192;

    /// <summary>Returns null when there is nothing syncable or the source app asked to be excluded.</summary>
    public static ClipContent? Read()
    {
        var data = Retry(Clipboard.GetDataObject);
        if (data is null) return null;
        // Password managers mark secrets with these formats; respect them.
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return null;
        bool sensitive = IsHistoryExcluded(data);

        // Copy on an image file in File Explorer: send the picture itself, so it pastes as an image on the phone.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
        {
            return ReadImageFile(files[0], sensitive);
        }

        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            string? text = data.GetData(DataFormats.UnicodeText) as string;
            return string.IsNullOrEmpty(text) ? null : new TextClip(text, sensitive);
        }
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png && png.Length > 8)
        {
            return new ImageClip(png.ToArray(), sensitive);
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image img)
        {
            using (img)
            {
                if (img.Width > MaxImageSide || img.Height > MaxImageSide) return null;
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                return new ImageClip(ms.ToArray(), sensitive);
            }
        }
        return null;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    };

    private const long MaxImageFileBytes = 64L * 1024 * 1024;

    /// <summary>A single copied image file becomes an image clip (PNG kept as-is, others converted).</summary>
    private static ImageClip? ReadImageFile(string path, bool sensitive)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path))) return null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxImageFileBytes) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            return new ImageClip(bytes, sensitive);
        }
        using var input = new MemoryStream(bytes);
        using var img = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
        if (img.Width > MaxImageSide || img.Height > MaxImageSide) return null;
        using var output = new MemoryStream();
        img.Save(output, ImageFormat.Png);
        return new ImageClip(output.ToArray(), sensitive);
    }

    /// <summary>
    /// Puts content on the clipboard with the plain Win32 API (OpenClipboard/SetClipboardData) rather than OLE.
    /// OLE's SetDataObject can block indefinitely while it waits on other programs; this either finishes at
    /// once or throws. <paramref name="owner"/> is our clipboard listener window.
    /// </summary>
    public static void Write(ClipContent content, IntPtr owner)
    {
        var formats = new List<(uint Format, byte[] Data)>();
        switch (content)
        {
            case TextClip t:
                formats.Add((NativeMethods.CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(t.Text + "\0")));
                break;
            case ImageClip i:
                byte[] png = i.Bytes.ToArray();
                byte[]? dib = PngToDib(png);
                if (dib is null) return;
                formats.Add((NativeMethods.CF_DIB, dib));
                // PNG too, so apps that understand it keep transparency.
                formats.Add((NativeMethods.RegisterClipboardFormat("PNG"), png));
                break;
            default:
                return;
        }
        if (content.Sensitive)
        {
            // Keep synced secrets out of Win+V history and cloud clipboard.
            byte[] zero = BitConverter.GetBytes(0);
            formats.Add((NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory"), zero));
            formats.Add((NativeMethods.RegisterClipboardFormat("CanUploadToCloudClipboard"), zero));
        }

        OpenWithRetry(owner);
        try
        {
            if (!NativeMethods.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed");
            foreach (var (format, data) in formats) SetData(format, data);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>Another program may have the clipboard open for a moment; retry for up to ~1 s, never longer.</summary>
    private static void OpenWithRetry(IntPtr owner)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner)) return;
            if (attempt >= 20) throw new Win32Exception(Marshal.GetLastWin32Error(), "The clipboard is in use by another program");
            Thread.Sleep(50);
        }
    }

    private static void SetData(uint format, byte[] data)
    {
        IntPtr handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed");
        try
        {
            IntPtr target = NativeMethods.GlobalLock(handle);
            if (target == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed");
            try
            {
                Marshal.Copy(data, 0, target, data.Length);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
            if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData failed");
            handle = IntPtr.Zero; // the clipboard owns it now
        }
        finally
        {
            if (handle != IntPtr.Zero) NativeMethods.GlobalFree(handle);
        }
    }

    /// <summary>CF_DIB is a BMP file without its 14-byte file header.</summary>
    private static byte[]? PngToDib(byte[] png)
    {
        using var input = new MemoryStream(png);
        using var img = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
        if (img.Width > MaxImageSide || img.Height > MaxImageSide) return null;
        using var bmp = new Bitmap(img);
        using var output = new MemoryStream();
        bmp.Save(output, ImageFormat.Bmp);
        const int fileHeader = 14;
        return output.Length <= fileHeader ? null : output.ToArray()[fileHeader..];
    }

    private static bool IsHistoryExcluded(IDataObject data) =>
        data.GetDataPresent("CanIncludeInClipboardHistory") &&
        data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4 &&
        BitConverter.ToInt32(ms.ToArray(), 0) == 0;

    /// <summary>Another process may hold the clipboard open for a moment; retry briefly.</summary>
    private static T? Retry<T>(Func<T?> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(30);
            }
        }
    }
}
