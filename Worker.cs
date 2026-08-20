using System.Text.RegularExpressions;
using Krones.Lms.LogAnalysisApp.Configuration;
using Krones.Lms.LogAnalysisApp.Reporting;
using Krones.Lms.LogAnalysisApp.Scanning;
using Krones.Lms.LogAnalysisApp.State;
using Microsoft.Extensions.Options;

namespace Krones.Lms.LogAnalysisApp;

/// <summary>Periodically polls all configured LMS log files and reports new ERROR entries.</summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly LogAnalysisOptions _options;
    private readonly List<LogFileScanner> _scanners;

    public Worker(ILogger<Worker> logger, IOptions<LogAnalysisOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        var baseDirectory = AppContext.BaseDirectory;
        var headerRegex = new Regex(_options.HeaderRegexPattern, RegexOptions.Compiled);
        var statePath = Path.GetFullPath(Path.Combine(baseDirectory, _options.StateFilePath));
        var errorLogPath = Path.GetFullPath(Path.Combine(baseDirectory, _options.ErrorLogPath));

        var stateStore = new ScanStateStore(statePath);
        var errorLogWriter = new ErrorLogWriter(errorLogPath);

        _scanners = _options.Sources
            .Select(source => new LogFileScanner(source, baseDirectory, headerRegex, stateStore, errorLogWriter, _logger))
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.PollingIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Console.Clear();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The output may be redirected instead of connected to a console.
            }

            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Scan gestartet.");
            var errorsSinceLastScan = 0;

            foreach (var scanner in _scanners)
            {
                try
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Quelle wird gescannt: {scanner.SourceName}");
                    var sourceErrorCount = await scanner.ScanAsync(stoppingToken).ConfigureAwait(false);
                    errorsSinceLastScan += sourceErrorCount;
                    WriteScanResult(scanner.SourceName, sourceErrorCount);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to scan log source");
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Quelle fehlgeschlagen: {scanner.SourceName}: {ex.Message}");
                }
            }

            WriteColored(
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Scan abgeschlossen. Fehler seit dem letzten Scan: {errorsSinceLastScan}.",
                errorsSinceLastScan > 0 ? ConsoleColor.Red : ConsoleColor.Green);

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested.
            }
        }
    }

    private static void WriteScanResult(string sourceName, int errorCount)
    {
        WriteColored(
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Quelle abgeschlossen: {sourceName} ({errorCount} relevante Eintraege).",
            errorCount > 0 ? ConsoleColor.Red : ConsoleColor.Green);
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        try
        {
            var previousColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previousColor;
        }
        catch (IOException)
        {
            Console.WriteLine(text);
        }
    }
}
