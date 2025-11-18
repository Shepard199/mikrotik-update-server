using System.Text.Json;

namespace MikroTik.UpdateServer.Services;

public class TimeZoneService
{
    private readonly string _configFilePath;
    private readonly Lock _lock = new();
    private TimeZoneInfo _timeZone;

    public TimeZoneService(IConfiguration config, IHostEnvironment env)
    {
        // путь до appsettings.json
        _configFilePath = Path.Combine(env.ContentRootPath, "appsettings.json");

        var tzId = config["TimeZoneId"];

        if (!string.IsNullOrWhiteSpace(tzId))
            _timeZone = SafeFindTimeZone(tzId) ?? TimeZoneInfo.Utc;
        else
            _timeZone = TimeZoneInfo.Local;
    }

    public TimeZoneInfo Current
    {
        get
        {
            lock (_lock)
            {
                return _timeZone;
            }
        }
    }

    private static TimeZoneInfo? SafeFindTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return null;
        }
    }

    public void SetTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("Time zone id is required.", nameof(timeZoneId));

        var tz = SafeFindTimeZone(timeZoneId)
                 ?? throw new TimeZoneNotFoundException($"Time zone '{timeZoneId}' not found.");

        lock (_lock)
        {
            _timeZone = tz;
            PersistTimeZoneId(timeZoneId);
        }
    }

    public DateTime GetLocalNow()
    {
        var utc = DateTime.UtcNow;
        return ConvertFromUtc(utc);
    }

    public DateTime ConvertFromUtc(DateTime utc)
    {
        switch (utc.Kind)
        {
            case DateTimeKind.Local:
                utc = utc.ToUniversalTime();
                break;
            case DateTimeKind.Unspecified:
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                break;
        }

        TimeZoneInfo tz;
        lock (_lock)
        {
            tz = _timeZone;
        }

        return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
    }

    public static IEnumerable<object> GetAllTimeZones()
    {
        return TimeZoneInfo
            .GetSystemTimeZones()
            .Select(z => new
            {
                id = z.Id,
                displayName = z.DisplayName,
                baseUtcOffsetMinutes = (int) z.BaseUtcOffset.TotalMinutes
            });
    }

    private void PersistTimeZoneId(string timeZoneId)
    {
        try
        {
            // если файла нет (например, в контейнере) — просто молча пропускаем
            if (!File.Exists(_configFilePath))
                return;

            var json = File.ReadAllText(_configFilePath);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            // верхний уровень: ключ -> значение (включая вложенные объекты как JsonElement)
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                       ?? new Dictionary<string, object?>();

            dict["TimeZoneId"] = timeZoneId;

            var newJson = JsonSerializer.Serialize(dict, options);
            File.WriteAllText(_configFilePath, newJson);
        }
        catch
        {
            // ignored
        }
    }
}