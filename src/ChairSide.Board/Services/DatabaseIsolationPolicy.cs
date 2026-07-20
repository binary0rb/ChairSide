using System.Security;

namespace ChairSide.Board.Services;

public sealed class DatabaseIsolationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class DatabaseIsolationPolicy(
    DatabaseIsolationLayout layout,
    IReparsePointInspector reparsePointInspector)
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public string ResolveAndValidate(
        string? configuredDatabasePath,
        string? contentRootPath,
        DeploymentEnvironment deploymentEnvironment)
    {
        if (string.IsNullOrWhiteSpace(configuredDatabasePath))
        {
            throw new DatabaseIsolationException("SQLite database path is required.");
        }

        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            throw new DatabaseIsolationException("Application ContentRootPath is required for database isolation.");
        }

        if (deploymentEnvironment.IsDeployed && !Path.IsPathFullyQualified(configuredDatabasePath))
        {
            throw new DatabaseIsolationException(
                $"{deploymentEnvironment.EnvironmentName} SQLite database path must be fully qualified; relative and drive-relative paths are refused.");
        }

        var normalizedContentRoot = NormalizePath(contentRootPath, "application content root");
        var normalizedDatabasePath = NormalizePath(
            deploymentEnvironment.IsDevelopment && !Path.IsPathFullyQualified(configuredDatabasePath)
                ? Path.Combine(normalizedContentRoot, configuredDatabasePath)
                : configuredDatabasePath,
            "SQLite database path");

        var normalizedLayout = NormalizeLayout();

        if (deploymentEnvironment.IsDevelopment)
        {
            RefuseInsideProtectedRoot(normalizedDatabasePath, normalizedLayout.ProductionAppRoot, "Production application root");
            RefuseInsideProtectedRoot(normalizedDatabasePath, normalizedLayout.ProductionDataRoot, "Production data root");
            RefuseInsideProtectedRoot(normalizedDatabasePath, normalizedLayout.TrainingAppRoot, "Training application root");
            RefuseInsideProtectedRoot(normalizedDatabasePath, normalizedLayout.TrainingDataRoot, "Training data root");
            RefuseDirectoryDatabaseLeaf(normalizedDatabasePath);
            return normalizedDatabasePath;
        }

        if (IsPathInside(normalizedDatabasePath, normalizedContentRoot))
        {
            throw new DatabaseIsolationException(
                $"{deploymentEnvironment.EnvironmentName} SQLite database path must be outside the deployed app content root.");
        }

        RefuseInsideProtectedRoot(normalizedDatabasePath, normalizedLayout.ProductionAppRoot, "Production application root");
        RefuseInsideProtectedRoot(normalizedDatabasePath, normalizedLayout.TrainingAppRoot, "Training application root");

        if (deploymentEnvironment.IsProduction)
        {
            RefuseOppositeDeployment(
                normalizedDatabasePath,
                normalizedLayout.TrainingAppRoot,
                normalizedLayout.TrainingDataRoot,
                normalizedLayout.TrainingDatabasePath,
                ChairSideEnvironmentNames.Training);
            RequireExactCanonicalPath(
                normalizedDatabasePath,
                normalizedLayout.ProductionDatabasePath,
                ChairSideEnvironmentNames.Production);
        }
        else
        {
            RefuseOppositeDeployment(
                normalizedDatabasePath,
                normalizedLayout.ProductionAppRoot,
                normalizedLayout.ProductionDataRoot,
                normalizedLayout.ProductionDatabasePath,
                ChairSideEnvironmentNames.Production);
            RequireExactCanonicalPath(
                normalizedDatabasePath,
                normalizedLayout.TrainingDatabasePath,
                ChairSideEnvironmentNames.Training);
        }

        InspectDeployedPathComponents(normalizedDatabasePath, deploymentEnvironment);
        return normalizedDatabasePath;
    }

    public void RescanDeployedPath(
        string normalizedDatabasePath,
        DeploymentEnvironment deploymentEnvironment)
    {
        if (!deploymentEnvironment.IsDeployed)
        {
            return;
        }

        InspectDeployedPathComponents(
            NormalizePath(normalizedDatabasePath, "SQLite database path"),
            deploymentEnvironment);
    }

    private NormalizedDatabaseIsolationLayout NormalizeLayout() => new(
        NormalizePath(layout.ProductionAppRoot, "approved Production application root"),
        NormalizePath(layout.ProductionDataRoot, "approved Production data root"),
        NormalizePath(layout.ProductionDatabasePath, "approved Production database path"),
        NormalizePath(layout.TrainingAppRoot, "approved Training application root"),
        NormalizePath(layout.TrainingDataRoot, "approved Training data root"),
        NormalizePath(layout.TrainingDatabasePath, "approved Training database path"));

    private void InspectDeployedPathComponents(
        string normalizedDatabasePath,
        DeploymentEnvironment deploymentEnvironment)
    {
        FileAttributes? databaseAttributes = null;
        foreach (var component in EnumeratePathComponents(normalizedDatabasePath))
        {
            var attributes = InspectAttributes(component);
            if (attributes.HasValue && attributes.Value.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new DatabaseIsolationException(
                    $"{deploymentEnvironment.EnvironmentName} SQLite database path is refused because existing component '{component}' is a reparse point.");
            }

            if (PathsEqual(component, normalizedDatabasePath))
            {
                databaseAttributes = attributes;
            }
        }

        if (databaseAttributes.HasValue && databaseAttributes.Value.HasFlag(FileAttributes.Directory))
        {
            throw new DatabaseIsolationException(
                $"SQLite database path '{normalizedDatabasePath}' names an existing directory, not a database file.");
        }
    }

    private void RefuseDirectoryDatabaseLeaf(string normalizedDatabasePath)
    {
        var attributes = InspectAttributes(normalizedDatabasePath);
        if (attributes.HasValue && attributes.Value.HasFlag(FileAttributes.Directory))
        {
            throw new DatabaseIsolationException(
                $"SQLite database path '{normalizedDatabasePath}' names an existing directory, not a database file.");
        }
    }

    private FileAttributes? InspectAttributes(string path)
    {
        try
        {
            return reparsePointInspector.GetAttributesIfExists(path);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or SecurityException)
        {
            throw new DatabaseIsolationException(
                $"Unable to verify filesystem metadata for SQLite path component '{path}'. Startup is refused.",
                exception);
        }
    }

    private static IEnumerable<string> EnumeratePathComponents(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new DatabaseIsolationException($"SQLite database path '{path}' has no volume root.");
        }

        var current = root;
        yield return current;

        var relativePath = path[root.Length..];
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static void RefuseInsideProtectedRoot(string path, string protectedRoot, string rootName)
    {
        if (IsPathInside(path, protectedRoot))
        {
            throw new DatabaseIsolationException(
                $"SQLite database path '{path}' is inside the protected {rootName} '{protectedRoot}'.");
        }
    }

    private static void RefuseOppositeDeployment(
        string path,
        string oppositeAppRoot,
        string oppositeDataRoot,
        string oppositeDatabasePath,
        string oppositeEnvironmentName)
    {
        if (IsPathInside(path, oppositeAppRoot)
            || IsPathInside(path, oppositeDataRoot)
            || PathsEqual(path, oppositeDatabasePath))
        {
            throw new DatabaseIsolationException(
                $"SQLite database path '{path}' belongs to the {oppositeEnvironmentName} deployment and is refused.");
        }
    }

    private static void RequireExactCanonicalPath(string path, string canonicalPath, string environmentName)
    {
        if (!PathsEqual(path, canonicalPath))
        {
            throw new DatabaseIsolationException(
                $"{environmentName} SQLite database path must be exactly '{canonicalPath}'. Configured path was '{path}'.");
        }
    }

    private static bool IsPathInside(string path, string root) =>
        PathsEqual(path, root)
        || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static string NormalizePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DatabaseIsolationException($"The {description} is required.");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or SecurityException)
        {
            throw new DatabaseIsolationException(
                $"The {description} '{path}' is malformed or cannot be normalized.",
                exception);
        }
    }

    private sealed record NormalizedDatabaseIsolationLayout(
        string ProductionAppRoot,
        string ProductionDataRoot,
        string ProductionDatabasePath,
        string TrainingAppRoot,
        string TrainingDataRoot,
        string TrainingDatabasePath);
}
