namespace BitCraftOverlay;

/// <summary>One saved start/stop rate calculation (e.g. XP gained per hour).</summary>
public class CalcEntry
{
    public string Name { get; set; } = "";
    public long StartUnix { get; set; }
    public double StartValue { get; set; }
    public long StopUnix { get; set; }
    public double StopValue { get; set; }

    public double RatePerHour => StopUnix > StartUnix
        ? (StopValue - StartValue) / (StopUnix - StartUnix) * 3600.0
        : 0;

    public string RateDisplay => $"{RatePerHour:0.##}/h";
}
