using System.Globalization;

namespace ChairSide.Board.Services;

/// <summary>Outcome of resolving operator-supplied maintenance command-line arguments.</summary>
public enum MaintenanceOutcome
{
    /// <summary>No <c>--maintenance</c> flag present; the app should start normally.</summary>
    NotRequested,

    /// <summary>A known command with a matching confirmation token; safe to execute.</summary>
    Authorized,

    /// <summary>Unknown command, or a missing/incorrect confirmation token; do not mutate.</summary>
    Refused
}

/// <summary>Result of parsing maintenance arguments. Pure data - no side effects.</summary>
public sealed record MaintenanceResolution(
    MaintenanceOutcome Outcome,
    string? Command,
    string? RefusalReason,
    // The parsed --completed-cycles target for the large synthetic seed command; null for every
    // other command (and for refusals). Already validated to the accepted range when non-null.
    int? CompletedCycles = null);

/// <summary>
/// Parses the operator-run maintenance CLI arguments and enforces the per-command confirmation
/// token. Pure and side-effect free so it is unit-testable and so refusals can never mutate data.
/// Destructive work happens only after this returns <see cref="MaintenanceOutcome.Authorized"/>.
/// </summary>
public static class MaintenanceCommands
{
    public const string MaintenanceFlag = "--maintenance";
    public const string ConfirmFlag = "--confirm";

    public const string TrainingSeedCommand = "reset-training-data";
    public const string EmptyBetaCommand = "reset-empty";
    public const string LargeSyntheticSeedCommand = "reset-large-synthetic-report-data";

    public const string TrainingSeedToken = "RESET_TRAINING_DATA";
    public const string EmptyBetaToken = "RESET_EMPTY_BETA";
    public const string LargeSyntheticSeedToken = "RESET_LARGE_SYNTHETIC_REPORT_DATA";

    // Optional count argument for the large synthetic seed command, plus its accepted range. These
    // are the single source of truth for the range; DemoBoardStore clamps to the same bounds as
    // defense-in-depth. A value outside the range is refused here so no mutation ever happens.
    public const string CompletedCyclesFlag = "--completed-cycles";
    public const int DefaultCompletedCycles = 1000;
    public const int MinCompletedCycles = 100;
    public const int MaxCompletedCycles = 10000;

    public static MaintenanceResolution Resolve(string[] args)
    {
        var command = GetFlagValue(args, MaintenanceFlag);
        if (command is null)
        {
            return new MaintenanceResolution(MaintenanceOutcome.NotRequested, null, null);
        }

        string requiredToken;
        if (string.Equals(command, TrainingSeedCommand, StringComparison.Ordinal))
        {
            requiredToken = TrainingSeedToken;
        }
        else if (string.Equals(command, EmptyBetaCommand, StringComparison.Ordinal))
        {
            requiredToken = EmptyBetaToken;
        }
        else if (string.Equals(command, LargeSyntheticSeedCommand, StringComparison.Ordinal))
        {
            requiredToken = LargeSyntheticSeedToken;
        }
        else
        {
            return new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"Unknown maintenance command '{command}'. Expected '{TrainingSeedCommand}', '{EmptyBetaCommand}', or '{LargeSyntheticSeedCommand}'.");
        }

        var token = GetFlagValue(args, ConfirmFlag);
        if (string.IsNullOrEmpty(token))
        {
            return new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"Missing confirmation. '{command}' requires {ConfirmFlag} {requiredToken}.");
        }

        if (!string.Equals(token, requiredToken, StringComparison.Ordinal))
        {
            return new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"Confirmation token does not match. '{command}' requires {ConfirmFlag} {requiredToken}.");
        }

        // The large synthetic seed takes an optional --completed-cycles count. An absent flag uses
        // the default; a present-but-non-numeric or out-of-range value is refused with no mutation.
        if (string.Equals(command, LargeSyntheticSeedCommand, StringComparison.Ordinal))
        {
            var rawCount = GetFlagValue(args, CompletedCyclesFlag);
            var completedCycles = DefaultCompletedCycles;
            if (rawCount is not null)
            {
                if (!int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out completedCycles))
                {
                    return new MaintenanceResolution(
                        MaintenanceOutcome.Refused,
                        command,
                        $"{CompletedCyclesFlag} must be a whole number between {MinCompletedCycles} and {MaxCompletedCycles}.");
                }

                if (completedCycles < MinCompletedCycles || completedCycles > MaxCompletedCycles)
                {
                    return new MaintenanceResolution(
                        MaintenanceOutcome.Refused,
                        command,
                        $"{CompletedCyclesFlag} must be between {MinCompletedCycles} and {MaxCompletedCycles}. Received {completedCycles}.");
                }
            }

            return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null, completedCycles);
        }

        return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null);
    }

    /// <summary>
    /// True for maintenance commands that must never run against a Production database, regardless of
    /// a correct confirmation token. The environment check itself lives in the maintenance runner;
    /// this keeps the "which commands are Production-forbidden" policy pure and unit-testable.
    /// </summary>
    public static bool IsProductionForbidden(string? command) =>
        string.Equals(command, LargeSyntheticSeedCommand, StringComparison.Ordinal);

    // Returns the value following the given flag, or null when the flag is absent or has no value.
    private static string? GetFlagValue(string[] args, string flag)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], flag, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
