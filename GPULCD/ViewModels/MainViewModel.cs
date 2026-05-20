using System.Diagnostics;
using System.Drawing;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GPULCD.Models;
using GPULCD.Rendering;
using GPULCD.Rendering.Pages;
using GPULCD.Services;
using ConnectionState = GPULCD.Models.ConnectionState;

namespace GPULCD.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly LcdService _lcd;
    private readonly ISensorService _sensors;
    private readonly TrayService _tray;
    private readonly FrameRenderer _renderer;
    private readonly AppSettings _settings;
    private readonly GscolerBracketService? _gscoler;
    private bool IsGscolerMode => _settings.DisplayMode.Equals("gscoler", StringComparison.OrdinalIgnoreCase);
    private System.Threading.Timer? _renderTimer;
    private int _currentIntervalMs;
    private int _rendering; // re-entrancy guard
    private int _previewSkip; // skip preview frames for perf
    private Bitmap? _pendingLcdFrame; // latest frame for LCD send thread
    private readonly object _lcdFrameLock = new();
    private Thread? _lcdSendThread;

    private string _connectionStatus = "Disconnected";
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    private string _sensorStatus = "Not started";
    public string SensorStatus { get => _sensorStatus; set => Set(ref _sensorStatus, value); }

    private string _currentPageName = "";
    public string CurrentPageName { get => _currentPageName; set => Set(ref _currentPageName, value); }

    private int _brightness = 75;
    public int Brightness
    {
        get => _brightness;
        set
        {
            if (Set(ref _brightness, value))
            {
                _settings.Brightness = value;
                if (_lcd.State == ConnectionState.Connected)
                    _lcd.SetBrightness(value);
            }
        }
    }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    private ImageSource? _previewImage;
    public ImageSource? PreviewImage { get => _previewImage; set => Set(ref _previewImage, value); }

    public ICommand StartStopCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _lcd = new LcdService();
        _sensors = _settings.SensorProvider.Equals("hwinfo", StringComparison.OrdinalIgnoreCase)
            ? new HwInfoSensorService()
            : new LibreHardwareMonitorSensorService();
        _tray = new TrayService(onExit: () =>
        {
            Stop();
            System.Windows.Application.Current.Shutdown();
        });

        bool isLandscape = _settings.Orientation is DisplayOrientation.Landscape or DisplayOrientation.ReverseLandscape;
        _renderer = new FrameRenderer(
            width: isLandscape ? 480 : 320,
            height: isLandscape ? 320 : 480);

        // Register pages
        _renderer.AddPage(new SystemMonitorPage(_sensors, _settings));
        _renderer.AddPage(new NetworkPage());
        _renderer.AddPage(new AlertsPage(_sensors, _settings));

        _renderer.AddPage(new NowPlayingPage(new MediaSessionService()));

        // GIF player — add if a GIF file exists
        var gifPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "Gifs", "demo.gif");
        var altGifPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GPULCD", "screensaver.gif");
        if (System.IO.File.Exists(gifPath))
            _renderer.AddPage(new GifPlayerPage(gifPath));
        else if (System.IO.File.Exists(altGifPath))
            _renderer.AddPage(new GifPlayerPage(altGifPath));

        _renderer.AutoRotateEnabled = true;
        _renderer.AutoRotateSeconds = 30;
        _renderer.PageChanged += (_, page) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CurrentPageName = page.Name;
                AdjustTimerForPage(page);
            });
        };

        if (IsGscolerMode)
            _gscoler = new GscolerBracketService(_sensors, _settings);

        _brightness = _settings.Brightness;
        _currentIntervalMs = _settings.UpdateIntervalMs;

        StartStopCommand = new RelayCommand(ToggleStartStop);
        NextPageCommand = new RelayCommand(() => _renderer.NextPage());
        PrevPageCommand = new RelayCommand(() => _renderer.PrevPage());

        _lcd.StateChanged += OnLcdStateChanged;
    }

    public void InitializeTray(System.Windows.Window window)
    {
        _tray.Initialize(window, _renderer);
        _tray.UpdateState(ConnectionState.Disconnected, false);
    }

    public async void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        _sensors.Start(_settings.UpdateIntervalMs);
        SensorStatus = _sensors.StatusMessage;
        CurrentPageName = _renderer.CurrentPage?.Name ?? "";

        // One-time sensor dump for diagnostics
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000); // wait for first poll
            var readings = _sensors.GetCurrentReadings();
            var dumpPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GPULCD", "sensor-dump.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dumpPath)!);
            var lines = readings
                .OrderBy(r => r.Key)
                .Select(r => $"{r.Key,-50} {r.Value.Value,10:F1} {r.Value.Unit}");
            System.IO.File.WriteAllLines(dumpPath, lines);
        });

        if (IsGscolerMode)
        {
            try
            {
                ConnectionStatus = "Connecting (GSCOLER)...";
                await _gscoler!.ConnectAsync(_settings.ComPort);
                _gscoler.StartStreaming();
                ConnectionStatus = "Connected (GSCOLER Z-X6)";
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Failed: {ex.Message}";
                Debug.WriteLine($"GSCOLER connect failed: {ex}");
            }
        }
        else
        {
            try
            {
                ConnectionStatus = "Connecting...";
                await _lcd.ConnectAsync(_settings.ComPort);
                _lcd.SetBrightness(_settings.Brightness);
                _lcd.SetOrientation(_settings.Orientation);
                ConnectionStatus = $"Connected ({_lcd.DetectedModel})";
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Failed: {ex.Message}";
                Debug.WriteLine($"LCD connect failed: {ex}");
            }

            // Separate thread for LCD serial writes so they don't block rendering
            _lcdSendThread = new Thread(LcdSendLoop) { IsBackground = true, Name = "LcdSend" };
            _lcdSendThread.Start();
        }

        _currentIntervalMs = _renderer.CurrentPage?.PreferredIntervalMs ?? _settings.UpdateIntervalMs;
        _renderTimer = new System.Threading.Timer(_ => RenderFrame(), null, 0, _currentIntervalMs);
    }

    public void Stop()
    {
        _renderTimer?.Dispose();
        _renderTimer = null;
        _sensors.Stop();
        _gscoler?.Disconnect();
        _lcd.Disconnect();
        IsRunning = false;
        ConnectionStatus = "Disconnected";
        SensorStatus = "Stopped";
        _tray.UpdateState(ConnectionState.Disconnected, false);
    }

    private void ToggleStartStop()
    {
        if (IsRunning) Stop();
        else Start();
    }

    private void AdjustTimerForPage(IPage page)
    {
        int newInterval = page.PreferredIntervalMs;
        if (newInterval != _currentIntervalMs && _renderTimer != null)
        {
            _currentIntervalMs = newInterval;
            _renderTimer.Change(0, newInterval);
        }
    }

    private void RenderFrame()
    {
        // Re-entrancy guard — skip if previous frame is still rendering
        if (Interlocked.CompareExchange(ref _rendering, 1, 0) != 0)
            return;

        try
        {
            SensorStatus = _sensors.StatusMessage;

            var bitmap = _renderer.RenderFrame();

            // Queue frame for LCD send thread (non-blocking)
            lock (_lcdFrameLock)
            {
                _pendingLcdFrame?.Dispose();
                _pendingLcdFrame = (Bitmap)bitmap.Clone();
            }

            // Update preview at max ~4 FPS to keep UI responsive
            _previewSkip++;
            int previewEvery = Math.Max(1, _currentIntervalMs < 250 ? 5 : 1);
            if (_previewSkip >= previewEvery)
            {
                _previewSkip = 0;
                UpdatePreview(bitmap);
            }

            bitmap.Dispose();
            _tray.UpdateState(_lcd.State, _sensors.IsAvailable);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Render error: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _rendering, 0);
        }
    }

    private void LcdSendLoop()
    {
        LogDiag("LcdSendLoop started");
        int frameCount = 0;
        while (IsRunning)
        {
            Bitmap? frame = null;
            lock (_lcdFrameLock)
            {
                frame = _pendingLcdFrame;
                _pendingLcdFrame = null;
            }

            if (frame != null && _lcd.State == ConnectionState.Connected)
            {
                try
                {
                    _lcd.SendBitmap(frame);
                    frameCount++;
                    if (frameCount <= 3 || frameCount % 100 == 0)
                        LogDiag($"Frame {frameCount} sent ({frame.Width}x{frame.Height})");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LCD send error: {ex.Message}");
                    LogDiag($"LCD send error: {ex.Message}");
                }
                finally { frame.Dispose(); }
            }
            else
            {
                frame?.Dispose();
            }

            Thread.Sleep(16); // don't spin — check for new frames ~60x/sec
        }
    }

    private void UpdatePreview(Bitmap bitmap)
    {
        try
        {
            // Fast path: copy pixels directly into a WriteableBitmap via BMP format
            // BMP is much faster to encode/decode than PNG
            using var ms = new System.IO.MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                () => { PreviewImage = bi; });
        }
        catch { }
    }

    private void OnLcdStateChanged(object? sender, ConnectionState state)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ConnectionStatus = state switch
            {
                ConnectionState.Connected => $"Connected ({_lcd.DetectedModel})",
                ConnectionState.Reconnecting => "Reconnecting...",
                ConnectionState.Connecting => "Connecting...",
                _ => "Disconnected"
            };
        });
    }

    private static void LogDiag(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GPULCD", "diag.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _renderer.Dispose();
        _lcd.Dispose();
        _gscoler?.Dispose();
        _sensors.Dispose();
        _tray.Dispose();
        _settings.Save();
        GC.SuppressFinalize(this);
    }
}
