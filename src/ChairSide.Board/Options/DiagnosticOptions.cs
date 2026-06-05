namespace ChairSide.Board.Options;

public sealed class DiagnosticOptions
{
    public const string SectionName = "DiagnosticOptions";

    /// <summary>
    /// Directory where diagnostic log files are written.
    /// Use an absolute path (e.g. C:\ChairSide\Logs) in production.
    /// Relative paths are resolved from the application content root.
    /// </summary>
    public string LogDirectory { get; set; } = "./logs";

    /// <summary>
    /// Maximum size in bytes for each log file before it is rotated.
    /// When the file meets or exceeds this size, it is renamed to .log.1 and a fresh
    /// file begins. Only one generation is kept. Set to 0 to disable rotation.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 10_000_000; // 10 MB

    /// <summary>
    /// Maximum number of POST requests to /api/client-errors allowed per source IP
    /// per minute. Requests beyond the limit are rejected with HTTP 429 and are not
    /// logged. Set to 0 to disable rate limiting.
    /// </summary>
    public int ClientErrorRateLimitPerMinute { get; set; } = 30;
}
