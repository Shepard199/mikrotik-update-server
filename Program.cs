using System.Runtime.InteropServices;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using MikroTik.UpdateServer.Models;
using MikroTik.UpdateServer.Services;

namespace MikroTik.UpdateServer;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // сюда добавь внешний прокси, если он есть
            // options.KnownProxies.Add(IPAddress.Parse("172.27.0.1"));
        });

        // DI
        builder.Services.AddSingleton<MikroTikUpdateService>();
        builder.Services.AddSingleton<ScheduleService>();
        builder.Services.AddHostedService<UpdateCheckService>();
        builder.Services.AddSingleton<ILogStore, InMemoryLogStore>();
        builder.Services.AddSingleton<ILoggerProvider, LogStoreLoggerProvider>();

        // Новый сервис часового пояса
        builder.Services.AddSingleton<TimeZoneService>();

        builder.Services.AddHttpClient("MikroTikDiagnostics", client => { client.Timeout = TimeSpan.FromSeconds(5); });

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<GzipCompressionProvider>();
            options.Providers.Add<BrotliCompressionProvider>();
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        var logStore = app.Services.GetRequiredService<ILogStore>();
        app.UseExceptionHandler("/error");

        var baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");
        Console.WriteLine($"[STARTUP] Base folder path: {baseFolder}");
        Console.WriteLine($"[STARTUP] Base folder exists: {Directory.Exists(baseFolder)}");

        if (Directory.Exists(baseFolder))
        {
            var files = Directory.GetFiles(baseFolder);
            Console.WriteLine($"[STARTUP] Files in base folder: {string.Join(", ", files.Select(Path.GetFileName))}");
        }

        app.UseResponseCompression();
        app.UseCors();

        // Лог запросов
        app.Use(async (context, next) =>
        {
            var startTime = DateTime.UtcNow;
            var path = context.Request.Path;
            var method = context.Request.Method;

            // Пытаемся взять оригинальный IP из X-Forwarded-For, если есть
            var realIp = context.Request.Headers["X-Forwarded-For"].ToString();
            if (string.IsNullOrWhiteSpace(realIp))
            {
                realIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            }
            else
            {
                // Если несколько, берём первый
                var commaIndex = realIp.IndexOf(',');
                if (commaIndex > 0)
                    realIp = realIp[..commaIndex].Trim();
                else
                    realIp = realIp.Trim();
            }

            var remotePort = context.Connection.RemotePort;

            // Логический "серверный" IP — можно зашить или взять из конфигурации
            var serverIp = context.Connection.LocalIpAddress?.ToString() ?? "unknown";
            var serverPort = context.Connection.LocalPort;

            var ipInfo = $"{realIp}:{remotePort} -> {serverIp}:{serverPort}";

            try
            {
                await next();

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;
                var statusCode = context.Response.StatusCode;

                Console.WriteLine(
                    $"[{endTime:yyyy-MM-dd HH:mm:ss}] {ipInfo} {method} {path} -> {statusCode} ({duration.TotalMilliseconds:F0}ms)");

                var level = statusCode >= 500
                    ? "Error"
                    : statusCode >= 400
                        ? "Warning"
                        : "Information";

                logStore.Add(new LogEntry
                {
                    Timestamp = endTime,
                    Level = level,
                    Source = "HTTP",
                    Message = $"{ipInfo} {method} {path} -> {statusCode} ({duration.TotalMilliseconds:F0}ms)"
                });
            }
            catch (Exception ex)
            {
                var errorTime = DateTime.UtcNow;

                Console.WriteLine(
                    $"[{errorTime:yyyy-MM-dd HH:mm:ss}] [ERROR] {ipInfo} {method} {path}: {ex.Message}");

                logStore.Add(new LogEntry
                {
                    Timestamp = errorTime,
                    Level = "Error",
                    Source = "HTTP",
                    Message = $"{ipInfo} {method} {path} -> exception",
                    Exception = ex.ToString()
                });

                throw;
            }
        });

        // Безопасные заголовки
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");

            if (context.Request.IsHttps)
                context.Response.Headers.Append(
                    "Strict-Transport-Security",
                    "max-age=31536000; includeSubDomains");

            await next.Invoke();
        });

        // Cache-Control
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.EndsWith(".npk") == true ||
                context.Request.Path.Value?.EndsWith(".zip") == true)
                context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
            else if (context.Request.Path.Value?.StartsWith("/api/") == true)
                context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");

            await next.Invoke();
        });

        app.Use(async (context, next) =>
        {
            var tz = context.RequestServices.GetRequiredService<TimeZoneService>();

            if (!context.Response.HasStarted)
            {
                var utcNow = DateTime.UtcNow;
                var localNow = tz.GetLocalNow();

                context.Response.Headers["X-Server-TimeUtc"] = utcNow.ToString("o");
                context.Response.Headers["X-Server-TimeLocal"] = localNow.ToString("o");
                context.Response.Headers["X-Server-TimeZoneId"] = tz.Current.Id;
            }

            await next();
        });

        #region Api

        // API
        var api = app.MapGroup("/api");

        api.MapGet("/versions", GetVersions);
        api.MapGet("/status", GetStatus);
        api.MapPost("/update-check", TriggerUpdateCheck);
        api.MapPost("/set-active-version/{version}", SetActiveVersion);
        api.MapDelete("/remove-version/{version}", RemoveVersion);
        api.MapGet("/download/{version}/{filename}", DownloadFile);
        api.MapGet("/versions/history", GetVersionHistory);
        api.MapGet("/changelog", GetGlobalChangelog);
        api.MapGet("/changelog/{version}", GetVersionChangelog);

        // ===== ДИАГНОСТИКА =====
        api.MapGet("/diagnostics", GetDiagnostics);

        // ===== LOGS =====
        api.MapGet("/logs", (string? level, string? search, int? take, ILogStore store, TimeZoneService tz) =>
        {
            var logs = store.Query(level, search, take ?? 100);

            // Добавляем локальное время по текущему часовому поясу
            var projected = logs.Select(l => new
            {
                l.Level,
                l.Source,
                l.Message,
                l.Exception,
                timestampUtc = l.Timestamp,
                timestampLocal = tz.ConvertFromUtc(l.Timestamp)
            });

            return Results.Ok(new {logs = projected});
        });

        api.MapGet("/logs/stats", (ILogStore store, TimeZoneService tz) =>
        {
            var stats = store.GetStats();

            var oldestUtc = stats.OldestEntry;
            var newestUtc = stats.NewestEntry;

            if (stats.TotalEntries == 0 || oldestUtc is null || newestUtc is null)
                return Results.Ok(new
                {
                    stats.TotalEntries,
                    stats.InfoCount,
                    stats.WarningCount,
                    stats.ErrorCount,
                    oldestEntryUtc = oldestUtc,
                    newestEntryUtc = newestUtc,
                    oldestEntryLocal = (DateTime?) null,
                    newestEntryLocal = (DateTime?) null,
                    timeZone = tz.Current.Id
                });

            // Здесь уже точно не null
            var oldestLocal = tz.ConvertFromUtc(oldestUtc.Value);
            var newestLocal = tz.ConvertFromUtc(newestUtc.Value);

            return Results.Ok(new
            {
                stats.TotalEntries,
                stats.InfoCount,
                stats.WarningCount,
                stats.ErrorCount,
                oldestEntryUtc = oldestUtc,
                newestEntryUtc = newestUtc,
                oldestEntryLocal = (DateTime?) oldestLocal,
                newestEntryLocal = (DateTime?) newestLocal,
                timeZone = tz.Current.Id
            });
        });

        api.MapGet("/logs/download", (ILogStore store) =>
        {
            var zipBytes = store.ExportAsZip();
            var fileName = $"logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            return Results.File(zipBytes, "application/zip", fileName);
        });

        // ===== Schedule =====
        api.MapGet("/schedule", GetSchedule);
        api.MapGet("/schedule/status", GetScheduleStatus);
        api.MapPost("/schedule", UpdateSchedule);
        api.MapPost("/schedule/pause", PauseSchedule);
        api.MapPost("/schedule/resume", ResumeSchedule);

        // Healthcheck (тут сразу и UTC, и локальное время)
        app.MapGet("/health", (TimeZoneService tz) => Results.Ok(new
        {
            status = "healthy",
            timestampUtc = DateTime.UtcNow,
            timestampLocal = tz.GetLocalNow(),
            timeZone = tz.Current.Id
        }));

        // ===== Settings / Architectures =====
        api.MapGet("/settings/arches", GetAllowedArches);
        api.MapPost("/settings/arches", UpdateAllowedArches);

        // ===== Settings / TimeZone =====
        api.MapGet("/settings/timezone", GetTimeZone);
        api.MapPost("/settings/timezone", UpdateTimeZone);
        api.MapGet("/settings/timezone/list", GetTimeZoneList);

        // Специальные маршруты для MikroTik обновлений (эмулируют официальные пути)
        app.MapMethods("/routeros/{filename}", ["GET", "HEAD"], ServeMikroTikFile);
        app.MapMethods("/routeros/{version}/{filename}", ["GET", "HEAD"], ServeMikroTikFile);

        #endregion

        // Статика — wwwroot рядом с exe (ДОЛЖНА БЫТЬ ПОСЛЕ ВСЕХ API МАРШРУТОВ)
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRoot),
            RequestPath = "",
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream"
        });

        // Корень на UI
        app.MapGet("/", () => Results.Redirect("/index.html"));

        // Обработка ошибок JSON-эндпоинтом
        app.MapGet("/error", HandleError);

        try
        {
            Console.WriteLine(
                "\n" +
                "┌────────────────────────────────────────────────────────┐\n" +
                "│   MikroTik ROS Local Update Server v1.0                │\n" +
                "│   Powered by Shepard199                                │\n" +
                "└────────────────────────────────────────────────────────┘\n");

            app.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL ERROR: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static IResult GetAllowedArches(MikroTikUpdateService service)
    {
        var arches = service.GetAllowedArches();
        return Results.Ok(arches);
    }

    private static async Task<IResult> UpdateAllowedArches(
        MikroTikUpdateService service,
        string[] arches)
    {
        try
        {
            await service.UpdateAllowedArchesAsync(arches);
            return Results.Ok(new {message = "Allowed architectures updated successfully"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating allowed architectures: {ex.Message}");
        }
    }

    // Schedule
    private static IResult GetSchedule(ScheduleService scheduleService)
    {
        var config = scheduleService.GetConfig();
        return Results.Ok(config);
    }

    private static IResult GetScheduleStatus(ScheduleService scheduleService)
    {
        var status = scheduleService.GetStatus();
        return Results.Ok(status);
    }

    private static async Task<IResult> UpdateSchedule(ScheduleService scheduleService, ScheduleConfig config)
    {
        try
        {
            await scheduleService.UpdateConfigAsync(config);
            return Results.Ok(new {message = "Schedule updated successfully"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating schedule: {ex.Message}");
        }
    }

    private static async Task<IResult> PauseSchedule(ScheduleService scheduleService, [FromQuery] int hours)
    {
        try
        {
            await scheduleService.PauseAsync(TimeSpan.FromHours(hours));
            return Results.Ok(new {message = $"Updates paused for {hours} hours"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error pausing schedule: {ex.Message}");
        }
    }

    private static async Task<IResult> ResumeSchedule(ScheduleService scheduleService)
    {
        try
        {
            await scheduleService.ResumeAsync();
            return Results.Ok(new {message = "Updates resumed"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error resuming schedule: {ex.Message}");
        }
    }

    private static async Task<IResult> GetDiagnostics(
        MikroTikUpdateService service,
        IHttpClientFactory httpClientFactory,
        TimeZoneService tz)
    {
        try
        {
            // Пытаемся подключиться к MikroTik серверам
            var client = httpClientFactory.CreateClient("MikroTikDiagnostics");

            var connectivity = new
            {
                mikrotikServer = "unknown",
                details = "Not tested"
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://upgrade.mikrotik.com/");
                var response = await client.SendAsync(request);
                connectivity = new
                {
                    mikrotikServer = response.IsSuccessStatusCode ? "✓ Connected" : "✗ Failed",
                    details = $"HTTP {(int) response.StatusCode}"
                };
            }
            catch (HttpRequestException ex)
            {
                connectivity = new
                {
                    mikrotikServer = "✗ Network Error",
                    details = ex.Message
                };
            }
            catch (TaskCanceledException)
            {
                connectivity = new
                {
                    mikrotikServer = "✗ Timeout",
                    details = "Connection timed out (5 seconds)"
                };
            }

            var diagnostics = new
            {
                timestampUtc = DateTime.UtcNow,
                timestampLocal = tz.GetLocalNow(),
                timeZone = tz.Current.Id,
                server = new
                {
                    framework = ".NET " + RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    processorCount = Environment.ProcessorCount,
                    workingDirectory = AppContext.BaseDirectory
                },
                network = connectivity,
                versions = await service.GetVersionsInfoAsync(),
                status = await service.GetStatusAsync()
            };

            return Results.Ok(diagnostics);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    code = "diagnostics_error",
                    message = ex.Message,
                    timestampUtc = DateTime.UtcNow
                },
                statusCode: 500);
        }
    }

    private static async Task<IResult> ServeMikroTikFile(
        string? version,
        string? filename,
        MikroTikUpdateService service,
        HttpContext context)
    {
        Console.WriteLine($"[DEBUG] ServeMikroTikFile called: version='{version}', filename='{filename}'");

        // Защита от пустого имени
        if (string.IsNullOrEmpty(filename))
        {
            Console.WriteLine("[DEBUG] Filename required but not provided");
            return Results.BadRequest("Filename required");
        }

        // 1. Pointer-файлы (LATEST.6, NEWEST6.stable, NEWESTa6.long-term и т.п.)
        // ОБРАБАТЫВАЕМ ВНЕ ЗАВИСИМОСТИ ОТ ВЕРСИИ В URL!
        if (IsPointerFile(filename))
        {
            Console.WriteLine($"[DEBUG] Processing pointer file request: {filename}");

            var content = service.GetPointerFileContent(filename);
            if (content is null)
            {
                var req = version is null
                    ? $"routeros/{filename}"
                    : $"routeros/{version}/{filename}";

                Console.WriteLine($"[DEBUG] Pointer content not available for: {req}");
                return Results.NotFound(new
                {
                    error = "Pointer not available",
                    requested = req
                });
            }

            return Results.Text(content, "text/plain; charset=utf-8");
        }

        // 2. Реальные файлы с версией: /routeros/{version}/{filename}
        if (!string.IsNullOrEmpty(version))
        {
            Console.WriteLine($"[DEBUG] Processing versioned file: {version}/{filename}");

            if (version.Contains("..") || version.Contains("\\") || version.Contains("/") ||
                filename.Contains("..") || filename.Contains("\\") || filename.Contains("/"))
            {
                Console.WriteLine($"[DEBUG] Security violation detected for: {version}/{filename}");
                return Results.StatusCode(403);
            }

            string? filePath;

            if (filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[DEBUG] Looking for CHANGELOG in version: {version}");
                filePath = await service.GetChangelogPathAsync(version);
            }
            else if (filename.Equals("packages.csv", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[DEBUG] Looking for packages.csv for version: {version}");
                filePath = await service.GetPackagesCsvPathAsync(version);
            }
            else
            {
                // Для файлов прошивок ищем в v6/v7
                Console.WriteLine($"[DEBUG] Looking for firmware file: {version}/{filename}");
                filePath = await service.GetFilePathAsync(version, filename);
            }

            Console.WriteLine($"[DEBUG] Final filePath: {filePath}");
            Console.WriteLine($"[DEBUG] File exists: {File.Exists(filePath ?? string.Empty)}");

            if (filePath is null || !File.Exists(filePath))
            {
                Console.WriteLine($"[DEBUG] File not found: routeros/{version}/{filename}");
                return Results.NotFound(new
                {
                    error = "File not found",
                    requested = $"routeros/{version}/{filename}"
                });
            }

            return await ServePhysicalFile(filePath, filename);
        }

        // 3. Одиночные файлы: /routeros/{filename}
        Console.WriteLine($"[DEBUG] Processing regular file: {filename}");

        if (filename.Contains("..") || filename.Contains("\\") || filename.Contains("/"))
        {
            Console.WriteLine($"[DEBUG] Security violation detected for: {filename}");
            return Results.StatusCode(403);
        }

        string? filePathRegular;

        // Для глобального CHANGELOG
        if (filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[DEBUG] Looking for global CHANGELOG");
            filePathRegular = await service.GetGlobalChangelogPathAsync();
        }
        else
        {
            Console.WriteLine($"[DEBUG] Looking for regular file: {filename}");
            filePathRegular = await service.GetFilePathAsync(filename);
        }

        Console.WriteLine($"[DEBUG] Final filePath: {filePathRegular}");
        Console.WriteLine($"[DEBUG] File exists: {File.Exists(filePathRegular ?? string.Empty)}");

        if (filePathRegular is null || !File.Exists(filePathRegular))
        {
            Console.WriteLine($"[DEBUG] File not found: {filename}");
            return Results.NotFound(new
            {
                error = "File not found",
                requested = filename
            });
        }

        return await ServePhysicalFile(filePathRegular, filename);
    }

    // Вспомогательный метод для определения pointer-файлов
    private static bool IsPointerFile(string filename)
    {
        var lowerFilename = filename.ToLowerInvariant();

        // Проверка основных паттернов
        return lowerFilename.StartsWith("latest.") ||
               lowerFilename.StartsWith("newest6") ||
               lowerFilename.StartsWith("newest7") ||
               lowerFilename.StartsWith("newesta6") ||
               lowerFilename.StartsWith("newesta7") ||
               (lowerFilename.Contains("stable") && !lowerFilename.Contains('.')) ||
               (lowerFilename.Contains("long-term") && !lowerFilename.Contains('.')) ||
               (lowerFilename.Contains("testing") && !lowerFilename.Contains('.')) ||
               (lowerFilename.Contains("development") && !lowerFilename.Contains('.'));
    }

    // Вспомогательный метод для обслуживания физических файлов
    private static Task<IResult> ServePhysicalFile(string filePath, string filename)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".npk" => "application/octet-stream",
            ".zip" => "application/zip",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            _ => "application/octet-stream"
        };

        // Для CHANGELOG не добавляем Content-Disposition
        var downloadName = filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetFileName(filePath);

        Console.WriteLine($"[DEBUG] Serving file: {filePath} with contentType: {contentType}");
        var stream = File.OpenRead(filePath);
        var result = Results.File(stream, contentType, downloadName);
        return Task.FromResult(result);
    }

    // ===== Handlers =====

    private static async Task<IResult> GetVersions(MikroTikUpdateService service)
    {
        var data = await service.GetVersionsInfoAsync();
        return Results.Ok(data);
    }

    private static async Task<IResult> GetStatus(MikroTikUpdateService service)
    {
        var data = await service.GetStatusAsync();
        return Results.Ok(data);
    }

    private static async Task<IResult> TriggerUpdateCheck(MikroTikUpdateService service)
    {
        var (downloaded, versions, status) = await service.CheckAndDownloadUpdatesAsync();

        return status switch
        {
            "already_in_progress" => Results.Json(
                new
                {
                    code = "update_in_progress",
                    message = "Update check is already in progress"
                },
                statusCode: 409),

            "network_unavailable" => Results.Json(
                new
                {
                    code = "network_unavailable",
                    message = "Cannot reach MikroTik servers. Check internet connection or firewall settings.",
                    details = "Server cannot connect to upgrade.mikrotik.com"
                },
                statusCode: 503),

            "network_error" => Results.Json(
                new
                {
                    code = "network_error",
                    message = "Network error occurred during update check",
                    details = "Please check your internet connection"
                },
                statusCode: 503),

            "timeout" => Results.Json(
                new
                {
                    code = "timeout",
                    message = "Update check timed out",
                    details = "MikroTik servers took too long to respond"
                },
                statusCode: 504),

            "fetch_failed" => Results.Json(
                new
                {
                    code = "fetch_failed",
                    message = "Failed to fetch latest version information from MikroTik servers",
                    details = "Ensure upgrade.mikrotik.com is accessible"
                },
                statusCode: 503),

            "error" => Results.Json(
                new
                {
                    code = "internal_error",
                    message = "Unexpected error during update check"
                },
                statusCode: 500),

            "success" => Results.Ok(new
            {
                message = "Update check completed",
                downloaded,
                checkedVersions = versions,
                timestamp = DateTime.UtcNow
            }),

            _ => Results.Json(
                new
                {
                    code = "unknown_error",
                    message = $"Unknown status: {status}"
                },
                statusCode: 500)
        };
    }

    private static async Task<IResult> SetActiveVersion(
        string version,
        MikroTikUpdateService service)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Results.Json(
                new {code = "bad_request", message = "Version parameter is required"},
                statusCode: 400);

        try
        {
            var result = await service.SetActiveVersionAsync(version);
            if (!result)
                return Results.Json(
                    new {code = "version_not_found", message = $"Version {version} not found"},
                    statusCode: 404);

            return Results.Ok(new {message = "Active version updated", version});
        }
        catch
        {
            return Results.Json(
                new {code = "internal_error", message = "Failed to set active version"},
                statusCode: 500);
        }
    }

    private static async Task<IResult> RemoveVersion(
        string version,
        MikroTikUpdateService service)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Results.Json(
                new {code = "bad_request", message = "Version parameter is required"},
                statusCode: 400);

        try
        {
            var result = await service.RemoveVersionAsync(version);
            if (!result)
                return Results.Json(
                    new {code = "version_protected", message = $"Version {version} is active or protected"},
                    statusCode: 409);

            return Results.Ok(new {message = "Version removed", version});
        }
        catch
        {
            return Results.Json(
                new {code = "internal_error", message = "Failed to remove version"},
                statusCode: 500);
        }
    }

    private static async Task<IResult> DownloadFile(
        string version,
        string filename,
        MikroTikUpdateService service,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(filename))
            return Results.Json(
                new {code = "bad_request", message = "Version and filename are required"},
                statusCode: 400);

        var filePath = await service.GetFilePathAsync(version, filename);
        if (filePath == null || !File.Exists(filePath))
            return Results.Json(
                new {code = "file_not_found", message = $"File not found: {version}/{filename}"},
                statusCode: 404);

        try
        {
            var fileInfo = new FileInfo(filePath);
            var etag = $"\"{fileInfo.LastWriteTimeUtc.Ticks}\"";
            context.Response.Headers["ETag"] = etag;

            if (context.Request.Headers.TryGetValue("If-None-Match", out var clientEtag) &&
                clientEtag == etag)
                return Results.StatusCode(304);

            var stream = File.OpenRead(filePath);
            return Results.File(stream, "application/octet-stream", filename);
        }
        catch
        {
            return Results.Json(
                new {code = "internal_error", message = "Failed to download file"},
                statusCode: 500);
        }
    }

    private static IResult HandleError(HttpContext context)
    {
        return Results.Json(
            new
            {
                error = "Internal Server Error",
                timestamp = DateTime.UtcNow,
                traceId = context.TraceIdentifier
            },
            statusCode: 500);
    }

    private static async Task<IResult> GetVersionHistory(
        MikroTikUpdateService service,
        [FromQuery] int take = 50)
    {
        var history = await service.GetVersionHistoryAsync(take);
        return Results.Ok(new
        {
            count = history.Count,
            data = history
        });
    }

    private static async Task<IResult> GetGlobalChangelog(MikroTikUpdateService service)
    {
        var content = await service.GetGlobalChangelogContentAsync();
        if (content is null)
            return Results.Json(
                new {code = "not_found", message = "Global CHANGELOG not available"},
                statusCode: 404);

        return Results.Text(content, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> GetVersionChangelog(
        string version,
        MikroTikUpdateService service)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Results.Json(
                new {code = "bad_request", message = "Version parameter is required"},
                statusCode: 400);

        var content = await service.GetChangelogContentAsync(version);
        if (content is null)
            return Results.Json(
                new {code = "not_found", message = $"CHANGELOG for version {version} not found"},
                statusCode: 404);

        return Results.Text(content, "text/plain; charset=utf-8");
    }

    private static IResult GetTimeZone(TimeZoneService tz)
    {
        var current = tz.Current;
        return Results.Ok(new
        {
            id = current.Id,
            displayName = current.DisplayName,
            baseUtcOffsetMinutes = (int) current.BaseUtcOffset.TotalMinutes
        });
    }

    private static IResult GetTimeZoneList(TimeZoneService tz)
    {
        var zones = TimeZoneService.GetAllTimeZones();
        return Results.Ok(zones);
    }

    private static IResult UpdateTimeZone(TimeZoneService tz, [FromBody] TimeZoneUpdateDto? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.TimeZoneId))
            return Results.Json(
                new {code = "bad_request", message = "TimeZoneId is required"},
                statusCode: 400);

        try
        {
            tz.SetTimeZone(dto.TimeZoneId);
            var current = tz.Current;
            return Results.Ok(new
            {
                message = "Time zone updated",
                id = current.Id,
                displayName = current.DisplayName,
                baseUtcOffsetMinutes = (int) current.BaseUtcOffset.TotalMinutes
            });
        }
        catch (TimeZoneNotFoundException)
        {
            return Results.Json(
                new {code = "timezone_not_found", message = $"Time zone '{dto.TimeZoneId}' not found"},
                statusCode: 404);
        }
    }

    // DTO для смены часового пояса
    private sealed record TimeZoneUpdateDto(string TimeZoneId);
}