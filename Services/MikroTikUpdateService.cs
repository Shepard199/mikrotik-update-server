using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace MikroTik.UpdateServer.Services;

public class MikroTikUpdateService
{
    private const int _diskUsageCacheSeconds = 30; // кэш на 30 секунд

    private static readonly string[] DefaultAllowedArches =
    [
        "arm",
        "arm64",
        "mipsbe",
        "mmips",
        "smips",
        "tile",
        "ppc"
    ];

    private readonly string _allowedArchesFile;

    private readonly string _baseFolder;
    private readonly string _deleteJsonFile;
    private readonly HttpClient _httpClient;

    private readonly string _lastCheckFile;
    private readonly ILogger<MikroTikUpdateService> _logger;
    private readonly string _versionsFile;

    private string _activeV6Version = "";
    private string _activeV7Fixed = "";
    private string _activeV7Latest = "";
    private string[] _allowedArches;

    private int _isChecking = 0;

    private DateTime _lastCheck = DateTime.MinValue;

    private DateTime _lastCpuCheck = DateTime.MinValue;
    private double _lastCpuValue = 0;

    private long _lastDiskUsageBytes = 0;
    private DateTime _lastDiskUsageTime = DateTime.MinValue;
    private long _totalDownloaded;
    private int _totalFiles;

    public MikroTikUpdateService(ILogger<MikroTikUpdateService> logger)
    {
        _logger = logger;

        var baseDir = AppContext.BaseDirectory;

        _baseFolder = Path.GetFullPath(Path.Combine(baseDir, "routeros"));

        _versionsFile = Path.Combine(_baseFolder, "versions.json");
        _deleteJsonFile = Path.Combine(baseDir, "delete_prefixes.json");
        _lastCheckFile = Path.Combine(baseDir, "last_check.json");

        _httpClient = new HttpClient {Timeout = TimeSpan.FromMinutes(10)};
        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "MikroTik-ROS-UpdateServer/1.0 (+https://github.com)");

        Directory.CreateDirectory(_baseFolder);
        LoadActiveVersions();
        LoadLastCheck();

        _allowedArchesFile = Path.Combine(baseDir, "allowed_arches.json");
        _allowedArches = LoadAllowedArches();

