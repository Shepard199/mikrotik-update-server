namespace MikroTik.UpdateServer.Models;

public class ScheduleConfig
{
    public bool Enabled { get; set; } = true;

    public string[] DaysOfWeek { get; set; } =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    ];

    public TimeSpan CheckTime { get; set; } = new(2, 0, 0); // 2:00 AM
    public DateTime? PausedUntil { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool NotifyOnError { get; set; } = true;
}

public class ScheduleStatus(ScheduleConfig config)
{
    private ScheduleConfig Config { get; } = config;
    public DateTime NextScheduledCheck { get; set; }

    public string Status
    {
        get
        {
            if (!Config.Enabled) return "Disabled";
            if (Config.PausedUntil.HasValue && Config.PausedUntil > DateTime.UtcNow)
                return "Paused";
            return "Running";
        }
    }

    public TimeSpan TimeUntilNextCheck
    {
        get
        {
            var now = DateTime.UtcNow;
            return NextScheduledCheck > now ? NextScheduledCheck - now : TimeSpan.Zero;
        }
    }
}