using Microsoft.Data.Sqlite;

namespace ChairSide.Board.Tests;

public sealed class TestWorkspaceCleanupTests
{
    [Fact]
    public void Dispose_removes_workspace_after_a_pooled_sqlite_connection_is_closed()
    {
        var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "pooled-cleanup.db");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 5
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE cleanup_probe (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        Assert.True(Directory.Exists(workspace.Root));

        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.Root));
    }

    [Fact]
    public void Dispose_is_idempotent_after_successful_cleanup()
    {
        var workspace = TestWorkspace.Create();

        workspace.Dispose();
        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.Root));
    }

    [Fact]
    public void Dispose_surfaces_a_persistent_windows_file_lock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = TestWorkspace.Create();
        var lockedPath = Path.Combine(workspace.DataRoot, "locked.txt");
        File.WriteAllText(lockedPath, "locked");

        using var lockStream = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        try
        {
            var exception = Assert.Throws<IOException>(() => workspace.Dispose());

            Assert.Contains("Unable to clean ChairSide test workspace", exception.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(workspace.Root));
        }
        finally
        {
            lockStream.Dispose();
            workspace.Dispose();
        }
    }
}
