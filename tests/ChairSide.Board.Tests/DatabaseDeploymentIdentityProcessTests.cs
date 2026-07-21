using System.Diagnostics;

using ChairSide.Board.Services;

using Microsoft.Data.Sqlite;

using Xunit.Abstractions;

namespace ChairSide.Board.Tests;

public sealed class DatabaseDeploymentIdentityProcessTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task Wrong_role_process_refusal_exits_two_without_crash_or_diagnostic_log()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "production-marked-copy.db");
        var logDirectory = Path.Combine(workspace.Root, "identity-refusal-logs");
        CreateProductionIdentity(databasePath);
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(databasePath);

        var repositoryRoot = FindRepositoryRoot();
        var projectDirectory = Path.Combine(repositoryRoot, "src", "ChairSide.Board");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine test build configuration.");
        var applicationAssembly = Path.Combine(
            projectDirectory,
            "bin",
            configuration,
            "net8.0",
            "ChairSide.Board.dll");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = projectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(applicationAssembly);
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = ChairSideEnvironmentNames.Development;
        process.StartInfo.Environment["BoardPersistenceOptions__DatabasePath"] = databasePath;
        process.StartInfo.Environment["DiagnosticOptions__LogDirectory"] = logDirectory;

        var stopwatch = Stopwatch.StartNew();
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var forcedKill = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            forcedKill = true;
            process.Kill(entireProcessTree: true);
            throw;
        }

        var combinedOutput = await standardOutput + await standardError;
        stopwatch.Stop();
        testOutput.WriteLine(
            $"Development wrong-role identity refusal: ExitCode={process.ExitCode}; ElapsedMilliseconds={stopwatch.ElapsedMilliseconds}; ForcedKill={forcedKill}");

        Assert.Equal(2, process.ExitCode);
        Assert.False(forcedKill);
        Assert.Contains("[ChairSide Startup] Refused:", combinedOutput, StringComparison.Ordinal);
        Assert.Contains("Development refuses a database carrying the Production deployment identity", combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0xe0434352", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeout", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forced kill", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(logDirectory));
        Assert.Equal(before, File.ReadAllBytes(databasePath));
    }

    private static void CreateProductionIdentity(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = new DatabaseDeploymentIdentityPolicy().CreateIdentity(
            connection,
            transaction,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production));
        transaction.Commit();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChairSide.Board.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate ChairSide.Board.sln from the test output directory.");
    }
}
