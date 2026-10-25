namespace DesktopNotes.Domain;

public static class LiteralSearch
{
    public static bool Matches(string? query, params string?[] fields)
    {
        var q = (query ?? "").Trim();
        return q.Length == 0 || fields.Any(x =>
            x?.Contains(q, StringComparison.Ordinal) == true);
    }
}
