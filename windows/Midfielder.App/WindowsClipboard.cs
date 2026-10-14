using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Midfielder.Core.Clip;

namespace Midfielder.App;

/// <summary>Reads/writes the Windows clipboard (UI/STA thread only).</summary>
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

    public static void Write(ClipContent content)
    {
        var data = new DataObject();
        switch (content)
        {
            case TextClip t:
                data.SetText(t.Text, TextDataFormat.UnicodeText);
                break;
            case ImageClip i:
                // PNG keeps transparency for apps that understand it; the bitmap is for everything else.
                data.SetData("PNG", false, new MemoryStream(i.Bytes.ToArray()));
                using (var ms = new MemoryStream(i.Bytes.ToArray()))
                using (var bmp = Image.FromStream(ms))
                {
                    if (bmp.Width > MaxImageSide || bmp.Height > MaxImageSide) return;
                    data.SetData(DataFormats.Bitmap, true, new Bitmap(bmp));
                }
                break;
            default:
                return;
        }
        if (content.Sensitive)
        {
            // Keep synced secrets out of Win+V history and cloud clipboard.
            data.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", false, new MemoryStream(BitConverter.GetBytes(0)));
        }
        Retry(() =>
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        });
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
