using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using GPULCD.Models;

namespace GPULCD.Services;

/// <summary>
/// Turing Smart Screen Rev A protocol implementation.
/// Supports 3.5" (320x480), 5" (480x800), 7" (600x1024) USB LCD panels.
/// </summary>
public class LcdService : ILcdService
{
    private enum Command : byte
    {
        Reset = 101,
        Clear = 102,
        ToBlack = 103,
        ScreenOff = 108,
        ScreenOn = 109,
        SetBrightness = 110,
        SetOrientation = 121,
        SetMirror = 122,
        DisplayPixels = 195,
        DisplayBitmap = 197,
        Hello = 69
    }

    private const int BaudRate = 115200;
    private const int SerialTimeout = 1000;
    private const string TargetSerialNumber = "USB35INCHIPSV2";
    private const int TargetVid = 0x1A86;
    private const int TargetPid = 0x5722;

    private SerialPort? _serial;
    private readonly object _serialLock = new();
    private CancellationTokenSource? _reconnectCts;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ScreenModel DetectedModel { get; private set; } = ScreenModel.Unknown;
    public int DisplayWidth { get; private set; } = 320;
    public int DisplayHeight { get; private set; } = 480;
    public event EventHandler<ConnectionState>? StateChanged;

    public Task ConnectAsync(string? comPort = null, CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);

        var port = comPort ?? AutoDetectComPort();
        if (port == null)
        {
            SetState(ConnectionState.Disconnected);
            throw new InvalidOperationException("No Turing Smart Screen found. Check USB connection.");
        }

