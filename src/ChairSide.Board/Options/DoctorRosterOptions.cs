using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

namespace ChairSide.Board.Options;

public sealed class DoctorRosterOptions
{
    public const string SectionName = "DoctorRosterOptions";

    public List<DoctorRosterItem> Doctors { get; set; } = [];

    public static List<DoctorRosterItem> DefaultDoctors() =>
    [
        new()
        {
            Id = "otte",
            DisplayName = "Dr. Otte",
            ShortName = "Otte",
            Color = "#2563eb",
            Active = true
        },
        new()
        {
            Id = "pledger",
            DisplayName = "Dr. Pledger",
            ShortName = "Pledger",
            Color = "#16a34a",
            Active = true
        },
        new()
        {
            Id = "gibson",
            DisplayName = "Dr. Gibson",
            ShortName = "Gibson",
            Color = "#f97316",
            Active = true
        },
        new()
        {
            Id = "schroeder",
            DisplayName = "Dr. Schroeder",
            ShortName = "Schroeder",
            Color = "#7c3aed",
            Active = true
        }
    ];
}

public sealed class DoctorRosterItem
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string ShortName { get; set; } = "";

    public string Color { get; set; } = "";

    public bool Active { get; set; } = true;
}

public sealed partial class DoctorRosterOptionsValidator : IValidateOptions<DoctorRosterOptions>
{
    public ValidateOptionsResult Validate(string? name, DoctorRosterOptions options)
    {
        var failures = new List<string>();
        if (!options.Doctors.Any(doctor => doctor.Active))
        {
            failures.Add("DoctorRosterOptions must include at least one active doctor.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < options.Doctors.Count; index++)
        {
            var doctor = options.Doctors[index];
            var prefix = $"DoctorRosterOptions:Doctors:{index}";
            if (string.IsNullOrWhiteSpace(doctor.Id))
            {
                failures.Add($"{prefix}:Id is required.");
            }
            else if (!seenIds.Add(doctor.Id))
            {
                failures.Add("DoctorRosterOptions:Doctors must have unique Id values.");
            }

            if (string.IsNullOrWhiteSpace(doctor.DisplayName))
            {
                failures.Add($"{prefix}:DisplayName is required.");
            }

            if (string.IsNullOrWhiteSpace(doctor.ShortName))
            {
                failures.Add($"{prefix}:ShortName is required.");
            }

            if (string.IsNullOrWhiteSpace(doctor.Color) || !HexColorRegex().IsMatch(doctor.Color))
            {
                failures.Add($"{prefix}:Color must be a valid hex color such as #2563eb.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorRegex();
}
