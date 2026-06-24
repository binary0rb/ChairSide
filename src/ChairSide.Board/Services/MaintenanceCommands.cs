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
public sealed record MaintenanceResolution(MaintenanceOutcome Outcome, string? Command, string? RefusalReason);

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

    public const string TrainingSeedToken = "RESET_TRAINING_DATA";
    public const string EmptyBetaToken = "RESET_EMPTY_BETA";

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
        else
        {
            return new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"Unknown maintenance command '{command}'. Expected '{TrainingSeedCommand}' or '{EmptyBetaCommand}'.");
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

        return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null);
    }

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
