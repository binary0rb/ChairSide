using Microsoft.Extensions.Options;

using ChairSide.Board.Services;

namespace ChairSide.Board.Options;

public sealed class AdminAccessOptions
{
    public const string SectionName = "AdminAccessOptions";

    public bool Enabled { get; set; }

    public string SharedToken { get; set; } = "";
}

public sealed class AdminAccessOptionsValidator(DeploymentEnvironment deploymentEnvironment)
    : IValidateOptions<AdminAccessOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminAccessOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.SharedToken))
        {
            failures.Add("AdminAccessOptions:SharedToken is required when admin access protection is enabled.");
        }

        if (deploymentEnvironment.IsDeployed
            && string.Equals(options.SharedToken, "dev-admin-token", StringComparison.Ordinal))
        {
            failures.Add(
                $"AdminAccessOptions:SharedToken must not use the dev-admin-token sample value in {deploymentEnvironment.EnvironmentName}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
