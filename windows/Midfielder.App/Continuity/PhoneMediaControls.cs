using Midfielder.Protocol;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Midfielder.App.Continuity;

/// <summary>
/// The phone's music in Windows' own media controls (the volume flyout, media keys, lock screen): a media session
/// driven by the phone's <see cref="MediaState"/>. Presses go back to the phone as commands. Nothing is created
/// until the first state arrives, and <see cref="Dispose"/> removes it.
/// </summary>
internal sealed class PhoneMediaControls(Action<MediaAction, long> command) : IDisposable
{
    private MediaPlayer? _player;
    private SystemMediaTransportControls? _smtc;

    public async Task ShowAsync(MediaState s)
    {
        if (!s.Active)
        {
            Clear();
            return;
        }
        var smtc = Ensure();
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = s.CanNext;
        smtc.IsPreviousEnabled = s.CanPrevious;
        var display = smtc.DisplayUpdater;
        display.Type = MediaPlaybackType.Music;
        display.MusicProperties.Title = s.Title;
        display.MusicProperties.Artist = s.Artist;
        display.MusicProperties.AlbumTitle = s.Album;
        if (!s.ArtUnchanged)
        {
            display.Thumbnail = s.ArtJpeg.IsEmpty ? null : RandomAccessStreamReference.CreateFromStream(await ToStreamAsync(s.ArtJpeg.ToByteArray()));
        }
        display.Update();
        smtc.PlaybackStatus = s.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (s.DurationMs > 0)
        {
            var end = TimeSpan.FromMilliseconds(s.DurationMs);
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = end,
                MaxSeekTime = s.CanSeek ? end : TimeSpan.Zero,
                Position = TimeSpan.FromMilliseconds(Math.Clamp(s.PositionMs, 0, s.DurationMs)),
            });
        }
    }

    public void Clear()
    {
        if (_smtc is null) return;
        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Update();
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.IsEnabled = false;
    }

    private SystemMediaTransportControls Ensure()
    {
        if (_smtc is not null) return _smtc;
        // A MediaPlayer with its automatic commands off gives an unpackaged app its own media session to drive.
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false;
        _smtc = _player.SystemMediaTransportControls;
        _smtc.ButtonPressed += OnButton;
        _smtc.PlaybackPositionChangeRequested += OnSeek;
        return _smtc;
    }

    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        var action = e.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaAction.Play,
            SystemMediaTransportControlsButton.Pause => MediaAction.Pause,
            SystemMediaTransportControlsButton.Next => MediaAction.Next,
            SystemMediaTransportControlsButton.Previous => MediaAction.Previous,
            _ => MediaAction.Unspecified,
        };
        if (action != MediaAction.Unspecified) command(action, 0);
    }

    private void OnSeek(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs e) =>
        command(MediaAction.Seek, (long)e.RequestedPlaybackPosition.TotalMilliseconds);

    private static async Task<IRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return stream;
    }

    public void Dispose()
    {
        if (_smtc is not null)
        {
            Clear();
            _smtc.ButtonPressed -= OnButton;
            _smtc.PlaybackPositionChangeRequested -= OnSeek;
        }
        _player?.Dispose();
        _player = null;
        _smtc = null;
    }
}
