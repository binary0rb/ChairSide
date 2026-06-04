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
}
