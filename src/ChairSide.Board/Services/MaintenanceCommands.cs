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
    // The parsed --completed-cycles target for the large synthetic seed command, or for the
    // reset-stress-fixture command's reporting-volume profile; null for every other command/profile
    // (and for refusals). Already validated to the accepted range when non-null.
    int? CompletedCycles = null,
    // The parsed --profile value for the reset-stress-fixture command; null for every other command
    // (and for refusals). Already validated against the allowed profile set when non-null.
    string? Profile = null);

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
    public const string StressFixtureCommand = "reset-stress-fixture";

    public const string TrainingSeedToken = "RESET_TRAINING_DATA";
    public const string EmptyBetaToken = "RESET_EMPTY_BETA";
    public const string LargeSyntheticSeedToken = "RESET_LARGE_SYNTHETIC_REPORT_DATA";
    public const string StressFixtureToken = "RESET_STRESS_FIXTURE";

    // Optional count argument for the large synthetic seed command, plus its accepted range. These
    // are the single source of truth for the range; DemoBoardStore clamps to the same bounds as
    // defense-in-depth. A value outside the range is refused here so no mutation ever happens.
    // Also reused (via ResolveCompletedCycles) by reset-stress-fixture's reporting-volume profile,
    // which delegates to the same large synthetic seeder.
    public const string CompletedCyclesFlag = "--completed-cycles";
    public const int DefaultCompletedCycles = 1000;
    public const int MinCompletedCycles = 100;
    public const int MaxCompletedCycles = 10000;

    // Required profile selector for reset-stress-fixture. There is no default profile - the operator
    // must name one explicitly so a stress fixture can never be seeded by accident.
    public const string ProfileFlag = "--profile";

    public const string ProfileReportingVolume = "reporting-volume";
    public const string ProfileLiveBoardStress = "live-board-stress";
    public const string ProfileDoctorViewStress = "doctor-view-stress";
    public const string ProfileDoctorViewOverflowStress = "doctor-view-overflow-stress";
    public const string ProfileScenarioRich = "scenario-rich";
    public const string ProfileFullStress = "full-stress";
    public const string ProfileAllScenarios = "all-scenarios";

    // Single source of truth for which --profile values reset-stress-fixture accepts.
    public static readonly IReadOnlyList<string> StressFixtureProfiles =
    [
        ProfileReportingVolume,
        ProfileLiveBoardStress,
        ProfileDoctorViewStress,
        ProfileDoctorViewOverflowStress,
        ProfileScenarioRich,
        ProfileFullStress,
        ProfileAllScenarios
    ];

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
        else if (string.Equals(command, StressFixtureCommand, StringComparison.Ordinal))
        {
            requiredToken = StressFixtureToken;
        }
        else
        {
            return new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"Unknown maintenance command '{command}'. Expected '{TrainingSeedCommand}', '{EmptyBetaCommand}', '{LargeSyntheticSeedCommand}', or '{StressFixtureCommand}'.");
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
            var (completedCycles, refusal) = ResolveCompletedCycles(args, command);
            if (refusal is not null)
            {
                return refusal;
            }

            return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null, completedCycles);
        }

        // reset-stress-fixture requires an explicit --profile from the allowed set - there is no
        // default profile, so a stress fixture can never be seeded by accident. --completed-cycles is
        // only meaningful (and only parsed) for the reporting-volume profile, which delegates to the
        // same large synthetic seeder as reset-large-synthetic-report-data.
        if (string.Equals(command, StressFixtureCommand, StringComparison.Ordinal))
        {
            var profile = GetFlagValue(args, ProfileFlag);
            if (string.IsNullOrEmpty(profile))
            {
                return new MaintenanceResolution(
                    MaintenanceOutcome.Refused,
                    command,
                    $"Missing {ProfileFlag}. '{command}' requires one of: {string.Join(", ", StressFixtureProfiles)}.");
            }

            if (!StressFixtureProfiles.Contains(profile, StringComparer.Ordinal))
            {
                return new MaintenanceResolution(
                    MaintenanceOutcome.Refused,
                    command,
                    $"Unknown {ProfileFlag} '{profile}'. Expected one of: {string.Join(", ", StressFixtureProfiles)}.");
            }

            if (profile is ProfileReportingVolume or ProfileAllScenarios)
            {
                var (completedCycles, refusal) = ResolveCompletedCycles(args, command);
                if (refusal is not null)
                {
                    return refusal;
                }

                return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null, completedCycles, profile);
            }

            // --completed-cycles only means something for the reporting-volume profile and the
            // all-scenarios profile (which also delegates part of its seeding to the large synthetic
            // seeder). Refuse rather than silently ignore it for every other profile, so a typo'd or
            // misremembered flag never seeds a different fixture than the operator expected.
            if (GetFlagValue(args, CompletedCyclesFlag) is not null)
            {
                return new MaintenanceResolution(
                    MaintenanceOutcome.Refused,
                    command,
                    $"{CompletedCyclesFlag} is only valid with {ProfileFlag} {ProfileReportingVolume} or {ProfileFlag} {ProfileAllScenarios}. Remove {CompletedCyclesFlag} or switch to one of those profiles.");
            }

            return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null, null, profile);
        }

        return new MaintenanceResolution(MaintenanceOutcome.Authorized, command, null);
    }

    // Shared --completed-cycles parsing for commands/profiles that accept it (the large synthetic
    // seed command, and reset-stress-fixture's reporting-volume profile). An absent flag uses the
    // default; a present-but-non-numeric or out-of-range value returns a refusal with no mutation.
    private static (int CompletedCycles, MaintenanceResolution? Refusal) ResolveCompletedCycles(string[] args, string command)
    {
        var rawCount = GetFlagValue(args, CompletedCyclesFlag);
        var completedCycles = DefaultCompletedCycles;
        if (rawCount is null)
        {
            return (completedCycles, null);
        }

        if (!int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out completedCycles))
        {
            return (0, new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"{CompletedCyclesFlag} must be a whole number between {MinCompletedCycles} and {MaxCompletedCycles}."));
        }

        if (completedCycles < MinCompletedCycles || completedCycles > MaxCompletedCycles)
        {
            return (0, new MaintenanceResolution(
                MaintenanceOutcome.Refused,
                command,
                $"{CompletedCyclesFlag} must be between {MinCompletedCycles} and {MaxCompletedCycles}. Received {completedCycles}."));
        }

        return (completedCycles, null);
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

