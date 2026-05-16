using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using GPULCD.Models;

namespace GPULCD.Services;

/// <summary>
/// Drives the GSCOLER Z-X6 GPU bracket display via CH340 serial (COM3).
/// Protocol reverse-engineered from USBPcap capture of the official GPULCD.exe.
///
/// The bracket renders its display locally from 20-byte sensor packets.
/// It does NOT receive bitmap frames.
///
/// Connection: 115200 8N1, DTR+RTS
/// Init: send 00 00 00 00 00 FF
/// Data: 20-byte packets at ~1.5 Hz, 8-packet cycle
/// </summary>
public class GscolerBracketService : IDisposable
{
    private const int BaudRate = 115200;
    private const int SerialTimeout = 1000;
    private const byte Magic = 0xA9;
    private const byte ValueNibbleMask = 0x02; // lower nibble of byte 1

    private static readonly byte[] InitPacket = [0x00, 0x00, 0x00, 0x00, 0x00, 0xFF];

    private SerialPort? _serial;
    private readonly object _serialLock = new();
    private readonly HwInfoSensorService _sensors;
    private readonly AppSettings _settings;
    private System.Threading.Timer? _sendTimer;
    private int _sequence; // 0-7 cycle
    private int _totalSent;
    private CancellationTokenSource? _reconnectCts;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public event EventHandler<ConnectionState>? StateChanged;

    public GscolerBracketService(HwInfoSensorService sensors, AppSettings settings)
    {
        _sensors = sensors;
        _settings = settings;
    }

    public Task ConnectAsync(string? comPort = null, CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);

        var port = comPort ?? LcdService.AutoDetectComPort();
        if (port == null)
        {
            SetState(ConnectionState.Disconnected);
            throw new InvalidOperationException("No GSCOLER bracket found. Check USB connection.");
        }

