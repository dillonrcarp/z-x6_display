namespace GPULCD.Models;

public class AlertThreshold
{
    public double? WarnAt { get; set; }
    public double? CritAt { get; set; }
    public double? MinRpm { get; set; } // alert if below this (fan failure)

    public AlertLevel Check(double value)
    {
        if (MinRpm.HasValue && value < MinRpm.Value)
            return AlertLevel.Critical;
        if (CritAt.HasValue && value >= CritAt.Value)
            return AlertLevel.Critical;
        if (WarnAt.HasValue && value >= WarnAt.Value)
            return AlertLevel.Warning;
        return AlertLevel.Ok;
    }
}

public enum AlertLevel { Ok, Warning, Critical }

public record ActiveAlert(string SensorSlot, string DisplayName, double Value, string Unit, AlertLevel Level, double Threshold);
