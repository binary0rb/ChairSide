using Microsoft.Extensions.Options;

namespace ChairSide.Board.Options;

/// <summary>
/// Allocation behavior classifies how strongly staff should confirm a case's expected
/// allocation at seating. Stored as a stable string so serialization stays simple and
/// forward compatible. Operational metadata only - never PHI.
/// </summary>
public static class AllocationBehaviors
{
    /// <summary>Standardized procedure: expected allocation prepopulates with softer adjustment.</summary>
    public const string Known = "Known";

    /// <summary>Variable procedure: staff should be prompted more strongly to confirm allocation.</summary>
    public const string Variable = "Variable";

    public static bool IsValid(string? value) =>
        string.Equals(value, Known, StringComparison.Ordinal) ||
        string.Equals(value, Variable, StringComparison.Ordinal);
}

public sealed class ProcedureRosterOptions
{
    public const string SectionName = "ProcedureRosterOptions";

    public List<ProcedureRosterItem> Procedures { get; set; } = [];

    // DefaultExpectedUnits are placeholder operational baselines (1 unit = 10 minutes).
    // They are intentionally rough and easy to tune later; do not treat them as clinically
    // authoritative. AllocationBehavior follows the Known/Variable classification.
    public static List<ProcedureRosterItem> DefaultProcedures() =>
    [
        new()
        {
            Id = "consult",
            Code = "CON",
            Label = "Consult",
            Icon = "speech",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 1
        },
        new()
        {
            Id = "extraction",
            Code = "EXT",
            Label = "Extraction",
            Icon = "forceps",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 3
        },
        // Sedation is no longer a standalone selectable procedure. It is kept in the
        // roster as an inactive entry so historical records coded as "SED" still
        // resolve to a readable label. It is applied to new cases via the sedation
        // modifier on eligible primary procedures (see SedationEligible).
        new()
        {
            Id = "sedation",
            Code = "SED",
            Label = "Sedation",
            Icon = "moon",
            Active = false,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 3
        },
        new()
        {
            Id = "post-op",
            Code = "POST",
            Label = "Post-op",
            Icon = "check",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 1
        },
        new()
        {
            Id = "implant",
            Code = "IMP",
            Label = "Implant",
            Icon = "bolt",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 6
        },
        new()
        {
            Id = "biopsy",
            Code = "BX",
            Label = "Biopsy",
            Icon = "vial",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 2
        },
        new()
        {
            Id = "misc",
            Code = "MISC",
            Label = "Misc",
            Icon = "check",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 2
        },
        new()
        {
            Id = "periodic-exam",
            Code = "POE",
            Label = "Periodic Exam",
            Icon = "speech",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 1
        },
        new()
        {
            Id = "impressions",
            Code = "IMPRES",
            Label = "Impressions",
            Icon = "teeth",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 2
        },
        new()
        {
            Id = "integration-check",
            Code = "INTCK",
            Label = "Integration Check",
            Icon = "sync",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 1
        },
        new()
        {
            Id = "biopsy-post-op",
            Code = "BXPOST",
            Label = "Biopsy Post-op",
            Icon = "vial",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 1
        },
        new()
        {
            Id = "implant-removal",
            Code = "IMPRM",
            Label = "Implant Removal",
            Icon = "wrench",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 3
        },
        new()
        {
            Id = "phone-office-consult",
            Code = "PCOC",
            Label = "Phone -> Office Consult",
            Icon = "phone",
            Active = true,
            AllocationBehavior = AllocationBehaviors.Known,
            DefaultExpectedUnits = 1
        },
        new()
        {
            Id = "uncover",
            Code = "UNCOV",
            Label = "Uncover",
            Icon = "uncover",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 2
        },
        new()
        {
            Id = "expose-and-bond",
            Code = "EXBOND",
            Label = "Expose and Bond",
            Icon = "bond",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 3
        },
        new()
        {
            Id = "all-on-four",
            Code = "AO4",
            Label = "All on Four",
            Icon = "archfour",
            Active = true,
            SedationEligible = true,
            AllocationBehavior = AllocationBehaviors.Variable,
            DefaultExpectedUnits = 12
        }
    ];
}

public sealed class ProcedureRosterItem
{
    public string? Id { get; set; }

    public string Code { get; set; } = "";

    public string Label { get; set; } = "";

    public string Icon { get; set; } = "";

    public bool Active { get; set; } = true;

    /// <summary>
    /// When true, this primary procedure may be marked as a sedation case via the
    /// sedation modifier. Sedation is never a standalone procedure; it only ever
    /// qualifies an eligible primary procedure (e.g. Extraction + Sedation).
    /// </summary>
    public bool SedationEligible { get; set; }

    /// <summary>
    /// Known or Variable. Drives how strongly staff are prompted to confirm expected
    /// allocation at seating. Operational metadata only - never PHI.
    /// </summary>
    public string AllocationBehavior { get; set; } = AllocationBehaviors.Variable;

    /// <summary>
    /// Default expected allocation in 10-minute units (1 unit = 10 minutes). Prepopulates
    /// the case-level expected allocation snapshot at seating; staff may override it.
    /// </summary>
    public int DefaultExpectedUnits { get; set; } = 1;
}

public sealed class ProcedureRosterOptionsValidator : IValidateOptions<ProcedureRosterOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcedureRosterOptions options)
    {
        var failures = new List<string>();
        if (!options.Procedures.Any(procedure => procedure.Active))
        {
            failures.Add("ProcedureRosterOptions must include at least one active procedure.");
        }

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < options.Procedures.Count; index++)
        {
            var procedure = options.Procedures[index];
            var prefix = $"ProcedureRosterOptions:Procedures:{index}";
            if (string.IsNullOrWhiteSpace(procedure.Code))
            {
                failures.Add($"{prefix}:Code is required.");
            }
            else if (!seenCodes.Add(procedure.Code))
            {
                failures.Add("ProcedureRosterOptions:Procedures must have unique Code values.");
            }

            if (string.IsNullOrWhiteSpace(procedure.Label))
            {
                failures.Add($"{prefix}:Label is required.");
            }

            if (string.IsNullOrWhiteSpace(procedure.Icon))
            {
                failures.Add($"{prefix}:Icon is required.");
            }

            if (!AllocationBehaviors.IsValid(procedure.AllocationBehavior))
            {
                failures.Add($"{prefix}:AllocationBehavior must be either '{AllocationBehaviors.Known}' or '{AllocationBehaviors.Variable}'.");
            }

            if (procedure.DefaultExpectedUnits < 1)
            {
                failures.Add($"{prefix}:DefaultExpectedUnits must be greater than 0.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
