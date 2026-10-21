using System.Diagnostics;
using Midfielder.Core.Feature;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Midfielder.App.Continuity;

/// <summary>
/// This PC's sound for the phone: captures what Windows plays (WASAPI loopback), converts it to 16-bit stereo and
/// hands it out in 20 ms chunks on a steady clock (silence when nothing plays, so the phone's player never starves).
/// At most ~200 ms is queued, so the delay stays small. Optionally mutes the PC while it runs.
/// </summary>
internal sealed class PcAudioStream : IDisposable
{
    private const int MaxQueuedMs = 200;
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    private readonly WasapiLoopbackCapture _capture;
    private readonly object _gate = new();
    private readonly Queue<byte> _pcm = new();
    private readonly int _maxQueuedBytes;
    private readonly bool _float;
    private readonly int _channels;
    private readonly bool? _wasMuted;
    private bool _disposed;

    public PcAudioStream(string streamId, bool mutePc)
    {
        StreamId = streamId;
        _capture = new WasapiLoopbackCapture();
        var format = _capture.WaveFormat;
        SampleRate = format.SampleRate;
        _channels = format.Channels;
        _float = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
        if (!(_float && format.BitsPerSample == 32) && !(format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            && !(format is WaveFormatExtensible && format.BitsPerSample == 16))
        {
            _capture.Dispose();
            throw new NotSupportedException($"Unsupported audio format: {format}");
        }
        ChunkBytes = AudioFrames.ChunkBytes(SampleRate, 2);
        _maxQueuedBytes = ChunkBytes * (MaxQueuedMs / AudioFrames.ChunkMs);
        _capture.DataAvailable += OnData;
        if (mutePc) _wasMuted = SetMuted(true);
        _capture.StartRecording();
    }

    public string StreamId { get; }
    public int SampleRate { get; }
    public int ChunkBytes { get; }

    /// <summary>Raised once when the stream stops by itself (the phone went away, capture failed).</summary>
    public event Action? Ended;

    /// <summary>Runs on the stream's data connection: one chunk every 20 ms until disposed or the phone disconnects.</summary>
    public async Task WriteAsync(Stream net, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        uint seq = 0;
        var chunk = new byte[ChunkBytes];
        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                long due = (long)clock.Elapsed.TotalMilliseconds / AudioFrames.ChunkMs;
                while (seq <= due && !_disposed)
                {
                    Take(chunk);
                    await AudioFrames.WriteAsync(net, seq++, chunk, ct).ConfigureAwait(false);
                }
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The phone stopped listening, or we stopped.
        }
        finally
        {
            Ended?.Invoke();
        }
    }

    private void Take(byte[] chunk)
    {
        lock (_gate)
        {
            int n = Math.Min(chunk.Length, _pcm.Count);
            for (int i = 0; i < n; i++) chunk[i] = _pcm.Dequeue();
            Array.Clear(chunk, n, chunk.Length - n); // silence for whatever is missing
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _channels * (_float ? 4 : 2);
        int frames = e.BytesRecorded / frameBytes;
        lock (_gate)
        {
            for (int f = 0; f < frames; f++)
            {
                int at = f * frameBytes;
                for (int c = 0; c < 2; c++)
                {
                    int ch = Math.Min(c, _channels - 1); // mono is duplicated, extra channels dropped
                    short sample = _float
                        ? (short)Math.Clamp(BitConverter.ToSingle(e.Buffer, at + ch * 4) * 32767f, -32768f, 32767f)
                        : BitConverter.ToInt16(e.Buffer, at + ch * 2);
                    _pcm.Enqueue((byte)sample);
                    _pcm.Enqueue((byte)(sample >> 8));
                }
            }
            // Keep the delay bounded: drop the oldest audio if the phone fell behind.
            while (_pcm.Count > _maxQueuedBytes) _pcm.Dequeue();
        }
    }

    /// <summary>Mutes or unmutes this PC's speakers; returns the previous state (null if it couldn't).</summary>
    private static bool? SetMuted(bool muted)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            bool was = device.AudioEndpointVolume.Mute;
            device.AudioEndpointVolume.Mute = muted;
            return was;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.DataAvailable -= OnData;
        try
        {
            _capture.StopRecording();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        _capture.Dispose();
        if (_wasMuted is { } was) SetMuted(was);
    }
}
