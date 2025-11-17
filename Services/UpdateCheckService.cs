namespace MikroTik.UpdateServer.Services;

public class UpdateCheckService(
    MikroTikUpdateService updateService,
    ScheduleService scheduleService,
    ILogger<UpdateCheckService> logger,
    IConfiguration config)
    : BackgroundService
{
    // Дата последнего ИМЕННО планового запуска (по расписанию)
    private DateOnly? _lastScheduledCheckDate;
    private PeriodicTimer? _timer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Update check service starting...");

        // Интервал проверки из конфигурации (по умолчанию 10 минут)
        var intervalMinutes = config.GetValue("UpdateCheckIntervalMinutes", 10);
        logger.LogInformation("Update check interval: {Interval} minutes", intervalMinutes);

        // Первичная проверка при старте
        try
        {
            var initialResult = await updateService.CheckAndDownloadUpdatesAsync();
            if (initialResult.downloaded > 0)
                logger.LogInformation(
                    "Initial update check completed. Downloaded {Count} files",
                    initialResult.downloaded);
            else
                logger.LogInformation(
                    "Initial update check completed. No new files.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Если нас отменили во время стартовой проверки — выходим
            logger.LogInformation("Initial update check cancelled");
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Initial update check failed");
        }

        _timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken)) await RunSingleIterationAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Update check service cancelled");
        }
    }

    private async Task RunSingleIterationAsync(CancellationToken stoppingToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Определяем, считается ли этот запуск "плановым"
        var isScheduledRun = scheduleService.ShouldRunNow() &&
                             _lastScheduledCheckDate != today;

        try
        {
            var result = await updateService.CheckAndDownloadUpdatesAsync();

            if (isScheduledRun)
            {
                _lastScheduledCheckDate = today;

                if (result.downloaded > 0)
                    logger.LogInformation(
                        "Scheduled update check downloaded {Count} files",
                        result.downloaded);
                else
                    logger.LogInformation(
                        "Scheduled update check completed - no new files");
            }
            else
            {
                // Обычная периодическая проверка
                if (result.downloaded > 0)
                    logger.LogInformation(
                        "Background update check downloaded {Count} files",
                        result.downloaded);
                else
                    logger.LogDebug(
                        "Background update check completed - no new files");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Даём наружному уровню обработать отмену
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error during {RunType} update check",
                isScheduledRun ? "scheduled" : "background");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Update check service stopping...");
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}