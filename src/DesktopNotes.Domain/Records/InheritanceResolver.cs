namespace DesktopNotes.Domain;

public static class InheritanceResolver
{
    public static EffectiveFields Resolve(
        EffectiveFields own,
        EffectiveFields? parent,
        InheritField mask)
    {
        if (parent is null)
        {
            if (mask != InheritField.None)
            {
                throw new ArgumentException("root_cannot_inherit", nameof(mask));
            }
            return own;
        }

        bool Follow(InheritField f) => (mask & f) != 0;
        return new(
            Follow(InheritField.Publisher) ? parent.Publisher : own.Publisher,
            Follow(InheritField.Assignee) ? parent.Assignee : own.Assignee,
            Follow(InheritField.Priority) ? parent.Priority : own.Priority,
            Follow(InheritField.Start) ? parent.Start : own.Start,
            Follow(InheritField.Due) ? parent.Due : own.Due,
            Follow(InheritField.AttachmentRoot) ? parent.AttachmentRootId : own.AttachmentRootId);
    }
}
