using System.Text.Json;
using System.Text.RegularExpressions;

namespace Krones.Lms.LogAnalysisApp.Reporting;

/// <summary>Appends detected errors to the shared LMS error log file.</summary>
public sealed class ErrorLogWriter
{
    private static readonly Regex HeaderPrefixRegex = new(
        @"^\s*\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\s+.*?\b(?:TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\b\s*[:\-]?\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VariableUserManagementIdRegex = new(
        @"(?<!\S)\[\d+\](?=\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex StackTraceLineRegex = new(
        @"^\s+at\s+",
        RegexOptions.Compiled);

    private static readonly Regex VariableNumberRegex = new(
        @"\b(?:equipment|equipment with id|source id)\s*(?:=|')?\s*\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _filePath;
    private readonly string _statePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, ErrorReportState> _errors;

    public ErrorLogWriter(string filePath)
    {
        _filePath = filePath;
        _statePath = filePath + ".state.json";
        _errors = LoadState(_statePath);
    }

    public async Task AppendAsync(string sourceName, DateTime timestamp, string level, string message, CancellationToken cancellationToken)
    {
        await AppendBatchAsync([(sourceName, timestamp, level, message)], cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendBatchAsync(
        IEnumerable<(string SourceName, DateTime Timestamp, string Level, string Message)> entries,
        CancellationToken cancellationToken)
    {
        var batch = entries.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_filePath);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var today = DateTime.Today;
            RemoveEntriesFromPreviousDays(today);

            foreach (var entry in batch)
            {
                var normalizedMessage = NormalizeMessage(entry.Message);
                var errorKey = CreateErrorKey(normalizedMessage);
                var key = $"{today:yyyy-MM-dd}|{entry.SourceName}|{errorKey}";
                if (!_errors.TryGetValue(key, out var error))
                {
                    error = new ErrorReportState { SourceName = entry.SourceName, Timestamp = entry.Timestamp, Level = entry.Level, Message = normalizedMessage, ErrorKey = errorKey, CountToday = 0 };
                    _errors[key] = error;
                }

                error.SourceName = entry.SourceName;
                error.Timestamp = entry.Timestamp;
                error.Level = entry.Level;
                error.ErrorKey = errorKey;
                error.CountToday++;
            }

            if (_errors.Count == 0)
            {
                return;
            }

            var lines = _errors.Values
                .OrderBy(item => item.Timestamp)
                .Select(item => $"{item.Timestamp:yyyy-MM-dd HH:mm:ss} [{item.Level}] [{item.SourceName}] {item.Message} Count: {item.CountToday}")
                .ToArray();
            await ReplaceFileAtomicallyAsync(_filePath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            await ReplaceFileAtomicallyAsync(_statePath, JsonSerializer.Serialize(_errors), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Dictionary<string, ErrorReportState> LoadState(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, ErrorReportState>();
        }

        var loaded = JsonSerializer.Deserialize<Dictionary<string, ErrorReportState>>(File.ReadAllText(path))
            ?? new Dictionary<string, ErrorReportState>();
        var migrated = new Dictionary<string, ErrorReportState>();

        foreach (var error in loaded.Values)
        {
            error.Message = NormalizeMessage(error.Message);
            error.ErrorKey = CreateErrorKey(error.Message);
            var key = $"{error.Timestamp:yyyy-MM-dd}|{error.SourceName}|{error.ErrorKey}";
            if (migrated.TryGetValue(key, out var existing))
            {
                if (error.Timestamp > existing.Timestamp)
                {
                    existing.Timestamp = error.Timestamp;
                    existing.Level = error.Level;
                    existing.SourceName = error.SourceName;
                }

                existing.CountToday += error.CountToday;
            }
            else
            {
                migrated[key] = error;
            }
        }

        return migrated;
    }

    private void RemoveEntriesFromPreviousDays(DateTime today)
    {
        foreach (var key in _errors
            .Where(pair => pair.Value.Timestamp.Date != today.Date)
            .Select(pair => pair.Key)
            .ToList())
        {
            _errors.Remove(key);
        }
    }

    private static string NormalizeMessage(string message)
    {
        var lines = message.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length > 0)
        {
            lines[0] = HeaderPrefixRegex.Replace(lines[0], string.Empty);
        }

        var relevantLines = lines
            .Where(line => !StackTraceLineRegex.IsMatch(line))
            .Take(2)
            .ToArray();
        var normalized = VariableUserManagementIdRegex.Replace(string.Join(Environment.NewLine, relevantLines).Trim(), string.Empty);
        normalized = VariableNumberRegex.Replace(normalized, match =>
        {
            var value = match.Value;
            var numberIndex = value.LastIndexOfAny(['=', '\'', ' ']);
            return numberIndex >= 0 ? value[..(numberIndex + 1)] + "#" : value;
        });
        return normalized;
    }

    private static string CreateErrorKey(string message)
    {
        var lines = message.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var keyMessage = lines.Length > 1 && lines[1].Contains("BadRequestTimeout", StringComparison.OrdinalIgnoreCase)
            ? lines[0] + " BadRequestTimeout"
            : lines.FirstOrDefault() ?? message;

        return keyMessage;
    }

    private static async Task ReplaceFileAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