        try
        {
            OpenSerial(port);
            SendHello();
            SetState(ConnectionState.Connected);
            StartReconnectWatcher(comPort);
        }
        catch
        {
            SetState(ConnectionState.Disconnected);
            throw;
        }

        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        CloseSerial();
        SetState(ConnectionState.Disconnected);
    }

    public void SetBrightness(int levelPercent)
    {
        levelPercent = Math.Clamp(levelPercent, 0, 100);
        // Display scale: 0 = brightest, 255 = darkest
        int level = 255 - (int)(levelPercent / 100.0 * 255);
        SendCommand(Command.SetBrightness, level, 0, 0, 0);
    }

    public void SetOrientation(DisplayOrientation orientation)
    {
        int width = orientation is DisplayOrientation.Landscape or DisplayOrientation.ReverseLandscape
            ? Math.Max(DisplayWidth, DisplayHeight)
            : Math.Min(DisplayWidth, DisplayHeight);
        int height = orientation is DisplayOrientation.Landscape or DisplayOrientation.ReverseLandscape
            ? Math.Min(DisplayWidth, DisplayHeight)
            : Math.Max(DisplayWidth, DisplayHeight);

        var buffer = new byte[16];
        PackCoordinates(buffer, 0, 0, 0, 0);
        buffer[5] = (byte)Command.SetOrientation;
        buffer[6] = (byte)((int)orientation + 100);
        buffer[7] = (byte)(width >> 8);
        buffer[8] = (byte)(width & 0xFF);
        buffer[9] = (byte)(height >> 8);
        buffer[10] = (byte)(height & 0xFF);

        WriteSerial(buffer);

        // Update display dimensions to match the new orientation
        DisplayWidth = width;
        DisplayHeight = height;
    }

    public void SendBitmap(Bitmap bitmap, int x = 0, int y = 0)
    {
        int w = Math.Min(bitmap.Width, DisplayWidth - x);
        int h = Math.Min(bitmap.Height, DisplayHeight - y);

        if (w <= 0 || h <= 0) return;

        int x1 = x + w - 1;
        int y1 = y + h - 1;

        var rgb565 = BitmapToRgb565(bitmap, w, h);

        SendCommand(Command.DisplayBitmap, x, y, x1, y1);

        // Send in chunks of width * 8 bytes
        int chunkSize = DisplayWidth * 8;
        for (int offset = 0; offset < rgb565.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, rgb565.Length - offset);
            WriteSerial(rgb565.AsSpan(offset, len));
        }
    }

    public void ScreenOn() => SendCommand(Command.ScreenOn, 0, 0, 0, 0);
    public void ScreenOff() => SendCommand(Command.ScreenOff, 0, 0, 0, 0);

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }

    // --- Protocol internals ---

    private void SendCommand(Command cmd, int x, int y, int ex, int ey)
    {
        var buffer = new byte[6];
        PackCoordinates(buffer, x, y, ex, ey);
        buffer[5] = (byte)cmd;
        WriteSerial(buffer);
    }

    private static void PackCoordinates(byte[] buffer, int x, int y, int ex, int ey)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)(((x & 3) << 6) | (y >> 4));
        buffer[2] = (byte)(((y & 15) << 4) | (ex >> 6));
        buffer[3] = (byte)(((ex & 63) << 2) | (ey >> 8));
        buffer[4] = (byte)(ey & 255);
    }

    private void SendHello()
    {
        var hello = new byte[] { 69, 69, 69, 69, 69, 69 };
        WriteSerial(hello);

        byte[]? response = null;
        try
        {
            response = ReadSerial(6, timeout: 2000);
        }
        catch { /* Turing 3.5" doesn't respond */ }

        FlushInput();

        // Log the raw hello response for protocol debugging
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GPULCD", "diag.log");
            var respStr = response != null
                ? string.Join(" ", response.Select(b => $"0x{b:X2}"))
                : "null (no response/timeout)";
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] Hello response ({response?.Length ?? 0} bytes): {respStr}\n");
        }
        catch { }

        if (response != null && response.Length == 6)
        {
            if (response.All(b => b == 0x01))
            {
                DetectedModel = ScreenModel.UsbMonitor35;
                DisplayWidth = 320; DisplayHeight = 480;
            }
            else if (response.All(b => b == 0x02))
            {
                DetectedModel = ScreenModel.UsbMonitor5;
                DisplayWidth = 480; DisplayHeight = 800;
            }
            else if (response.All(b => b == 0x03))
            {
                DetectedModel = ScreenModel.UsbMonitor7;
                DisplayWidth = 600; DisplayHeight = 1024;
            }
            else
            {
                DetectedModel = ScreenModel.Turing35;
                DisplayWidth = 320; DisplayHeight = 480;
            }
        }
        else
        {
            DetectedModel = ScreenModel.Turing35;
            DisplayWidth = 320; DisplayHeight = 480;
        }

        Debug.WriteLine($"LCD detected: {DetectedModel} ({DisplayWidth}x{DisplayHeight})");
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GPULCD", "diag.log");
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] LCD connected on {_serial?.PortName}, model={DetectedModel}, res={DisplayWidth}x{DisplayHeight}\n");
        }
        catch { }
    }

    // --- Serial port management ---

    private void OpenSerial(string portName)
    {
        lock (_serialLock)
        {
            CloseSerial();
            _serial = new SerialPort(portName, BaudRate)
            {
                ReadTimeout = SerialTimeout,
                WriteTimeout = SerialTimeout,
                Handshake = Handshake.RequestToSend,
                DtrEnable = true
            };
            _serial.Open();
        }
    }

    private void CloseSerial()
    {
        lock (_serialLock)
        {
            if (_serial?.IsOpen == true)
            {
                try { _serial.Close(); } catch { }
            }
            _serial?.Dispose();
            _serial = null;
        }
    }

    private void WriteSerial(byte[] data)
    {
        lock (_serialLock)
        {
            if (_serial?.IsOpen != true)
                throw new InvalidOperationException("Serial port not connected");

            try
            {
                _serial.Write(data, 0, data.Length);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                HandleSerialError(ex);
                throw;
            }
        }
    }

    private void WriteSerial(ReadOnlySpan<byte> data)
    {
        WriteSerial(data.ToArray());
    }

    private byte[]? ReadSerial(int count, int timeout = 2000)
    {
        lock (_serialLock)
        {
            if (_serial?.IsOpen != true) return null;

            var oldTimeout = _serial.ReadTimeout;
            _serial.ReadTimeout = timeout;
            try
            {
                var buffer = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = _serial.Read(buffer, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
                return read == count ? buffer : null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                _serial.ReadTimeout = oldTimeout;
            }
        }
    }

    private void FlushInput()
    {
        lock (_serialLock)
        {
            if (_serial?.IsOpen == true)
            {
                try { _serial.DiscardInBuffer(); } catch { }
            }
        }
    }

    // --- Auto-detection ---

    public static string? AutoDetectComPort()
    {
        // Method 1: Check serial port properties directly
        foreach (var portName in SerialPort.GetPortNames())
        {
            // We'll use WMI to match VID/PID
        }

        // Method 2: WMI query for USB serial devices
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%COM%'");

            foreach (var obj in searcher.Get())
            {
                var deviceId = obj["DeviceID"]?.ToString() ?? "";
                var caption = obj["Caption"]?.ToString() ?? "";

                // Match by VID/PID
                if (deviceId.Contains($"VID_{TargetVid:X4}", StringComparison.OrdinalIgnoreCase) &&
                    deviceId.Contains($"PID_{TargetPid:X4}", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract COM port from caption like "USB Serial Device (COM3)"
                    var match = System.Text.RegularExpressions.Regex.Match(caption, @"\(COM(\d+)\)");
                    if (match.Success)
                        return $"COM{match.Groups[1].Value}";
                }

                // Match by serial number in device ID
                if (deviceId.Contains(TargetSerialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(caption, @"\(COM(\d+)\)");
                    if (match.Success)
                        return $"COM{match.Groups[1].Value}";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WMI COM port detection failed: {ex.Message}");
        }

        return null;
    }

    // --- Reconnection ---

    private void StartReconnectWatcher(string? preferredPort)
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;

        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);

                lock (_serialLock)
                {
                    if (_serial?.IsOpen == true) continue;
                }

                // Connection lost — try to reconnect
                SetState(ConnectionState.Reconnecting);
                Debug.WriteLine("LCD disconnected, attempting reconnect...");

                for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(Math.Min(2000 * (attempt + 1), 10000), ct);

                    var port = preferredPort ?? AutoDetectComPort();
                    if (port == null) continue;

                    try
                    {
                        OpenSerial(port);
                        SendHello();
                        SetState(ConnectionState.Connected);
                        Debug.WriteLine($"LCD reconnected on {port}");
                        break;
                    }
                    catch
                    {
                        CloseSerial();
                    }
                }
            }
        }, ct);
    }

    private void HandleSerialError(Exception ex)
    {
        Debug.WriteLine($"Serial error: {ex.Message}");
        CloseSerial();
        // Reconnect watcher will pick this up
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    // --- Bitmap conversion ---

    /// <summary>
    /// Convert a System.Drawing.Bitmap to RGB565 little-endian byte array.
    /// </summary>
    public static byte[] BitmapToRgb565(Bitmap bitmap, int width, int height)
    {
        var result = new byte[width * height * 2];
        var rect = new Rectangle(0, 0, Math.Min(width, bitmap.Width), Math.Min(height, bitmap.Height));

        BitmapData? bmpData = null;
        try
        {
            bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = bmpData.Stride;
            int pixelSize = 4; // ARGB = 4 bytes

            unsafe
            {
                byte* scan0 = (byte*)bmpData.Scan0;

                for (int y = 0; y < rect.Height; y++)
                {
                    byte* row = scan0 + y * stride;
                    int resultRowOffset = y * width * 2;

                    for (int x = 0; x < rect.Width; x++)
                    {
                        int px = x * pixelSize;
                        byte b = row[px];     // Blue
                        byte g = row[px + 1]; // Green
                        byte r = row[px + 2]; // Red

                        // RGB565: RRRRR GGGGGG BBBBB
                        ushort rgb565 = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

                        // Little-endian
                        int idx = resultRowOffset + x * 2;
                        result[idx] = (byte)(rgb565 & 0xFF);
                        result[idx + 1] = (byte)(rgb565 >> 8);
                    }
                }
            }
        }
        finally
        {
            if (bmpData != null)
                bitmap.UnlockBits(bmpData);
        }

        return result;
    }
}
