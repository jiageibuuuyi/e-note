using Microsoft.Data.Sqlite;

namespace DesktopNotes.Infrastructure.Persistence;

public static class MigrationRunner
{
    public static void Initialize(string connectionString, string? initialSqlPath = null)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            pragmaCmd.ExecuteNonQuery();
        }

        if (IsVersionApplied(connection, 1))
        {
            return;
        }

        string sqlPath = ResolveInitialSqlPath(initialSqlPath);
        string sql = File.ReadAllText(sqlPath);

        using var migrateCmd = connection.CreateCommand();
        migrateCmd.CommandText = sql;
        migrateCmd.ExecuteNonQuery();
    }

    private static bool IsVersionApplied(SqliteConnection connection, int version)
    {
        using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
        long tableExists = (long)(tableCheck.ExecuteScalar() ?? 0L);
        if (tableExists == 0)
        {
            return false;
        }

        using var versionCheck = connection.CreateCommand();
        versionCheck.CommandText = "SELECT count(*) FROM schema_migrations WHERE version = $v;";
        versionCheck.Parameters.AddWithValue("$v", version);
        long count = (long)(versionCheck.ExecuteScalar() ?? 0L);
        return count > 0;
    }

    public static string ResolveInitialSqlPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "001_initial.sql"),
            Path.Combine(baseDir, "Persistence", "Migrations", "001_initial.sql"),
            Path.Combine(Directory.GetCurrentDirectory(), "docs", "database", "001_initial.sql"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "DesktopNotes.Infrastructure", "Persistence", "Migrations", "001_initial.sql")
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Cannot locate 001_initial.sql migration file.");
    }
}
