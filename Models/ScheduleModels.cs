namespace MikroTik.UpdateServer.Models;

public class ScheduleConfig
{
    public bool Enabled { get; set; } = true;

    public string[] DaysOfWeek { get; set; } =
    {
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    };

    public TimeSpan CheckTime { get; set; } = new(2, 0, 0); // 2:00 AM
    public DateTime? PausedUntil { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool NotifyOnError { get; set; } = true;
}

public class ScheduleStatus
{
    public ScheduleConfig Config { get; set; } = new();
    public DateTime NextScheduledCheck { get; set; }
    public bool IsPaused => Config.PausedUntil.HasValue && Config.PausedUntil > DateTime.Now;
    public TimeSpan TimeUntilNextCheck => NextScheduledCheck - DateTime.Now;
    public string Status => IsPaused ? "Paused" : Config.Enabled ? "Active" : "Disabled";
}