namespace ChairSide.Board.Services;

public static class ChairSideEnvironmentNames
{
    public const string Development = "Development";
    public const string Training = "Training";
    public const string Production = "Production";
}

public enum DeploymentRole
{
    Development,
    Training,
    Production
}

public sealed record DeploymentEnvironment(DeploymentRole Role, string EnvironmentName)
{
    public bool IsDevelopment => Role == DeploymentRole.Development;

    public bool IsTraining => Role == DeploymentRole.Training;

    public bool IsProduction => Role == DeploymentRole.Production;

    public bool IsDeployed => Role is DeploymentRole.Training or DeploymentRole.Production;
}

public sealed class DeploymentEnvironmentException(string message) : InvalidOperationException(message);

public static class DeploymentEnvironmentPolicy
{
    public static DeploymentEnvironment Resolve(string? environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName)
            || !string.Equals(environmentName, environmentName.Trim(), StringComparison.Ordinal))
        {
            throw UnknownEnvironment(environmentName);
        }

        if (string.Equals(environmentName, ChairSideEnvironmentNames.Development, StringComparison.OrdinalIgnoreCase))
        {
            return new DeploymentEnvironment(DeploymentRole.Development, ChairSideEnvironmentNames.Development);
        }

        if (string.Equals(environmentName, ChairSideEnvironmentNames.Training, StringComparison.OrdinalIgnoreCase))
        {
            return new DeploymentEnvironment(DeploymentRole.Training, ChairSideEnvironmentNames.Training);
        }

        if (string.Equals(environmentName, ChairSideEnvironmentNames.Production, StringComparison.OrdinalIgnoreCase))
        {
            return new DeploymentEnvironment(DeploymentRole.Production, ChairSideEnvironmentNames.Production);
        }

        throw UnknownEnvironment(environmentName);
    }

    private static DeploymentEnvironmentException UnknownEnvironment(string? environmentName)
    {
        var displayName = environmentName is null ? "<null>" : $"'{environmentName}'";
        return new DeploymentEnvironmentException(
            $"ChairSide environment {displayName} is not recognized. Expected Development, Training, or Production.");
    }
}
