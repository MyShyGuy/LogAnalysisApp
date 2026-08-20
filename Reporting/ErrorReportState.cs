namespace Krones.Lms.LogAnalysisApp.Reporting;

public sealed class ErrorReportState
{
    public string SourceName { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public string Level { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string ErrorKey { get; set; } = string.Empty;

    public int CountToday { get; set; }
}