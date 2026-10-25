namespace DesktopNotes.Application.Ports;

public interface ISettingsRepositoryPort
{
    string? GetSettingJson(string key);
    void SetSettingJson(string key, string valueJson);
}
