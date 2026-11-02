using System.Text;

namespace DesktopNotes.Domain;

public static class RecordRules
{
    public static bool HasValidText(string? title, string? body)
    {
        if (!IsWellFormedText(title) || !IsWellFormedText(body))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body);
    }

    public static bool IsWellFormedText(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.IndexOf('\0') >= 0)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidDueSpec(DueSpec due)
    {
        return due.Kind switch
        {
            DueKind.None => due.Date is null && due.Instant is null && due.ZoneId is null,
            DueKind.Date => due.Date is not null && due.Instant is null && due.ZoneId is null,
            DueKind.Instant => due.Date is null && due.Instant is not null && !string.IsNullOrWhiteSpace(due.ZoneId),
            _ => false
        };
    }

    public static bool IsValidHierarchy(
        Guid id,
        RecordKind kind,
        Guid? parentId,
        InheritField mask,
        RecordKind? parentKind,
        Guid? parentParentId,
        RecordStatus? parentStatus,
        bool parentDeleted)
    {
        if (parentId is null)
        {
            return mask == InheritField.None;
        }

        if (kind != RecordKind.Task || parentId.Value == id)
        {
            return false;
        }

        if (((int)mask & ~(int)InheritField.All) != 0)
        {
            return false;
        }

        if (parentKind != RecordKind.Task)
        {
            return false;
        }

        if (parentStatus != RecordStatus.Active || parentDeleted)
        {
            return false;
        }

        return true;
    }
}