        _logger.LogInformation("Service initialized. Base folder: {BaseFolder}", _baseFolder);
        _logger.LogInformation("Updates folder: routeros");
    }

    private string[] LoadAllowedArches()
    {
        try
        {
            if (File.Exists(_allowedArchesFile))
            {
                var json = File.ReadAllText(_allowedArchesFile);
                var arches = JsonSerializer.Deserialize<string[]>(json);

                if (arches is {Length: > 0})
                {
                    var normalized = arches
                        .Select(a => a?.Trim().ToLowerInvariant())
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Distinct()
                        .ToArray();

                    if (normalized.Length > 0)
                    {
                        _logger.LogInformation(
                            "Loaded {Count} allowed architectures from {File}",
                            normalized.Length, _allowedArchesFile);
                        return normalized;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load allowed architectures, using defaults");
        }

        _logger.LogInformation("Using default allowed architectures: {Arches}",
            string.Join(", ", DefaultAllowedArches));

        return DefaultAllowedArches;
    }

    public string[] GetAllowedArches()
    {
        return _allowedArches.ToArray();
    }

    public async Task UpdateAllowedArchesAsync(IEnumerable<string> arches)
    {
        if (arches == null) throw new ArgumentNullException(nameof(arches));

        var normalized = arches
            .Select(a => a?.Trim().ToLowerInvariant())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToArray();

        // Если пользователь снял все галки – откат к дефолту, чтобы сервис не сломался
        if (normalized.Length == 0)
            normalized = DefaultAllowedArches;

        _allowedArches = normalized;

        try
        {
            var json = JsonSerializer.Serialize(_allowedArches,
                new JsonSerializerOptions {WriteIndented = true});
            await File.WriteAllTextAsync(_allowedArchesFile, json);
            _logger.LogInformation("Allowed architectures updated: {Arches}",
                string.Join(", ", _allowedArches));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save allowed architectures to {File}", _allowedArchesFile);
            throw;
        }
    }


    /// <summary>
    ///     Проверяет доступность MikroTik серверов
    /// </summary>
    private async Task<bool> CheckMikroTikConnectivityAsync()
    {
        try
        {
            _logger.LogInformation("Checking connectivity to upgrade.mikrotik.com...");
            using var request =
                new HttpRequestMessage(HttpMethod.Head, "https://upgrade.mikrotik.com/routeros/LATEST.6");
            var response = await _httpClient.SendAsync(request);
            var isConnected = response.IsSuccessStatusCode;
            _logger.LogInformation("MikroTik server connectivity: {Status}", isConnected ? "OK" : "FAILED");
            return isConnected;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "MikroTik server unreachable (network error)");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "MikroTik server timeout");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking MikroTik connectivity");
            return false;
        }
    }

    private void LoadLastCheck()
    {
        try
        {
            if (File.Exists(_lastCheckFile))
            {
                var content = File.ReadAllText(_lastCheckFile);
                if (DateTime.TryParse(content, out var lastCheck))
                {
                    _lastCheck = lastCheck;
                    _logger.LogInformation("Loaded last check time: {LastCheck}", _lastCheck);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading last check time");
        }
    }

    private void SaveLastCheck()
    {
        try
        {
            File.WriteAllText(_lastCheckFile, _lastCheck.ToString("O")); // ISO 8601 format
            _logger.LogDebug("Saved last check time: {LastCheck}", _lastCheck);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving last check time");
        }
    }

    private async Task<object?> GetDiskUsageAsync()
    {
        // Если кеш ещё актуален (менее 30 сек прошло) — возвращаем кеш
        if ((DateTime.Now - _lastDiskUsageTime).TotalSeconds < _diskUsageCacheSeconds && _lastDiskUsageBytes > 0)
        {
            _logger.LogDebug("Returning cached disk usage");
            return new
            {
                totalMB = (_lastDiskUsageBytes / 1024.0 / 1024.0).ToString("F2"),
                totalGB = (_lastDiskUsageBytes / 1024.0 / 1024.0 / 1024.0).ToString("F2")
            };
        }

        // Иначе пересчитываем в фоне
        return await Task.Run(GetDiskUsageSync);
    }

    private object? GetDiskUsageSync()
    {
        if (!Directory.Exists(_baseFolder))
            return null;

        try
        {
            var totalSize = 0L;
            var dir = new DirectoryInfo(_baseFolder);

            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                try
                {
                    totalSize += file.Length;
                }
                catch
                {
                    // Пропускаем недоступные файлы
                }

            _lastDiskUsageBytes = totalSize;
            _lastDiskUsageTime = DateTime.Now;

            return new
            {
                totalMB = (totalSize / 1024.0 / 1024.0).ToString("F2"),
                totalGB = (totalSize / 1024.0 / 1024.0 / 1024.0).ToString("F2")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating disk usage");
            return new {error = "Could not calculate disk usage"};
        }
    }

    public Task<string?> GetPackagesCsvPathAsync(string branchVersion)
    {
        if (string.IsNullOrWhiteSpace(branchVersion))
            return Task.FromResult<string?>(null);

        var packagesDir = Path.Combine(_baseFolder, "packages");
        var localPath = Path.Combine(packagesDir, $"{branchVersion}.csv");

        return Task.FromResult(File.Exists(localPath) ? localPath : null);
    }

    private async Task DownloadPackagesCsvForBranchAsync(string branchVersion)
    {
        if (string.IsNullOrWhiteSpace(branchVersion))
            return;

        var packagesDir = Path.Combine(_baseFolder, "packages");
        Directory.CreateDirectory(packagesDir);

        var localPath = Path.Combine(packagesDir, $"{branchVersion}.csv");

        // Уже есть и не пустой — не трогаем
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
        {
            _logger.LogDebug("packages.csv already exists for branch {Branch}", branchVersion);
            return;
        }

        var url = $"https://upgrade.mikrotik.com/routeros/{branchVersion}/packages.csv";

        try
        {
            _logger.LogInformation("Downloading packages.csv for branch {Branch} from {Url}", branchVersion, url);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var headResponse = await _httpClient.SendAsync(request);

            // Проверяем наличие файла перед попыткой скачивания
            if (!headResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "packages.csv not available for branch {Branch} (HTTP {StatusCode}). This is normal for fixed or old versions.",
                    branchVersion,
                    (int) headResponse.StatusCode);
                return;
            }

            var csv = await _httpClient.GetStringAsync(url);
            await File.WriteAllTextAsync(localPath, csv);
            _logger.LogInformation("Saved packages.csv for branch {Branch} to {Path}", branchVersion, localPath);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "packages.csv not found for branch {Branch}. This is normal for fixed or old versions. Error: {Message}",
                branchVersion,
                ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "HTTP error downloading packages.csv for branch {Branch}",
                branchVersion);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Timeout downloading packages.csv for branch {Branch}",
                branchVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to download packages.csv for branch {Branch}",
                branchVersion);
        }
    }

    public Task<string?> GetChangelogPathAsync(string version)
    {
        // Проверяем в v6 и v7 папках
        var v6Path = Path.Combine(_baseFolder, "v6", version, "CHANGELOG");
        var v7Path = Path.Combine(_baseFolder, "v7", version, "CHANGELOG");

        if (File.Exists(v6Path))
            return Task.FromResult<string?>(v6Path);

        return File.Exists(v7Path)
            ? Task.FromResult<string?>(v7Path)
            : Task.FromResult<string?>(null);
    }

    public Task<string?> GetGlobalChangelogPathAsync()
    {
        var globalChangelogPath = Path.Combine(_baseFolder, "CHANGELOG");
        return File.Exists(globalChangelogPath)
            ? Task.FromResult<string?>(globalChangelogPath)
            : Task.FromResult<string?>(null);
    }

    public async Task<(int downloaded, string[] versions, string status)> CheckAndDownloadUpdatesAsync()
    {
        // Проверяем, не запущена ли уже проверка
        if (Interlocked.Exchange(ref _isChecking, 1) != 0)
        {
            _logger.LogWarning("Update check already in progress, skipping");
            return (0, [], "already_in_progress");
        }

        var downloadedCount = 0;
        var processedVersions = new List<string>();

        try
        {
            _logger.LogInformation("=== Starting update check at {Time} ===", DateTime.Now);

            // Проверяем доступность сервера
            var isConnected = await CheckMikroTikConnectivityAsync();
            if (!isConnected)
            {
                _logger.LogWarning("Cannot reach MikroTik servers. Using cached versions if available.");
                return (0, [], "network_unavailable");
            }

            var (v6Version, v6Build) =
                await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/LATEST.6");
            var (v7Latest, v7LatestBuild) =
                await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/NEWESTa7.stable");
            const string v7Fixed = "7.12.1";
            const long v7FixedBuild = 0L;

            if (v6Version == null || v7Latest == null)
            {
                _logger.LogWarning("Could not fetch version information from MikroTik");
                return (0, [], "fetch_failed");
            }

            _logger.LogInformation(
                "Latest versions - v6: {V6}, v7Fixed: {V7Fixed}, v7Latest: {V7Latest}",
                v6Version, v7Fixed, v7Latest);

            // Проверяем и скачиваем v6 версию
            if (!_activeV6Version.Equals(v6Version, StringComparison.OrdinalIgnoreCase) ||
                !await IsVersionCompleteAsync(v6Version, true))
            {
                var v6Dir = Path.Combine(_baseFolder, "v6", v6Version);
                downloadedCount += await ProcessVersionAsync(v6Version, v6Dir, true, false);
                _activeV6Version = v6Version;
                CleanupOldVersions(Path.Combine(_baseFolder, "v6"), 3, v6Version);
                processedVersions.Add($"v6:{v6Version}");
            }
            else
            {
                _logger.LogInformation("v6 version {V6} already exists and complete", v6Version);
                processedVersions.Add($"v6:{v6Version}(existing)");
            }

            // Проверяем и скачиваем v7 fixed версию
            if (!_activeV7Fixed.Equals(v7Fixed, StringComparison.OrdinalIgnoreCase) ||
                !await IsVersionCompleteAsync(v7Fixed, false))
            {
                var v7FixedDir = Path.Combine(_baseFolder, "v7", v7Fixed);
                downloadedCount += await ProcessVersionAsync(v7Fixed, v7FixedDir, false, false);
                _activeV7Fixed = v7Fixed;
                processedVersions.Add($"v7-fixed:{v7Fixed}");
            }
            else
            {
                _logger.LogInformation("v7 fixed version {V7} already exists and complete", v7Fixed);
                processedVersions.Add($"v7-fixed:{v7Fixed}(existing)");
            }

            // Проверяем и скачиваем v7 latest версию
            if (!_activeV7Latest.Equals(v7Latest, StringComparison.OrdinalIgnoreCase) ||
                !await IsVersionCompleteAsync(v7Latest, false))
            {
                var v7LatestDir = Path.Combine(_baseFolder, "v7", v7Latest);
                downloadedCount += await ProcessVersionAsync(v7Latest, v7LatestDir, false, false);
                _activeV7Latest = v7Latest;
                CleanupOldVersions(Path.Combine(_baseFolder, "v7"), 3, v7Latest, v7Fixed);
                processedVersions.Add($"v7-latest:{v7Latest}");
            }
            else
            {
                _logger.LogInformation("v7 latest version {V7} already exists and complete", v7Latest);
                processedVersions.Add($"v7-latest:{v7Latest}(existing)");
            }

            await UpdatePointerFilesAsync(v6Version, v7Fixed, v7Latest, v6Build, v7FixedBuild, v7LatestBuild);
            await LogVersionsAsync(v6Version, v7Fixed, v7Latest);

            _lastCheck = DateTime.Now;
            SaveLastCheck();

            _logger.LogInformation("=== Update check completed. Downloaded {Count} files ===", downloadedCount);
            return (downloadedCount, processedVersions.ToArray(), "success");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error during update check");
            return (0, [], "network_error");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout during update check");
            return (0, [], "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "=== Error during update check ===");
            return (0, [], "error");
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    // Проверяет, все ли файлы для версии существуют
    private Task<bool> IsVersionCompleteAsync(string version, bool isV6Extra)
    {
        var archs = _allowedArches;
        var versionDir = Path.Combine(_baseFolder, isV6Extra ? "v6" : "v7", version);

        if (!Directory.Exists(versionDir))
            return Task.FromResult(false);

        foreach (var arch in archs)
        {
            var fileName = isV6Extra
                ? $"all_packages-{arch}-{version}.zip"
                : $"routeros-{arch}-{version}.npk";

            var filePath = Path.Combine(versionDir, fileName);

            if (!File.Exists(filePath))
                return Task.FromResult(false);

            // Дополнительная проверка, что файл не пустой
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
                return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private async Task<int> ProcessVersionAsync(string version, string downloadDir, bool isV6Extra, bool skipIfExists)
    {
        if (skipIfExists && await IsVersionCompleteAsync(version, isV6Extra))
        {
            _logger.LogInformation("Version {Version} already exists and complete, skipping", version);
            return 0;
        }

        Directory.CreateDirectory(downloadDir);

        var archs = _allowedArches;

        var fileUrls = archs.Select(arch => isV6Extra
                ? $"all_packages-{arch}-{version}.zip"
                : $"routeros-{version}-{arch}.npk")
            .Select(fileName => $"https://download.mikrotik.com/routeros/{version}/{fileName}")
            .ToList();

        _logger.LogInformation(
            "Processing {Type} version {Version}, {Count} files to check",
            isV6Extra ? "v6" : "v7",
            version,
            fileUrls.Count);

        List<string>? deletePrefixes = null;
        if (isV6Extra)
            deletePrefixes = LoadDeletePrefixes();

        var tasks = fileUrls
            .Select(url => DownloadFileAsync(url, downloadDir, isV6Extra, deletePrefixes))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        // Скачиваем CHANGELOG для этой версии
        await DownloadChangelogAsync(version, downloadDir);

        _logger.LogInformation(
            "Version {Version} processing completed. Downloaded: {Success}/{Total}",
            version,
            successCount,
            fileUrls.Count);

        return successCount;
    }

    private async Task DownloadChangelogAsync(string version, string downloadDir)
    {
        try
        {
            var changelogUrl = $"https://upgrade.mikrotik.com/routeros/{version}/CHANGELOG";
            var changelogPath = Path.Combine(downloadDir, "CHANGELOG");

            // Если файл уже существует, пропускаем загрузку
            if (File.Exists(changelogPath))
            {
                _logger.LogDebug("CHANGELOG already exists for version {Version}", version);
                return;
            }

            _logger.LogInformation("Downloading CHANGELOG for version {Version}", version);
            var changelogContent = await _httpClient.GetStringAsync(changelogUrl);

            await File.WriteAllTextAsync(changelogPath, changelogContent);
            _logger.LogInformation("Downloaded CHANGELOG for version {Version}", version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download CHANGELOG for version {Version}", version);
        }
    }

    private static string GetBranchFromVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;

        var parts = version.Split('.');
        // 7.20.4 -> 7.20
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }


    public string? GetPointerFileContent(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        // 1. Пытаемся найти реальный физический файл (case-insensitive)
        if (Directory.Exists(_baseFolder))
        {
            var normalizedFilename = filename.ToLowerInvariant();
            var existingFile = Directory.GetFiles(_baseFolder)
                .FirstOrDefault(f => Path.GetFileName(f).ToLowerInvariant() == normalizedFilename);

            if (existingFile != null)
                try
                {
                    var content = File.ReadAllText(existingFile);
                    _logger.LogDebug("Served pointer file from disk: {File}", filename);
                    return content;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading pointer file: {File}", filename);
                }
        }

        // 2. Используем карту pointer-файлов
        var pointerData = GetPointerVersionFromMap(filename);
        if (pointerData is null)
        {
            _logger.LogDebug("Pointer file not found in map: {File}", filename);
            return null;
        }

        var (version, _) = pointerData.Value;
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = $"{version} {epoch}\n";

        _logger.LogDebug("Generated pointer file on-the-fly: {File} -> {Version}", filename, version);
        return result;
    }

    private async Task<bool> DownloadFileAsync(
        string fileUrl,
        string downloadDir,
        bool isV6Extra,
        List<string>? deletePrefixes)
    {
        var fileName = Path.GetFileName(fileUrl);
        var filePath = Path.Combine(downloadDir, fileName);

        // Файл уже скачан
        if (File.Exists(filePath))
        {
            _logger.LogDebug("File already exists: {File}", fileName);

            // Для v6 extra гарантируем, что .npk распакованы из уже имеющегося архива
            if (isV6Extra &&
                fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ExtractZipToVersionDir(filePath, downloadDir);

            return false;
        }

        if (!await FileExistsAsync(fileUrl))
        {
            _logger.LogWarning("File not found on server: {Url}", fileUrl);
            return false;
        }

        try
        {
            _logger.LogInformation("Downloading: {File}", fileName);
            var bytes = await _httpClient.GetByteArrayAsync(fileUrl);

            await File.WriteAllBytesAsync(filePath, bytes);
            _totalDownloaded += bytes.Length;
            _totalFiles++;

            _logger.LogInformation(
                "Downloaded: {File} ({Size} MB)",
                fileName,
                (bytes.Length / 1024.0 / 1024.0).ToString("F2"));

            if (isV6Extra &&
                fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // сначала чистим архив от мусора, как и раньше
                if (deletePrefixes is not null)
                    CleanupZipFile(filePath, deletePrefixes);

                // затем распаковываем .npk в папку версии
                ExtractZipToVersionDir(filePath, downloadDir);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading {File}", fileName);
            if (File.Exists(filePath))
                File.Delete(filePath);
            return false;
        }
    }

    private void ExtractZipToVersionDir(string zipPath, string destinationDir)
    {
        try
        {
            if (!File.Exists(zipPath))
                return;

            Directory.CreateDirectory(destinationDir);

            using var archive = ZipFile.OpenRead(zipPath);
            var extractedCount = 0;

            foreach (var entry in archive.Entries)
            {
                // Нас интересуют только пакеты .npk
                if (!entry.FullName.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetPath = Path.Combine(destinationDir, entry.Name);

                using var entryStream = entry.Open();
                using var outStream = File.Create(targetPath);
                entryStream.CopyTo(outStream);

                extractedCount++;
            }

            _logger.LogInformation(
                "Extracted {Count} .npk files from {Zip} to {Dir}",
                extractedCount,
                Path.GetFileName(zipPath),
                destinationDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting .npk files from zip {Path}", zipPath);
        }
    }

    private async Task<(string? version, long build)> GetVersionFromUrlAsync(string url)
    {
        try
        {
            _logger.LogDebug("Fetching version from {Url}", url);
            var response = await _httpClient.GetStringAsync(url);
            var parts = response.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                _logger.LogWarning("Empty response from {Url}", url);
                return (null, 0L);
            }

            var v = parts[0];
            long build = 0;
            if (parts.Length > 1)
                long.TryParse(parts[1], out build);

            if (string.IsNullOrWhiteSpace(v))
            {
                _logger.LogWarning("Invalid version format from {Url}: {Response}", url, response);
                return (null, 0L);
            }

            _logger.LogInformation("Successfully fetched version {Version} (build {Build}) from {Url}", v, build, url);
            return (v, build);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching version from {Url}", url);
            return (null, 0L);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout fetching version from {Url}", url);
            return (null, 0L);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting version from {Url}", url);
            return (null, 0L);
        }
    }

    private async Task<bool> FileExistsAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private List<string> LoadDeletePrefixes()
    {
        if (!File.Exists(_deleteJsonFile))
            return [];

        try
        {
            var jsonContent = File.ReadAllText(_deleteJsonFile);
            var jsonData = JsonSerializer.Deserialize<Dictionary<string, string[]>>(jsonContent);

            var prefixes = jsonData?.TryGetValue("deletePrefixes", out var value) == true
                ? value.ToList()
                : [];

            _logger.LogInformation("Loaded delete prefixes: {Prefixes}", string.Join(", ", prefixes));
            return prefixes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading delete prefixes");
            return [];
        }
    }

    private void CleanupZipFile(string zipPath, List<string> deletePrefixes)
    {
        if (deletePrefixes.Count == 0)
            return;

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            ZipFile.ExtractToDirectory(zipPath, tempDir);

            var files = Directory.GetFiles(tempDir);
            var removedCount = 0;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (deletePrefixes.Any(p =>
                        fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Removing file from archive: {File}", fileName);
                    File.Delete(file);
                    removedCount++;
                }
            }

            File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, false);
            Directory.Delete(tempDir, true);

            _logger.LogInformation("Archive cleanup completed. Removed {Count} files", removedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up zip file {Path}", zipPath);
        }
    }

    private void CleanupOldVersions(string folder, int keepCount, params string[] protectVersions)
    {
        if (!Directory.Exists(folder))
            return;

        try
        {
            var dirs = Directory.GetDirectories(folder)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(d => !protectVersions.Contains(d))
                .OrderByDescending(d => Version.TryParse(d, out var v) ? v : new Version())
                .Skip(keepCount)
                .ToList();

            foreach (var dir in dirs)
            {
                var fullPath = Path.Combine(folder, dir);
                var size = Directory
                    .GetFiles(fullPath, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);

                Directory.Delete(fullPath, true);

                _logger.LogInformation(
                    "Removed old version: {Version} (freed {Size} MB)",
                    dir,
                    (size / 1024.0 / 1024.0).ToString("F2"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old versions");
        }
    }

    // Обновляем глобальный CHANGELOG (суммарный)
    private async Task UpdateGlobalChangelogAsync(string v6Version, string v7Fixed, string v7Latest)
    {
        try
        {
            var changelogPath = Path.Combine(_baseFolder, "CHANGELOG");
            var entries = new List<string>
            {
                // Добавляем информацию о текущих версиях
                $"Current versions at {DateTime.Now:yyyy-MM-dd HH:mm:ss}:",
                $"  RouterOS v6: {v6Version}",
                $"  RouterOS v7 (fixed): {v7Fixed}",
                $"  RouterOS v7 (latest): {v7Latest}",
                ""
            };

            // Собираем CHANGELOG из всех активных версий
            var activeVersions = new List<(string version, bool isV6)>
            {
                (v6Version, true),
                (v7Fixed, false),
                (v7Latest, false)
            };

            foreach (var (version, isV6) in activeVersions)
            {
                if (string.IsNullOrEmpty(version)) continue;

                var versionDir = Path.Combine(_baseFolder, isV6 ? "v6" : "v7", version);
                var versionChangelogPath = Path.Combine(versionDir, "CHANGELOG");

                if (File.Exists(versionChangelogPath))
                    try
                    {
                        var versionChangelog = await File.ReadAllTextAsync(versionChangelogPath);
                        entries.Add($"=== RouterOS {version} CHANGELOG ===");
                        entries.Add(versionChangelog);
                        entries.Add("");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading CHANGELOG for version {Version}", version);
                    }
            }

            await File.WriteAllLinesAsync(changelogPath, entries);
            _logger.LogDebug("Updated global CHANGELOG");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating global CHANGELOG");
        }
    }

    // Удалить старый CreatePointerFilesAsync, оставить только это:

    private async Task UpdatePointerFilesAsync(
        string v6Version,
        string v7Fixed,
        string v7Latest,
        long v6Build,
        long v7FixedBuild,
        long v7LatestBuild)
    {
        try
        {
            _logger.LogInformation(
                "UpdatePointerFilesAsync started - V6:{V6}, V7Fixed:{V7F}, V7Latest:{V7L}",
                v6Version, v7Fixed, v7Latest);

            Directory.CreateDirectory(Path.Combine(_baseFolder, "v6"));
            Directory.CreateDirectory(Path.Combine(_baseFolder, "v7"));

            // Используем карту pointer-файлов
            var pointerMap = BuildPointerMap(v6Version, v6Build, v7Fixed, v7FixedBuild, v7Latest, v7LatestBuild);

            foreach (var (fileName, (version, build)) in pointerMap)
            {
                var filePath = Path.Combine(_baseFolder, fileName);
                var content = $"{version} {build}\n";

                try
                {
                    await File.WriteAllTextAsync(filePath, content);
                    _logger.LogInformation("Created pointer file: {File} -> {Version}", fileName, version);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error writing pointer file: {File}", fileName);
                }
            }

            // Скачиваем packages.csv для веток RouterOS 7
            var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(v7Fixed))
                branches.Add(GetBranchFromVersion(v7Fixed));
            if (!string.IsNullOrEmpty(v7Latest))
                branches.Add(GetBranchFromVersion(v7Latest));

            foreach (var branch in branches)
                await DownloadPackagesCsvForBranchAsync(branch);

            // Обновляем глобальный CHANGELOG
            await UpdateGlobalChangelogAsync(v6Version, v7Fixed, v7Latest);

            _logger.LogInformation("Pointer files update completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating pointer files");
        }
    }

    // Обновляет CHANGELOG
    private async Task LogVersionsAsync(string v6, string v7Fixed, string v7Latest)
    {
        try
        {
            var logs = new List<VersionLog>();
            if (File.Exists(_versionsFile))
            {
                var content = await File.ReadAllTextAsync(_versionsFile);
                logs = JsonSerializer.Deserialize<List<VersionLog>>(content) ?? [];
            }

            logs.Add(new VersionLog
            {
                Timestamp = DateTime.Now,
                V6Stable = v6,
                V7Fixed = v7Fixed,
                V7Stable = v7Latest
            });

            if (logs.Count > 100)
                logs = logs.TakeLast(100).ToList();

            var json = JsonSerializer.Serialize(
                logs,
                new JsonSerializerOptions {WriteIndented = true});

            await File.WriteAllTextAsync(_versionsFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging versions");
        }
    }

    public Task<object> GetVersionsInfoAsync()
    {
        var v6Dir = Path.Combine(_baseFolder, "v6");
        var v7Dir = Path.Combine(_baseFolder, "v7");

        var v6Versions = Directory.Exists(v6Dir)
            ? Directory.GetDirectories(v6Dir)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(d => d != "LATEST.6")
                .OrderByDescending(Version.Parse)
                .ToList()
            : [];

        var v7Versions = Directory.Exists(v7Dir)
            ? Directory.GetDirectories(v7Dir)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(d => !d.StartsWith("NEWEST", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Version.Parse)
                .ToList()
            : [];

        object payload = new
        {
            v6 = new {active = _activeV6Version, versions = v6Versions},
            v7 = new {activeFixed = _activeV7Fixed, activeLatest = _activeV7Latest, versions = v7Versions},
            lastCheck = _lastCheck
        };

        return Task.FromResult(payload);
    }

    public async Task<object> GetStatusAsync()
    {
        var process = Process.GetCurrentProcess();
        var uptime = DateTime.Now - process.StartTime;

        var diskUsage = await GetDiskUsageAsync();
        var cpuUsage = GetCpuUsage();

        // Вместо Process.Threads.Count используем ThreadPool.ThreadCount
        var threadCount = ThreadPool.ThreadCount;

        // Если нужна информация об активных vs. максимальных threads:
        ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

        object payload = new
        {
            status = "online",
            timestamp = DateTime.UtcNow,
            uptime = new {days = uptime.Days, hours = uptime.Hours, minutes = uptime.Minutes},
            process = new
            {
                memory = (process.WorkingSet64 / 1024.0 / 1024.0).ToString("F2") + " MB",
                threads = new
                {
                    threadPoolActive = threadCount,
                    workerThreadsAvailable = workerThreads,
                    maxWorkerThreads,
                    completionPortThreadsAvailable = completionPortThreads,
                    maxCompletionPortThreads
                },
                cpuUsage
            },
            activeVersions = new {v6 = _activeV6Version, v7Fixed = _activeV7Fixed, v7Latest = _activeV7Latest},
            diskUsage,
            downloads = new {total = _totalDownloaded / 1024 / 1024 / 1024, files = _totalFiles},
            lastCheck = _lastCheck,
            settings = new
            {
                updatesFolder = _baseFolder
            }
        };

        return payload;
    }

    private string GetCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();

            // Проверяем не чаще, чем раз в секунду
            if ((DateTime.Now - _lastCpuCheck).TotalMilliseconds < 1000)
                return _lastCpuValue.ToString("F2") + "%";

            // Кроссплатформенный способ: используем CPU time
            // Formula: (CPUTime / TotalRunTime) * 100 / ProcessorCount
            var totalRunTime = (DateTime.Now - process.StartTime).TotalMilliseconds;
            var cpuTime = process.TotalProcessorTime.TotalMilliseconds;

            if (totalRunTime > 0) _lastCpuValue = cpuTime / totalRunTime / Environment.ProcessorCount * 100;

            _lastCpuCheck = DateTime.Now;

            return _lastCpuValue.ToString("F2") + "%";
        }
        catch
        {
            return "N/A";
        }
    }

    public Task<bool> SetActiveVersionAsync(string version)
    {
        var v6Dir = Path.Combine(_baseFolder, "v6", version);
        var v7Dir = Path.Combine(_baseFolder, "v7", version);

        if (Directory.Exists(v6Dir))
        {
            _activeV6Version = version;
            _logger.LogInformation("Active v6 version set to: {Version}", version);
            return Task.FromResult(true);
        }

        if (Directory.Exists(v7Dir))
        {
            if (version == "7.12.1")
                _activeV7Fixed = version;
            else
                _activeV7Latest = version;

            _logger.LogInformation("Active v7 version set to: {Version}", version);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> RemoveVersionAsync(string version)
    {
        if (_activeV6Version == version ||
            _activeV7Fixed == version ||
            _activeV7Latest == version)
        {
            _logger.LogWarning("Attempted to remove active version: {Version}", version);
            return Task.FromResult(false);
        }

        var v6Dir = Path.Combine(_baseFolder, "v6", version);
        var v7Dir = Path.Combine(_baseFolder, "v7", version);

        try
        {
            if (Directory.Exists(v6Dir))
            {
                var size = Directory
                    .GetFiles(v6Dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);

                Directory.Delete(v6Dir, true);

                _logger.LogInformation(
                    "Removed v6 version: {Version} (freed {Size} MB)",
                    version,
                    (size / 1024.0 / 1024.0).ToString("F2"));
                return Task.FromResult(true);
            }

            if (Directory.Exists(v7Dir))
            {
                var size = Directory
                    .GetFiles(v7Dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);

                Directory.Delete(v7Dir, true);

                _logger.LogInformation(
                    "Removed v7 version: {Version} (freed {Size} MB)",
                    version,
                    (size / 1024.0 / 1024.0).ToString("F2"));
                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing version {Version}", version);
        }

        return Task.FromResult(false);
    }

    public Task<string?> GetFilePathAsync(string path)
    {
        var fullPath = Path.Combine(_baseFolder, path);
        if (fullPath.StartsWith(_baseFolder, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(fullPath))
            return Task.FromResult<string?>(fullPath);

        return Task.FromResult<string?>(null);
    }

    public Task<string?> GetFilePathAsync(string version, string filename)
    {
        var v6Path = Path.Combine(_baseFolder, "v6", version, filename);
        var v7Path = Path.Combine(_baseFolder, "v7", version, filename);

        if (File.Exists(v6Path))
            return Task.FromResult<string?>(v6Path);

        return File.Exists(v7Path)
            ? Task.FromResult<string?>(v7Path)
            : Task.FromResult<string?>(null);
    }

    private void LoadActiveVersions()
    {
        if (!File.Exists(_versionsFile))
            return;

        try
        {
            var content = File.ReadAllText(_versionsFile);
            var logs = JsonSerializer.Deserialize<List<VersionLog>>(content);

            if (logs?.Count > 0)
            {
                var latest = logs.Last();
                _activeV6Version = latest.V6Stable;
                _activeV7Fixed = latest.V7Fixed;
                _activeV7Latest = latest.V7Stable;

                _logger.LogInformation("Loaded active versions from history");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading active versions");
        }
    }

    /// <summary>
    ///     Получает историю версий из versions.json, отсортированную по времени (новейшие первыми)
    /// </summary>
    public async Task<List<VersionLog>> GetVersionHistoryAsync(int take = 50)
    {
        if (!File.Exists(_versionsFile))
            return [];

        try
        {
            var content = await File.ReadAllTextAsync(_versionsFile);
            var logs = JsonSerializer.Deserialize<List<VersionLog>>(content) ?? [];

            // Сортируем по времени (новейшие первыми) и берём последние N
            return logs
                .OrderByDescending(l => l.Timestamp)
                .Take(Math.Max(1, Math.Min(take, 500))) // Макс 500, мин 1
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading version history");
            return [];
        }
    }

    /// <summary>
    ///     Получает текст глобального CHANGELOG
    /// </summary>
    public async Task<string?> GetGlobalChangelogContentAsync()
    {
        try
        {
            var path = await GetGlobalChangelogPathAsync();
            if (path is null || !File.Exists(path))
                return null;

            return await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading global changelog");
            return null;
        }
    }

    /// <summary>
    ///     Получает текст CHANGELOG для конкретной версии
    /// </summary>
    public async Task<string?> GetChangelogContentAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        try
        {
            var path = await GetChangelogPathAsync(version);
            if (path is null || !File.Exists(path))
                return null;

            return await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading changelog for version {Version}", version);
            return null;
        }
    }

    /// <summary>
    ///     Строит карту pointer-файлов с соответствующими версиями
    /// </summary>
    private Dictionary<string, (string version, long build)> BuildPointerMap(
        string v6Version, long v6Build,
        string v7Fixed, long v7FixedBuild,
        string v7Latest, long v7LatestBuild)
    {
        var map = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase)
        {
            // v6 стабильные и long-term версии
            ["LATEST.6"] = (v6Version, v6Build),
            ["NEWEST6.stable"] = (v6Version, v6Build),
            ["NEWESTa6.stable"] = (v6Version, v6Build),
            ["NEWESTa6.long-term"] = (v6Version, v6Build),

            // Upgrade с v6 на v7 — используем v7 latest
            ["NEWEST6.upgrade"] = (v7Latest, v7LatestBuild),
            ["NEWESTa6.upgrade"] = (v7Latest, v7LatestBuild),

            // v7 стабильные
            ["NEWEST7.stable"] = (v7Fixed, v7FixedBuild),
            ["NEWESTa7.stable"] = (v7Latest, v7LatestBuild),
            ["LATEST.7"] = (v7Latest, v7LatestBuild),

            // Development каналы
            ["NEWESTa6.development"] = (v7Latest, v7LatestBuild),
            ["NEWEST6.development"] = (v7Latest, v7LatestBuild),
            ["NEWESTa7.development"] = (v7Latest, v7LatestBuild),
            ["NEWEST7.development"] = (v7Latest, v7LatestBuild),

            // Testing каналы
            ["NEWESTa6.testing"] = (v7Latest, v7LatestBuild),
            ["NEWEST6.testing"] = (v7Latest, v7LatestBuild),
            ["NEWESTa7.testing"] = (v7Latest, v7LatestBuild),
            ["NEWEST7.testing"] = (v7Latest, v7LatestBuild),

            // RC каналы
            ["NEWESTa6.release-candidate"] = (v7Latest, v7LatestBuild),
            ["NEWEST6.release-candidate"] = (v7Latest, v7LatestBuild),
            ["NEWESTa7.release-candidate"] = (v7Latest, v7LatestBuild),
            ["NEWEST7.release-candidate"] = (v7Latest, v7LatestBuild)
        };

        return map;
    }

    /// <summary>
    ///     Получает версию и build для pointer-файла через карту
    /// </summary>
    private (string? version, long build)? GetPointerVersionFromMap(string filename)
    {
        var map = BuildPointerMap(
            _activeV6Version, 0,
            _activeV7Fixed, 0,
            _activeV7Latest, 0);

        return map.TryGetValue(filename, out var result) ? result : null;
    }
}