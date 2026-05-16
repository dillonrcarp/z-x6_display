using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GPULCD.Services;

public class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public Bitmap? AlbumArt { get; private set; }
    public bool IsPlaying { get; private set; }
    public double ProgressPercent { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool HasSession { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => UpdateSession();
            UpdateSession();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media session init failed: {ex.Message}");
        }
    }

    public void Update()
    {
        if (_session == null)
        {
            HasSession = false;
            return;
        }

        try
        {
            var info = _session.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var timeline = _session.GetTimelineProperties();
            if (timeline != null)
            {
                Position = timeline.Position;
                Duration = timeline.EndTime - timeline.StartTime;
                ProgressPercent = Duration.TotalSeconds > 0
                    ? Position.TotalSeconds / Duration.TotalSeconds * 100
                    : 0;
            }
        }
        catch { }
    }

    private async void UpdateSession()
    {
        try
        {
            _session = _manager?.GetCurrentSession();
            HasSession = _session != null;

            if (_session == null)
            {
                Title = ""; Artist = ""; Album = "";
                AlbumArt?.Dispose();
                AlbumArt = null;
                return;
            }

            _session.MediaPropertiesChanged += async (_, _) => await UpdateMediaProperties();
            await UpdateMediaProperties();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Session update failed: {ex.Message}");
        }
    }

    private async Task UpdateMediaProperties()
    {
        if (_session == null) return;

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            Album = props.AlbumTitle ?? "";

            // Load album art
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
                    await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
                    using var ms = new System.IO.MemoryStream();
                    ms.Write(buffer.ToArray(), 0, (int)buffer.Length);
                    ms.Position = 0;
                    var oldArt = AlbumArt;
                    AlbumArt = new Bitmap(ms);
                    oldArt?.Dispose();
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media properties update failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        AlbumArt?.Dispose();
        GC.SuppressFinalize(this);
    }
}
