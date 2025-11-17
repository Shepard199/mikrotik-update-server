using System.Text.Json;
using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

public class ScheduleService
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ScheduleService> _logger;

    private ScheduleConfig _config;

    public ScheduleService(ILogger<ScheduleService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppContext.BaseDirectory, "schedule.json");
        _config = LoadConfig();
    }

    public ScheduleConfig GetConfig()
    {
        // Возвращаем текущую ссылку. Предполагается, что снаружи её не мутируют.
        return _config;
    }

    public ScheduleStatus GetStatus()
    {
        var nextCheck = CalculateNextCheckTime();
        return new ScheduleStatus(_config)
        {
            NextScheduledCheck = nextCheck
        };
    }

    public async Task UpdateConfigAsync(ScheduleConfig newConfig)
    {
        if (newConfig is null)
            throw new ArgumentNullException(nameof(newConfig));

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            NormalizeConfig(newConfig);
            _config = newConfig;
            await SaveConfigAsync().ConfigureAwait(false);
            _logger.LogInformation("Schedule configuration updated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating schedule configuration");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PauseAsync(TimeSpan duration)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            _config.PausedUntil = now.Add(duration);
            await SaveConfigAsync().ConfigureAwait(false);
            _logger.LogInformation(
                "Updates paused until {PausedUntilUtc} (UTC)",
                _config.PausedUntil);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing updates");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _config.PausedUntil = null;
            await SaveConfigAsync().ConfigureAwait(false);
            _logger.LogInformation("Updates resumed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming updates");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool ShouldRunNow()
    {
        if (!_config.Enabled)
            return false;

        var now = DateTime.UtcNow;

        if (_config.PausedUntil.HasValue && _config.PausedUntil > now)
            return false;

        var today = now.Date;
        var scheduledTime = today.Add(_config.CheckTime);

        // Добавьте проверку на время выполнения
        var timeWindowEnd = scheduledTime.AddMinutes(5);

        // Исправьте условие времени
        if (now >= scheduledTime && now < timeWindowEnd)
        {
            var todayName = now.DayOfWeek.ToString();
            return _config.DaysOfWeek.Contains(todayName);
        }

        return false;
    }

    private DateTime CalculateNextCheckTime()
    {
        if (!_config.Enabled)
            return DateTime.MaxValue;

        var now = DateTime.UtcNow;

        if (_config.PausedUntil.HasValue && _config.PausedUntil > now)
            return _config.PausedUntil.Value;

        var today = now.Date;
        var scheduledTime = today.Add(_config.CheckTime);
        var todayName = now.DayOfWeek.ToString();

        // Если сегодня подходящий день и время еще не наступило
        if (now < scheduledTime && _config.DaysOfWeek.Contains(todayName))
            return scheduledTime;

        // Если сегодня подходящий день, но время прошло - ищем следующий день
        var currentDay = today;
        for (var i = 1; i <= 7; i++)
        {
            var nextDay = currentDay.AddDays(i);
            var nextDayName = nextDay.DayOfWeek.ToString();

            if (_config.DaysOfWeek.Contains(nextDayName))
                return nextDay.Add(_config.CheckTime);
        }

        return DateTime.MaxValue;
    }

    private ScheduleConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaultConfig = new ScheduleConfig();
                NormalizeConfig(defaultConfig);
                SaveConfigToDisk(defaultConfig);
                _logger.LogInformation("Schedule configuration file not found. Created default at {Path}", _configPath);
                return defaultConfig;
            }

            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<ScheduleConfig>(json) ?? new ScheduleConfig();
            NormalizeConfig(config);
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading schedule configuration, using defaults");
            var fallback = new ScheduleConfig();
            NormalizeConfig(fallback);
            return fallback;
        }
    }

    private void SaveConfigToDisk(ScheduleConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving schedule configuration to disk (sync)");
            // На старте лучше не падать, так что исключение тут не пробрасываем.
        }
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_configPath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving schedule configuration");
            throw;
        }
    }

    private static void NormalizeConfig(ScheduleConfig config)
    {
        // DaysOfWeek: гарантируем, что не null и не пустой
        if (config.DaysOfWeek == null || config.DaysOfWeek.Length == 0)
            config.DaysOfWeek = Enum.GetNames(typeof(DayOfWeek));

        // PausedUntil: нормализуем к UTC, если вдруг сохранили локальное время
        if (config.PausedUntil is {Kind: DateTimeKind.Local})
            config.PausedUntil = config.PausedUntil.Value.ToUniversalTime();
    }
}