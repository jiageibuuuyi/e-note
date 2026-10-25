using DesktopNotes.Application.Ports;

namespace DesktopNotes.Infrastructure.Persistence;

public sealed partial class SqliteStore : ISettingsRepositoryPort
{
    public string? GetSettingJson(string key)
    {
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value_json FROM settings WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        });
    }

    public void SetSettingJson(string key, string valueJson)
    {
        Write((conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value_json, version)
                VALUES ($key, $val, 1)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    version = settings.version + 1;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$val", valueJson);
            cmd.ExecuteNonQuery();
            return 0;
        });
    }
}
