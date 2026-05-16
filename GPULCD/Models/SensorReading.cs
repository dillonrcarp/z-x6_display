namespace GPULCD.Models;

public class SensorReading
{
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Avg { get; set; }

    public string FormattedValue => Unit switch
    {
        "°C" or "°F" => $"{Value:F0}{Unit}",
        "%" => $"{Value:F0}%",
        "RPM" => $"{Value:F0}",
        "MHz" => $"{Value:F0}",
        "GB" or "MB" => $"{Value:F1} {Unit}",
        "W" => $"{Value:F1}W",
        _ => $"{Value:F1} {Unit}"
    };
}
