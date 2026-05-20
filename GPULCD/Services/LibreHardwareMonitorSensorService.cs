using System.Diagnostics;
using GPULCD.Models;
using LibreHardwareMonitor.Hardware;

namespace GPULCD.Services;

/// <summary>
/// Reads sensor data using LibreHardwareMonitor (no external app required).
/// Requires admin privileges for full sensor access.
/// </summary>
public class LibreHardwareMonitorSensorService : ISensorService
{
    private Computer? _computer;
    private System.Threading.Timer? _pollTimer;
    private readonly Dictionary<string, SensorReading> _currentReadings = new();
    private readonly object _readingsLock = new();

    public bool IsAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "Not started";

    public event EventHandler<Dictionary<string, SensorReading>>? SensorsUpdated;

    public void Start(int intervalMs = 500)
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _computer?.Close();
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsMemoryEnabled = true,
                    IsControllerEnabled = false
                };
                _computer.Open();

                // Check if Ring0 driver loaded by looking for CPU temperature sensors
                bool hasCpuTemp = false;
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature
                            && sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            hasCpuTemp = true;
                            break;
                        }
                    }
                    if (hasCpuTemp) break;

                    foreach (var sub in hw.SubHardware)
                    {
                        sub.Update();
                        foreach (var sensor in sub.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature)
                            {
                                hasCpuTemp = true;
                                break;
                            }
                        }
                        if (hasCpuTemp) break;
                    }
                    if (hasCpuTemp) break;
                }

                if (hasCpuTemp || attempt == maxRetries)
                {
                    IsAvailable = true;
                    StatusMessage = hasCpuTemp
                        ? $"LHM: ready (attempt {attempt})"
                        : $"LHM: partial sensors (Ring0 driver may have failed)";
                    Debug.WriteLine($"LHM init attempt {attempt}: hasCpuTemp={hasCpuTemp}");
                    break;
                }

                Debug.WriteLine($"LHM init attempt {attempt}: no CPU temp, retrying...");
                _computer.Close();
                Thread.Sleep(1500);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LHM init attempt {attempt} error: {ex.Message}");
                if (attempt == maxRetries)
                {
                    IsAvailable = false;
                    StatusMessage = $"LHM init failed: {ex.Message}";
                    return;
                }
                Thread.Sleep(1500);
            }
        }

        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(_ => PollSensors(), null, 0, intervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        try
        {
            _computer?.Close();
        }
        catch { }

        _computer = null;
    }

    public Dictionary<string, SensorReading> GetCurrentReadings()
    {
        lock (_readingsLock)
        {
            return new Dictionary<string, SensorReading>(_currentReadings);
        }
    }

    public List<string> GetAvailableSensorNames()
    {
        lock (_readingsLock)
        {
            return _currentReadings.Keys.ToList();
        }
    }

    private void PollSensors()
    {
        if (_computer == null) return;

        try
        {
            var newReadings = new Dictionary<string, SensorReading>();
            var collidedNames = new HashSet<string>();

            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();

                foreach (var sub in hardware.SubHardware)
                    sub.Update();

                CollectSensors(hardware, newReadings, collidedNames);

                foreach (var sub in hardware.SubHardware)
                    CollectSensors(sub, newReadings, collidedNames);
            }

            lock (_readingsLock)
            {
                _currentReadings.Clear();
                foreach (var kvp in newReadings)
                    _currentReadings[kvp.Key] = kvp.Value;
            }

            IsAvailable = true;
            StatusMessage = $"LHM: {newReadings.Count} sensors";
            SensorsUpdated?.Invoke(this, newReadings);
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StatusMessage = $"LHM error: {ex.Message}";
            Debug.WriteLine($"LHM poll error: {ex}");
        }
    }

    private static void CollectSensors(IHardware hardware, Dictionary<string, SensorReading> readings,
        HashSet<string> collidedNames)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value == null) continue;

            string unit = sensor.SensorType switch
            {
                SensorType.Temperature => "\u00B0C",
                SensorType.Fan => "RPM",
                SensorType.Load => "%",
                SensorType.Clock => "MHz",
                SensorType.Voltage => "V",
                SensorType.Power => "W",
                SensorType.Data => "GB",
                SensorType.SmallData => "MB",
                SensorType.Throughput => "B/s",
                SensorType.Current => "A",
                SensorType.Energy => "Wh",
                SensorType.Factor => "",
                SensorType.Frequency => "Hz",
                SensorType.Level => "%",
                SensorType.Noise => "dBA",
                SensorType.TimeSpan => "s",
                SensorType.Humidity => "%",
                _ => ""
            };

            string baseName = sensor.Name;
            string key;

            if (collidedNames.Contains(baseName))
            {
                // This name already had a collision -- always use typed key
                key = $"{baseName} ({sensor.SensorType})";
                if (readings.ContainsKey(key))
                    key = $"{hardware.Name}: {baseName} ({sensor.SensorType})";
            }
            else if (readings.TryGetValue(baseName, out var existing))
            {
                // First collision on this name -- rename existing entry, mark name as collided
                collidedNames.Add(baseName);

                string existingType = InferTypeSuffix(existing.Unit);
                string existingNewKey = $"{baseName} ({existingType})";
                if (!readings.ContainsKey(existingNewKey))
                {
                    readings[existingNewKey] = existing;
                    readings.Remove(baseName);
                }

                key = $"{baseName} ({sensor.SensorType})";
                if (readings.ContainsKey(key))
                    key = $"{hardware.Name}: {baseName} ({sensor.SensorType})";
            }
            else
            {
                key = baseName;
            }

            readings[key] = new SensorReading
            {
                Name = sensor.Name,
                Unit = unit,
                Value = sensor.Value.Value,
                Min = sensor.Min ?? 0,
                Max = sensor.Max ?? 0,
                Avg = sensor.Value.Value // LHM doesn't track running avg; use current
            };
        }
    }

    private static string InferTypeSuffix(string unit) => unit switch
    {
        "\u00B0C" => "Temperature",
        "RPM" => "Fan",
        "MHz" => "Clock",
        "V" => "Voltage",
        "W" => "Power",
        "A" => "Current",
        "GB" => "Data",
        "MB" => "SmallData",
        "B/s" => "Throughput",
        "Wh" => "Energy",
        "Hz" => "Frequency",
        "dBA" => "Noise",
        "s" => "TimeSpan",
        "%" => "Load",
        _ => "Unknown"
    };

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