        try
        {
            OpenSerial(port);
            SendInit();
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

    public void StartStreaming()
    {
        _sequence = 0;
        // ~685ms interval matches the captured packet rate of ~1.48 pkt/s
        _sendTimer = new System.Threading.Timer(_ => SendNextReading(), null, 0, 685);
    }

    public void StopStreaming()
    {
        _sendTimer?.Dispose();
        _sendTimer = null;
    }

    public void Disconnect()
    {
        StopStreaming();
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        CloseSerial();
        SetState(ConnectionState.Disconnected);
    }

    // --- Packet construction ---

    /// <summary>
    /// Build a 20-byte GSCOLER sensor packet.
    /// </summary>
    /// <param name="value">Primary sensor value (0-4095)</param>
    /// <param name="channel">Channel type: 0xC0 for channel A, 0xD0 for channel B, 0xE0 for channel C</param>
    /// <param name="secondary1">Byte 6 secondary data</param>
    /// <param name="secondary2">Byte 7 secondary data</param>
    /// <param name="sequence">Sequence counter 0-7</param>
    /// <summary>
    /// Build a 20-byte GSCOLER sensor packet.
    ///
    /// Packet layout (verified by live probing 2026-05-15):
    ///   Bytes 0-3: Bar/gauge value (encoded)
    ///   Byte 4:    0x00
    ///   Byte 5:    0xA9 (magic)
    ///   Byte 6:    CPU temp tens digit
    ///   Byte 7:    CPU temp ones digit
    ///   Byte 8:    GPU temp tens digit
    ///   Byte 9:    GPU temp ones digit
    ///   Bytes 10-11: Sequence counter (0-7)
    ///   Bytes 12-19: Zero padding
    /// </summary>
    public static byte[] BuildPacket(int barValue, int cpuTemp, int gpuTemp, byte barChannel, int leftBar, int rightBar = -1)
    {
        var pkt = new byte[20];

        // Bar value encoding: byte0 = value >> 4, byte1 = ((value & 0xF) << 4) | 0x02
        pkt[0] = (byte)(barValue >> 4);
        pkt[1] = (byte)(((barValue & 0x0F) << 4) | ValueNibbleMask);

        // Byte 2: bar channel (upper nibble) + value overflow (lower nibble)
        byte valueHigh = (byte)((barValue >> 8) & 0x0F);
        pkt[2] = (byte)(barChannel | valueHigh);

        // Byte 3: value low byte (redundant)
        pkt[3] = (byte)(barValue & 0xFF);

        pkt[4] = 0x00;
        pkt[5] = Magic;

        // CPU temp as BCD digits
        pkt[6] = (byte)(Math.Clamp(cpuTemp, 0, 99) / 10);
        pkt[7] = (byte)(Math.Clamp(cpuTemp, 0, 99) % 10);

        // GPU temp as BCD digits
        pkt[8] = (byte)(Math.Clamp(gpuTemp, 0, 99) / 10);
        pkt[9] = (byte)(Math.Clamp(gpuTemp, 0, 99) % 10);

        // Bars: byte 10 = left bar, byte 11 = right bar (0-7 each)
        pkt[10] = (byte)(leftBar & 0x07);
        pkt[11] = (byte)((rightBar >= 0 ? rightBar : leftBar) & 0x07);

        return pkt;
    }

    // --- Sensor reading + packet sending ---

    private void SendNextReading()
    {
        if (State != ConnectionState.Connected) return;

        try
        {
            var readings = _sensors.GetCurrentReadings();

            int cpuTemp = (int)Math.Round(GetSensorValue(readings, _settings.SensorMappings.GetValueOrDefault("CpuTemp", "CPU Package")));
            int gpuTemp = (int)Math.Round(GetSensorValue(readings, _settings.SensorMappings.GetValueOrDefault("GpuTemp", "GPU Temperature")));
            int cpuUsage = (int)GetSensorValue(readings, _settings.SensorMappings.GetValueOrDefault("CpuUsage", "Total CPU Usage"));
            int gpuUsage = (int)GetSensorValue(readings, _settings.SensorMappings.GetValueOrDefault("GpuUsage", "GPU Core Load"));

            // Bars (bytes 10-11) are independent: byte10 = left bar, byte11 = right bar.
            // Map fan RPMs to bar levels 0-7. CPU fan ~0-2000 RPM, GPU fan ~0-3000 RPM.
            double cpuFanRpm = GetSensorValue(readings, _settings.SensorMappings.GetValueOrDefault("CpuFan", "Nuvoton NCT6687D): CPU"));
            double gpuFanRpm = GetSensorValue(readings, _settings.SensorMappings.GetValueOrDefault("GpuFan", "GPU Fan1"));

            int cpuBar = Math.Clamp((int)(cpuFanRpm / 250), 0, 7); // 0-2000 RPM -> 0-7
            int gpuBar = Math.Clamp((int)(gpuFanRpm / 375), 0, 7); // 0-3000 RPM -> 0-7

            // Bar value (bytes 0-3): not visually active on current firmware theme.
            int barValue = 0;

            var pkt = BuildPacket(barValue, cpuTemp, gpuTemp, 0xD0, cpuBar, gpuBar);
            WriteSerial(pkt);

            // Log first few cycles for debugging
            if (_totalSent < 24)
            {
                var hex = string.Join(" ", pkt.Select(b => $"{b:x2}"));
                LogDiag($"Seq={_sequence} cpu={cpuTemp} gpu={gpuTemp} bar={barValue} | {hex}");
            }
            _totalSent++;

            _sequence = (_sequence + 1) & 0x07;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GSCOLER send error: {ex.Message}");
        }
    }

    private static double GetSensorValue(Dictionary<string, SensorReading> readings, string sensorName)
    {
        // Exact match first
        if (readings.TryGetValue(sensorName, out var reading))
            return reading.Value;

        // Fuzzy match: find first sensor containing the name
        foreach (var kvp in readings)
        {
            if (kvp.Key.Contains(sensorName, StringComparison.OrdinalIgnoreCase))
                return kvp.Value.Value;
        }

        return 0;
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
                DtrEnable = true,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One
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

    private void SendInit()
    {
        WriteSerial(InitPacket);
        Debug.WriteLine("GSCOLER: Init packet sent");
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

    private void HandleSerialError(Exception ex)
    {
        Debug.WriteLine($"GSCOLER serial error: {ex.Message}");
        CloseSerial();
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

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

                SetState(ConnectionState.Reconnecting);

                for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(Math.Min(2000 * (attempt + 1), 10000), ct);

                    var port = preferredPort ?? LcdService.AutoDetectComPort();
                    if (port == null) continue;

                    try
                    {
                        OpenSerial(port);
                        SendInit();
                        SetState(ConnectionState.Connected);
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

    private static void LogDiag(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GPULCD", "gscoler-diag.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
