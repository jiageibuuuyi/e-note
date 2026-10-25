using DesktopNotes.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DesktopNotes.Integration.Tests;

public class SqliteSchemaTests
{
    [Fact]
    public void Schema_initializes_and_literal_symbols_are_not_wildcards()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "001_initial.sql"));
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
        command.CommandText = "SELECT instr('50%_合同',$q)";
        command.Parameters.AddWithValue("$q", "%_");
        Assert.Equal(3L, command.ExecuteScalar());
    }

    [Fact]
    public void MigrationRunner_is_idempotent_across_repeated_initializations()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "desktopnotes-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "app.db");

        try
        {
            using (var store1 = new SqliteStore(dbPath))
            using (var store2 = new SqliteStore(dbPath))
            {
                long version = store2.Read(conn =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT version FROM schema_migrations;";
                    return (long)(cmd.ExecuteScalar() ?? 0L);
                });
                Assert.Equal(1L, version);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
