using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Google.Protobuf;
using Midfielder.Protocol;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Midfielder.App.Continuity;

/// <summary>
/// Whatever plays on this PC (Spotify, a browser tab, Media Player...), for the phone: follows Windows' current media
/// session and sends a <see cref="MediaState"/> when it changes (debounced), and applies the phone's commands.
/// Midfielder's own session (the phone's music shown here) is ignored, so the two never echo each other.
/// </summary>
internal sealed class PcMediaWatcher(Action<MediaState> send) : IDisposable
{
    private const int DebounceMs = 300;
    private const int ArtPx = 300;
    private const int MaxArtBytes = 64 * 1024;

    private readonly object _gate = new();
    private System.Threading.Timer? _debounce;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _artKey;
    private bool _started;
    private bool _disposed;

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
        }
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (!_started) return;
                _manager = manager;
            }
            manager.SessionsChanged += OnSessions;
            manager.CurrentSessionChanged += OnSessions;
            Pick();
        }
        catch (Exception e)
        {
            AppLog.Error("Can't follow this PC's music", e);
        }
    }

    public void Stop()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            manager = _manager;
            _manager = null;
            Attach(null);
        }
        if (manager is not null)
        {
            manager.SessionsChanged -= OnSessions;
            manager.CurrentSessionChanged -= OnSessions;
        }
    }

    /// <summary>The phone (re)connected or switched music on: send the full state, art included.</summary>
    public void Resend()
    {
        lock (_gate) _artKey = null;
        Schedule();
    }

    public async Task CommandAsync(MediaCommand c)
    {
        GlobalSystemMediaTransportControlsSession? s;
        lock (_gate) s = _session;
        if (s is null) return;
        try
        {
            _ = c.Action switch
            {
                MediaAction.Play => await s.TryPlayAsync(),
                MediaAction.Pause => await s.TryPauseAsync(),
                MediaAction.Toggle => await s.TryTogglePlayPauseAsync(),
                MediaAction.Next => await s.TrySkipNextAsync(),
                MediaAction.Previous => await s.TrySkipPreviousAsync(),
                MediaAction.Seek => await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(Math.Max(0, c.SeekMs)).Ticks),
                _ => false,
            };
        }
        catch (Exception e)
        {
            AppLog.Error("Media command from the phone failed", e);
        }
    }

    private void OnSessions(GlobalSystemMediaTransportControlsSessionManager sender, object args) => Pick();

    private void Pick()
    {
        lock (_gate)
        {
            var manager = _manager;
            if (manager is null) return;
            var sessions = manager.GetSessions().Where(s => !IsOurs(s)).ToList();
            var current = manager.GetCurrentSession();
            var best = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? (current is not null && !IsOurs(current) ? current : sessions.FirstOrDefault());
            if (!ReferenceEquals(best, _session) && best?.SourceAppUserModelId != _session?.SourceAppUserModelId) _artKey = null;
            Attach(best);
        }
        Schedule();
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (ReferenceEquals(s, _session)) return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
            _session.TimelinePropertiesChanged -= OnChanged;
        }
        _session = s;
        if (s is not null)
        {
            s.MediaPropertiesChanged += OnChanged;
            s.PlaybackInfoChanged += OnChanged;
            s.TimelinePropertiesChanged += OnChanged;
        }
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (!_started || _disposed) return;
            _debounce ??= new System.Threading.Timer(_ => Push(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private async void Push()
    {
        GlobalSystemMediaTransportControlsSession? s;
        lock (_gate)
        {
            if (!_started) return;
            s = _session;
        }
        try
        {
            if (s is null)
            {
                send(new MediaState { Active = false });
                return;
            }
            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var position = timeline.Position;
            if (playing) position += DateTimeOffset.Now - timeline.LastUpdatedTime;
            var duration = timeline.EndTime - timeline.StartTime;
            string key = $"{s.SourceAppUserModelId}|{props.Title}|{props.AlbumTitle}|{props.Thumbnail is not null}";
            bool sameArt;
            lock (_gate)
            {
                sameArt = key == _artKey;
                _artKey = key;
            }
            var state = new MediaState
            {
                Active = true,
                AppName = AppName(s.SourceAppUserModelId),
                Title = props.Title ?? "",
                Artist = props.Artist ?? props.AlbumArtist ?? "",
                Album = props.AlbumTitle ?? "",
                ArtUnchanged = sameArt,
                Playing = playing,
                PositionMs = (long)Math.Max(0, Math.Min(position.TotalMilliseconds, duration.TotalMilliseconds)),
                DurationMs = (long)Math.Max(0, duration.TotalMilliseconds),
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CanNext = playback.Controls.IsNextEnabled,
                CanPrevious = playback.Controls.IsPreviousEnabled,
                CanSeek = playback.Controls.IsPlaybackPositionEnabled,
            };
            if (!sameArt && props.Thumbnail is not null && await ArtAsync(props.Thumbnail) is { } art) state.ArtJpeg = ByteString.CopyFrom(art);
            send(state);
        }
        catch (Exception e)
        {
            AppLog.Error("Reading this PC's music failed", e);
        }
    }

    private static bool IsOurs(GlobalSystemMediaTransportControlsSession s) =>
        s.SourceAppUserModelId?.Contains("Midfielder", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>"Spotify.exe" / "308046B0AF4A39CB" / "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → a readable name.</summary>
    private static string AppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        string name = aumid.Split('!')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 40 ? name[..40] : name;
    }

    private static async Task<byte[]?> ArtAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            using var source = Image.FromStream(input);
            float scale = Math.Min(1f, (float)ArtPx / Math.Max(source.Width, source.Height));
            using var bmp = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, bmp.Width, bmp.Height);
            }
            using var output = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            bmp.Save(output, jpeg, parameters);
            return output.Length <= MaxArtBytes ? output.ToArray() : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
            _debounce?.Dispose();
        }
    }
}
