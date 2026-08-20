using System.Text;
using System.Text.RegularExpressions;
using Krones.Lms.LogAnalysisApp.Configuration;
using Krones.Lms.LogAnalysisApp.Reporting;
using Krones.Lms.LogAnalysisApp.State;
using Microsoft.Extensions.Logging;

namespace Krones.Lms.LogAnalysisApp.Scanning;

/// <summary>A single detected log entry (header line plus any stack-trace/continuation lines).</summary>
internal sealed class LogEntry
{
    public LogEntry(DateTime? timestamp, string level, string firstLine)
    {
        Timestamp = timestamp;
        Level = level;
        Text = new StringBuilder(firstLine);
    }

    public DateTime? Timestamp { get; }

    public string Level { get; }

    public StringBuilder Text { get; }
}

/// <summary>
/// Reads new content appended to a single log file since the last poll, extracts ERROR entries
/// and forwards them to the shared LMS error log.
/// </summary>
public sealed class LogFileScanner
{
    private readonly LogSourceOptions _source;
    private readonly string _fullPath;
    private readonly Regex _headerRegex;
    private readonly ScanStateStore _stateStore;
    private readonly ErrorLogWriter _errorLogWriter;
    private readonly ILogger _logger;

    public string SourceName => _source.Name;

    public LogFileScanner(
        LogSourceOptions source,
        string baseDirectory,
        Regex headerRegex,
        ScanStateStore stateStore,
        ErrorLogWriter errorLogWriter,
        ILogger logger)
    {
        _source = source;
        _fullPath = Path.GetFullPath(Path.Combine(baseDirectory, source.Path));
        _headerRegex = headerRegex;
        _stateStore = stateStore;
        _errorLogWriter = errorLogWriter;
        _logger = logger;
    }

    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_fullPath))
        {
            _logger.LogWarning("Log file for source {Source} not found at {Path}", _source.Name, _fullPath);
            return 0;
        }

        var previousState = _stateStore.TryGet(_source.Name);
        var isFirstScan = previousState is null;

        long fileLength;
        using (var probe = new FileStream(_fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fileLength = probe.Length;
        }

        // A saved offset larger than the current file length means the log was rotated/truncated; start over.
        var startOffset = previousState is null || previousState.Offset > fileLength ? 0 : previousState.Offset;

        if (startOffset >= fileLength)
        {
            await _stateStore.SetAsync(_source.Name, new LogSourceState { Offset = startOffset, LastRunDate = DateOnly.FromDateTime(DateTime.Today) }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        byte[] buffer;
        using (var stream = new FileStream(_fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(startOffset, SeekOrigin.Begin);
            var length = (int)(stream.Length - startOffset);
            buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }
        }

        var lines = SplitCompleteLines(buffer, startOffset);
        if (lines.Count == 0)
        {
            return 0;
        }

        var entries = BuildEntries(lines);
        var today = DateTime.Today;
        var errorCount = 0;
        var errorsToWrite = new List<(string SourceName, DateTime Timestamp, string Level, string Message)>();

        foreach (var entry in entries)
        {
            if (entry.Level is not ("WARN" or "ERROR" or "FATAL"))
            {
                continue;
            }

            if (isFirstScan && entry.Timestamp.HasValue && entry.Timestamp.Value.Date != today)
            {
                continue;
            }

            var timestamp = entry.Timestamp ?? DateTime.Now;
            errorsToWrite.Add((_source.Name, timestamp, entry.Level, entry.Text.ToString()));
            errorCount++;
        }

        await _errorLogWriter.AppendBatchAsync(errorsToWrite, cancellationToken).ConfigureAwait(false);

        var processedOffset = lines[^1].EndOffsetExclusive;
        await _stateStore.SetAsync(
            _source.Name,
            new LogSourceState { Offset = processedOffset, LastRunDate = DateOnly.FromDateTime(today) },
            cancellationToken).ConfigureAwait(false);

        return errorCount;
    }

    private List<LogEntry> BuildEntries(List<(long StartOffset, long EndOffsetExclusive, string Text)> lines)
    {
        var entries = new List<LogEntry>();
        LogEntry? current = null;

        foreach (var (_, _, text) in lines)
        {
            var match = _headerRegex.Match(text);
            if (match.Success)
            {
                if (current is not null)
                {
                    entries.Add(current);
                }

                DateTime? timestamp = DateTime.TryParse(match.Groups["date"].Value, out var parsed) ? parsed : null;
                current = new LogEntry(timestamp, match.Groups["level"].Value, text);
            }
            else
            {
                // Continuation line (e.g. exception stack trace) belonging to the previous header line.
                current?.Text.Append(Environment.NewLine).Append(text);
            }
        }

        if (current is not null)
        {
            entries.Add(current);
        }

        return entries;
    }

    private static List<(long StartOffset, long EndOffsetExclusive, string Text)> SplitCompleteLines(byte[] buffer, long baseOffset)
    {
        var lines = new List<(long, long, string)>();
        var start = 0;

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != (byte)'\n')
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer, start, i - start + 1).TrimEnd('\r', '\n');
            lines.Add((baseOffset + start, baseOffset + i + 1, text));
            start = i + 1;
        }

        return lines;
    }
}
