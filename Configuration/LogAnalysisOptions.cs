namespace Krones.Lms.LogAnalysisApp.Configuration;

public sealed class LogAnalysisOptions
{
    public const string SectionName = "LogAnalysis";

    public int PollingIntervalMinutes { get; set; } = 2;

    public string StateFilePath { get; set; } = "State/scanner-state.json";

    public string ErrorLogPath { get; set; } = "Log/LmsErrorLog.log";

    /// <summary>Regex used to detect the start of a new log entry (must expose "date" and "level" groups).</summary>
    public string HeaderRegexPattern { get; set; } =
        @"^(?<date>\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)\s.*?\b(?<level>TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\b";

    public List<LogSourceOptions> Sources { get; set; } = new();
}

public sealed class LogSourceOptions
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}
