using System.IO.Compression;
using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

public class InMemoryLogStore : ILogStore
{
    private const int MaxEntries = 10_000;

    // Кольцевой буфер
    private readonly LogEntry?[] _buffer = new LogEntry[MaxEntries];

    private readonly Lock _syncRoot = new();

    // Текущее количество валидных записей (0..MaxEntries)
    private int _count;

    // nextIndex — позиция, куда писать следующий элемент
    private int _nextIndex;

    public void Add(LogEntry entry)
    {
        if (entry is null)
            throw new ArgumentNullException(nameof(entry));

        lock (_syncRoot)
        {
            _buffer[_nextIndex] = entry;

            if (_count < MaxEntries) _count++;

            _nextIndex++;
            if (_nextIndex >= MaxEntries)
                _nextIndex = 0;
        }
    }

    public IReadOnlyList<LogEntry> Query(string? level, string? search, int take)
    {
        if (take <= 0) take = 100;
        if (take > 1000) take = 1000;

        var snapshot = Snapshot();
        if (snapshot.Length == 0)
            return [];

        var hasLevelFilter = !string.IsNullOrWhiteSpace(level);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(search);

        var normalizedLevel = hasLevelFilter ? level : null;
        var searchText = hasSearchFilter ? search! : string.Empty;

        var result = new List<LogEntry>(take);

        // Идём с конца (от новых к старым), пока не наберём нужное количество
        for (var i = snapshot.Length - 1; i >= 0 && result.Count < take; i--)
        {
            var e = snapshot[i];

            if (hasLevelFilter &&
                !string.Equals(e.Level, normalizedLevel, StringComparison.OrdinalIgnoreCase))
                continue;

            if (hasSearchFilter && !MatchesSearch(e, searchText))
                continue;

            result.Add(e);
        }

        return result;
    }

    public LogStats GetStats()
    {
        var snapshot = Snapshot();
        if (snapshot.Length == 0)
            return new LogStats {TotalEntries = 0};

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
        var snapshot = Snapshot();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry(
                $"logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt",
                CompressionLevel.Fastest);

            using var writer = new StreamWriter(entry.Open());

            // Пишем по возрастанию времени
            foreach (var log in snapshot.OrderBy(e => e.Timestamp))
                writer.WriteLine(
                    $"{log.Timestamp:O}\t{log.Level}\t{log.Source}\t{log.Message}\t{log.Exception}");
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     Берём снапшот логов в хронологическом порядке: от старых к новым.
    /// </summary>
    private LogEntry[] Snapshot()
    {
        lock (_syncRoot)
        {
            if (_count == 0)
                return [];

            var result = new LogEntry[_count];

            // Индекс самого старого элемента
            var oldestIndex = _nextIndex - _count;
            if (oldestIndex < 0)
                oldestIndex += MaxEntries;

            var idx = oldestIndex;
            for (var i = 0; i < _count; i++)
            {
                var entry = _buffer[idx];
                // Если внутри буфера null
                if (entry is null)
                    continue;

                result[i] = entry;

                idx++;
                if (idx >= MaxEntries)
                    idx = 0;
            }

            return result;
        }
    }

    private static bool MatchesSearch(LogEntry e, string search)
    {
        if (string.IsNullOrEmpty(search))
            return true;

        if (!string.IsNullOrEmpty(e.Source) &&
            e.Source.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (!string.IsNullOrEmpty(e.Message) &&
            e.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return !string.IsNullOrEmpty(e.Exception) &&
               e.Exception.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}