/// <summary>
/// Fail-closed environment gate for operator-run maintenance. Confirmation-token and command-shape
/// validation remain in <see cref="MaintenanceCommands.Resolve"/>; this gate runs before the callback
/// is allowed to build the application or resolve any service.
/// </summary>
public static class MaintenanceExecutionPolicy
{
    private static readonly HashSet<string> AllowedDestructiveCommands = new(StringComparer.Ordinal)
    {
        MaintenanceCommands.TrainingSeedCommand,
        MaintenanceCommands.EmptyBetaCommand,
        MaintenanceCommands.LargeSyntheticSeedCommand,
        MaintenanceCommands.StressFixtureCommand
    };

    public static bool IsAllowed(DeploymentEnvironment environment, string? command) =>
        command is not null
        && environment.Role is DeploymentRole.Development or DeploymentRole.Training
        && AllowedDestructiveCommands.Contains(command);

    public static int Execute(
        DeploymentEnvironment environment,
        MaintenanceResolution resolution,
        Func<int> authorizedAction,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(authorizedAction);
        error ??= Console.Error;

        if (resolution.Outcome == MaintenanceOutcome.NotRequested)
        {
            WriteRefusal(error, "No maintenance command was requested.");
            return 2;
        }

        if (resolution.Outcome == MaintenanceOutcome.Refused)
        {
            WriteRefusal(error, resolution.RefusalReason ?? "Maintenance command was refused.");
            return 2;
        }

        if (!IsAllowed(environment, resolution.Command))
        {
            WriteRefusal(
                error,
                $"'{resolution.Command ?? "<unknown>"}' cannot run in {environment.EnvironmentName}.");
            return 2;
        }

        return authorizedAction();
    }

    private static void WriteRefusal(TextWriter error, string reason)
    {
        error.WriteLine($"[ChairSide Maintenance] Refused: {reason}");
        error.WriteLine("[ChairSide Maintenance] No data was changed.");
    }
}
