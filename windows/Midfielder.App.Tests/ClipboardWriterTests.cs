using System.Drawing;
using System.Drawing.Imaging;
using Midfielder.Core.Clip;
using Xunit;

namespace Midfielder.App.Tests;

/// <summary>Real Windows clipboard: write on the writer thread, read back the way the app does.</summary>
[Collection("clipboard")] // the clipboard is global: never run these in parallel
public sealed class ClipboardWriterTests : IDisposable
{
    private readonly ClipboardWriter _writer = new();

    public void Dispose() => _writer.Dispose();

    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); } catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA reader hung");
        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public async Task WritesTextThatReadsBack()
    {
        const string text = "héllo from the phone 👋\r\nsecond line";
        await _writer.WriteAsync(new TextClip(text)).WaitAsync(TimeSpan.FromSeconds(5));
        var read = OnSta(WindowsClipboard.Read);
        Assert.Equal(text, Assert.IsType<TextClip>(read).Text);
    }

    [Fact]
    public async Task WritesImageThatReadsBack()
    {
        using var bmp = new Bitmap(64, 48);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.MediumSeaGreen);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        await _writer.WriteAsync(new ImageClip(ms.ToArray())).WaitAsync(TimeSpan.FromSeconds(5));
        var read = OnSta(WindowsClipboard.Read);
        var img = Assert.IsType<ImageClip>(read);
        using var back = Image.FromStream(new MemoryStream(img.Bytes.ToArray()));
        Assert.Equal(64, back.Width);
        Assert.Equal(48, back.Height);
    }

    [Fact]
    public async Task WaitsBrieflyForAnotherProgramHoldingTheClipboard()
    {
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            Assert.True(NativeMethods.OpenClipboard(IntPtr.Zero));
            held.Set();
            Thread.Sleep(300);
            NativeMethods.CloseClipboard();
        });
        holder.Start();
        held.Wait();
        await _writer.WriteAsync(new TextClip("after a short wait")).WaitAsync(TimeSpan.FromSeconds(5));
        holder.Join();
        Assert.Equal("after a short wait", Assert.IsType<TextClip>(OnSta(WindowsClipboard.Read)).Text);
    }

    [Fact]
    public async Task FailsFastInsteadOfHangingWhenTheClipboardStaysBusy()
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            Assert.True(NativeMethods.OpenClipboard(IntPtr.Zero));
            held.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            NativeMethods.CloseClipboard();
        });
        holder.Start();
        held.Wait();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => _writer.WriteAsync(new TextClip("busy")).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
        release.Set();
        holder.Join();
        // The writer is still healthy afterwards.
        await _writer.WriteAsync(new TextClip("recovered")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("recovered", Assert.IsType<TextClip>(OnSta(WindowsClipboard.Read)).Text);
    }
}
