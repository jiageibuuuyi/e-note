using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopNotes.Domain;

public static class DisplayName
{
    public static string Create(string? title, string? body)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        var text = Regex.Replace(body ?? "", @"\s+", " ").Trim();
        var e = StringInfo.GetTextElementEnumerator(text);
        var output = new StringBuilder();
        int count = 0;
        while (e.MoveNext())
        {
            if (count++ == 30)
            {
                return output.Append('…').ToString();
            }
            output.Append(e.GetTextElement());
        }

        return output.ToString();
    }
}
