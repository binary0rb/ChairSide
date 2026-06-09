using Microsoft.Extensions.Options;

namespace ChairSide.Board.Options;

public sealed class ProcedureRosterOptions
{
    public const string SectionName = "ProcedureRosterOptions";

    public List<ProcedureRosterItem> Procedures { get; set; } = [];

    public static List<ProcedureRosterItem> DefaultProcedures() =>
    [
        new()
        {
            Id = "consult",
            Code = "CON",
            Label = "Consult",
            Icon = "speech",
            Active = true
        },
        new()
        {
            Id = "extraction",
            Code = "EXT",
            Label = "Extraction",
            Icon = "forceps",
            Active = true
        },
        new()
        {
            Id = "sedation",
            Code = "SED",
            Label = "Sedation",
            Icon = "moon",
            Active = true
        },
        new()
        {
            Id = "post-op",
            Code = "POST",
            Label = "Post-op",
            Icon = "check",
            Active = true
        },
        new()
        {
            Id = "implant",
            Code = "IMP",
            Label = "Implant",
            Icon = "bolt",
            Active = true
        },
        new()
        {
            Id = "biopsy",
            Code = "BX",
            Label = "Biopsy",
            Icon = "vial",
            Active = true
        },
        new()
        {
            Id = "misc",
            Code = "MISC",
            Label = "Misc",
            Icon = "check",
            Active = true
        },
        new()
        {
            Id = "periodic-exam",
            Code = "POE",
            Label = "Periodic Exam",
            Icon = "speech",
            Active = true
        },
        new()
        {
            Id = "impressions",
            Code = "IMPRES",
            Label = "Impressions",
            Icon = "teeth",
            Active = true
        },
        new()
        {
            Id = "integration-check",
            Code = "INTCK",
            Label = "Integration Check",
            Icon = "sync",
            Active = true
        },
        new()
        {
            Id = "biopsy-post-op",
            Code = "BXPOST",
            Label = "Biopsy Post-op",
            Icon = "vial",
            Active = true
        },
        new()
        {
            Id = "implant-removal",
            Code = "IMPRM",
            Label = "Implant Removal",
            Icon = "wrench",
            Active = true
        },
        new()
        {
            Id = "phone-office-consult",
            Code = "PCOC",
            Label = "Phone -> Office Consult",
            Icon = "phone",
            Active = true
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
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
