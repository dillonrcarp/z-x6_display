using GPULCD.Models;

namespace GPULCD.Services;

public interface ISensorService : IDisposable
{
    bool IsAvailable { get; }
    string StatusMessage { get; }

    event EventHandler<Dictionary<string, SensorReading>>? SensorsUpdated;

    void Start(int intervalMs = 500);
    void Stop();
    Dictionary<string, SensorReading> GetCurrentReadings();
    List<string> GetAvailableSensorNames();
}
