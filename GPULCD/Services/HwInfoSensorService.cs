using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using GPULCD.Models;

namespace GPULCD.Services;

/// <summary>
/// Reads sensor data from HWiNFO64 shared memory.
/// Requires "Shared Memory Support" enabled in HWiNFO settings.
/// </summary>
public class HwInfoSensorService : ISensorService
{
    private const string SharedMemoryName = "Global\\HWiNFO_SENS_SM2";
    private const string MutexName = "Global\\HWiNFO_SM2_MUTEX";
    private const uint MagicValue = 0x53695748; // "HWiS" as stored in memory

    // Header offsets
    private const int OffsetMagic = 0x0000;
    private const int OffsetVersion = 0x0004;
    private const int OffsetLastUpdate = 0x000C;
    private const int OffsetSensorSectionOffset = 0x0014;
    private const int OffsetSensorElementSize = 0x0018;
    private const int OffsetSensorElementCount = 0x001C;
    private const int OffsetEntrySectionOffset = 0x0020;
    private const int OffsetEntryElementSize = 0x0024;
    private const int OffsetEntryElementCount = 0x0028;

    // Entry field offsets (within each entry)
    private const int EntryType = 0x0000;
    private const int EntrySensorIndex = 0x0004;
    private const int EntryId = 0x0008;
    private const int EntryNameOriginal = 0x000C;
    private const int EntryNameUser = 0x008C;
    private const int EntryUnit = 0x010C;
    private const int EntryValue = 0x011C;
    private const int EntryValueMin = 0x0124;
    private const int EntryValueMax = 0x012C;
    private const int EntryValueAvg = 0x0134;

    // Sensor field offsets
    private const int SensorNameOriginal = 0x0008;
    private const int SensorNameUser = 0x0088;

    private System.Threading.Timer? _pollTimer;
    private readonly Dictionary<string, SensorReading> _currentReadings = new();
    private readonly object _readingsLock = new();

    public bool IsAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "Not started";

    public event EventHandler<Dictionary<string, SensorReading>>? SensorsUpdated;

    public void Start(int intervalMs = 500)
    {
        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(_ => PollSensors(), null, 0, intervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
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
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // Validate magic
            uint magic = accessor.ReadUInt32(OffsetMagic);
            if (magic != MagicValue)
            {
                IsAvailable = false;
                StatusMessage = "HWiNFO shared memory invalid (bad magic)";
                return;
            }

            // Read header
            uint sensorOffset = accessor.ReadUInt32(OffsetSensorSectionOffset);
            uint sensorSize = accessor.ReadUInt32(OffsetSensorElementSize);
            uint sensorCount = accessor.ReadUInt32(OffsetSensorElementCount);
            uint entryOffset = accessor.ReadUInt32(OffsetEntrySectionOffset);
            uint entrySize = accessor.ReadUInt32(OffsetEntryElementSize);
            uint entryCount = accessor.ReadUInt32(OffsetEntryElementCount);

            // Read sensor names (for grouping)
            var sensorNames = new string[sensorCount];
            for (uint i = 0; i < sensorCount; i++)
            {
                long pos = sensorOffset + i * sensorSize;
                string userStr = ReadString(accessor, pos + SensorNameUser, 128);
                string origStr = ReadString(accessor, pos + SensorNameOriginal, 128);
                sensorNames[i] = string.IsNullOrEmpty(userStr) ? origStr : userStr;
            }

            // Read entries
            var newReadings = new Dictionary<string, SensorReading>();
            for (uint i = 0; i < entryCount; i++)
            {
                long pos = entryOffset + i * entrySize;

                uint sensorIdx = accessor.ReadUInt32(pos + EntrySensorIndex);
                string entryUserName = ReadString(accessor, pos + EntryNameUser, 128);
                string entryOrigName = ReadString(accessor, pos + EntryNameOriginal, 128);
                string unit = ReadString(accessor, pos + EntryUnit, 16);
                double value = accessor.ReadDouble(pos + EntryValue);
                double min = accessor.ReadDouble(pos + EntryValueMin);
                double max = accessor.ReadDouble(pos + EntryValueMax);
                double avg = accessor.ReadDouble(pos + EntryValueAvg);

                string name = string.IsNullOrEmpty(entryUserName) ? entryOrigName : entryUserName;
                string sensorGroup = sensorIdx < sensorNames.Length ? sensorNames[sensorIdx] : "Unknown";

                // Use "SensorGroup: EntryName" as key for uniqueness
                string key = $"{name}";

                // If duplicate name, prefix with sensor group
                if (newReadings.ContainsKey(key))
                    key = $"{sensorGroup}: {name}";

                newReadings[key] = new SensorReading
                {
                    Name = name,
                    Unit = unit,
                    Value = value,
                    Min = min,
                    Max = max,
                    Avg = avg
                };
            }

            lock (_readingsLock)
            {
                _currentReadings.Clear();
                foreach (var kvp in newReadings)
                    _currentReadings[kvp.Key] = kvp.Value;
            }

            IsAvailable = true;
            StatusMessage = $"HWiNFO: {entryCount} sensors";
            SensorsUpdated?.Invoke(this, newReadings);
        }
        catch (FileNotFoundException)
        {
            IsAvailable = false;
            StatusMessage = "HWiNFO not running or Shared Memory not enabled";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StatusMessage = $"HWiNFO error: {ex.Message}";
            Debug.WriteLine($"HWiNFO sensor read error: {ex}");
        }
    }

    private static string ReadString(MemoryMappedViewAccessor accessor, long position, int maxLength)
    {
        var bytes = new byte[maxLength];
        accessor.ReadArray(position, bytes, 0, maxLength);
        int nullIdx = Array.IndexOf(bytes, (byte)0);
        if (nullIdx < 0) nullIdx = maxLength;
        return Encoding.GetEncoding(1252).GetString(bytes, 0, nullIdx).Trim();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
