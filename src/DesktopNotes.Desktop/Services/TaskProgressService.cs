using System.Text.Json;
using DesktopNotes.Application.Contracts;
using DesktopNotes.Application.Ports;
using DesktopNotes.Domain;

namespace DesktopNotes.Desktop.Services;

public sealed class TaskProgressService
{
    private const string SettingKey = "task.custom_progress_map";
    private readonly ISettingsRepositoryPort? _repository;
    private readonly Dictionary<Guid, int> _customProgress = new();
    private bool _loaded;
    private readonly object _lock = new();

    private static TaskProgressService? s_instance;
    public static TaskProgressService Instance
    {
        get => s_instance ??= new TaskProgressService(null);
        set => s_instance = value;
    }

    public event Action<Guid, int?>? ProgressChanged;

    public TaskProgressService(ISettingsRepositoryPort? repository)
    {
        _repository = repository;
    }

    public void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded || _repository == null) return;
            try
            {
                string? json = _repository.GetSettingJson(SettingKey);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (map is not null)
                    {
                        _customProgress.Clear();
                        foreach (var kvp in map)
                        {
                            if (Guid.TryParse(kvp.Key, out var id))
                            {
                                _customProgress[id] = Math.Clamp(kvp.Value, 0, 100);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed persisted state
            }
            _loaded = true;
        }
    }

    public int? GetCustomProgress(Guid taskId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _customProgress.TryGetValue(taskId, out int p) ? p : null;
        }
    }

    public void SetCustomProgress(Guid taskId, int progress)
    {
        EnsureLoaded();
        int clamped = Math.Clamp(progress, 0, 100);
        lock (_lock)
        {
            _customProgress[taskId] = clamped;
            Persist();
        }
        ProgressChanged?.Invoke(taskId, clamped);
    }

    public void ClearCustomProgress(Guid taskId)
    {
        EnsureLoaded();
        bool removed;
        lock (_lock)
        {
            removed = _customProgress.Remove(taskId);
            if (removed)
            {
                Persist();
            }
        }
        if (removed)
        {
            ProgressChanged?.Invoke(taskId, null);
        }
    }

    private void Persist()
    {
        if (_repository == null) return;
        try
        {
            var dict = _customProgress.ToDictionary(kvp => kvp.Key.ToString("D"), kvp => kvp.Value);
            string json = JsonSerializer.Serialize(dict);
            _repository.SetSettingJson(SettingKey, json);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Computes effective progress for a record given its subtasks.
    /// </summary>
    public (int Progress, bool IsCustom, bool HasSubtasks) ComputeProgress(
        RecordView record,
        IReadOnlyList<RecordView> subtasks,
        Func<Guid, bool>? isMarkedCompleted = null)
    {
        EnsureLoaded();

        if (record.Status == RecordStatus.Closed || (isMarkedCompleted != null && isMarkedCompleted(record.Id)))
        {
            return (100, false, subtasks.Count > 0);
        }

        int? custom = GetCustomProgress(record.Id);
        if (custom.HasValue)
        {
            return (custom.Value, true, subtasks.Count > 0);
        }

        if (subtasks.Count > 0)
        {
            int total = 0;
            foreach (var sub in subtasks)
            {
                if (sub.Status == RecordStatus.Closed || (isMarkedCompleted != null && isMarkedCompleted(sub.Id)))
                {
                    total += 100;
                }
                else
                {
                    int? subCustom = GetCustomProgress(sub.Id);
                    total += subCustom ?? 0;
                }
            }
            int avg = (int)Math.Round((double)total / subtasks.Count);
            return (Math.Clamp(avg, 0, 100), false, true);
        }

        return (0, false, false);
    }

    /// <summary>
    /// Computes recursive progress across multi-level subtask hierarchies.
    /// </summary>
    public int ComputeRecursiveProgress(
        RecordView record,
        IReadOnlyDictionary<Guid, List<RecordView>> childrenByParent,
        Func<Guid, bool>? isMarkedCompleted,
        out bool isCustom)
    {
        EnsureLoaded();

        if (record.Status == RecordStatus.Closed || (isMarkedCompleted != null && isMarkedCompleted(record.Id)))
        {
            isCustom = false;
            return 100;
        }

        int? custom = GetCustomProgress(record.Id);
        if (custom.HasValue)
        {
            isCustom = true;
            return custom.Value;
        }

        isCustom = false;
        if (childrenByParent.TryGetValue(record.Id, out var children) && children.Count > 0)
        {
            int sum = 0;
            foreach (var child in children)
            {
                sum += ComputeRecursiveProgress(child, childrenByParent, isMarkedCompleted, out _);
            }
            return Math.Clamp((int)Math.Round((double)sum / children.Count), 0, 100);
        }

        return 0;
    }
}
