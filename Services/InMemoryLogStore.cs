using System.Collections.Concurrent;
using System.IO.Compression;
using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

public class InMemoryLogStore : ILogStore
{
    private const int MaxEntries = 10_000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);

        // Ограничиваем количество записей в памяти
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<LogEntry> Query(string? level, string? search, int take)
    {
        if (take <= 0) take = 100;
        if (take > 1000) take = 1000;

        var query = _entries.Reverse(); // новые сверху

        if (!string.IsNullOrWhiteSpace(level))
            query = query.Where(e =>
                string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLowerInvariant();
            query = query.Where(e =>
                e.Source.ToLowerInvariant().Contains(s) ||
                e.Message.ToLowerInvariant().Contains(s) ||
                (e.Exception?.ToLowerInvariant().Contains(s) ?? false));
        }

        return query.Take(take).ToArray();
    }

    public LogStats GetStats()
    {
        var snapshot = _entries.ToArray();
        if (snapshot.Length == 0)
            return new LogStats { TotalEntries = 0 };

        return new LogStats
        {
            TotalEntries = snapshot.Length,
            InfoCount = snapshot.Count(e =>
                string.Equals(e.Level, "Information", StringComparison.OrdinalIgnoreCase)),
            WarningCount = snapshot.Count(e =>
                string.Equals(e.Level, "Warning", StringComparison.OrdinalIgnoreCase)),
            ErrorCount = snapshot.Count(e =>
                string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)),
            OldestEntry = snapshot.Min(e => e.Timestamp),
            NewestEntry = snapshot.Max(e => e.Timestamp)
        };
    }

    public byte[] ExportAsZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry(
                $"logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt",
                CompressionLevel.Fastest);

            using var writer = new StreamWriter(entry.Open());
            foreach (var log in _entries.OrderBy(e => e.Timestamp))
                writer.WriteLine(
                    $"{log.Timestamp:O}\t{log.Level}\t{log.Source}\t{log.Message}\t{log.Exception}");
        }

        return ms.ToArray();
    }
}