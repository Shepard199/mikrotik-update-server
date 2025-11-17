namespace MikroTik.UpdateServer.Models;

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Information"; // Information / Warning / Error / Debug
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
}