using System.Text;

namespace Jellyfin.Plugin.PeanutButterJelly;

public static class Titles
{
    public const string ExplicitMark = "[E]";
    public const string NoMatchTag = "DeezerNoMatch";

    public static string Affix { get; set; } = ExplicitMark;

    public static bool PrependMark { get; set; }

    public static void UseStyle(string? affix, bool prepend)
    {
        var text = (affix ?? string.Empty).Trim();
        Affix = text.Length > 0 ? text : ExplicitMark;
        PrependMark = prepend;
    }

    public static void ResetStyle()
    {
        Affix = ExplicitMark;
        PrependMark = false;
    }

    public static string StripMark(string name)
    {
        var s = name.Trim();
        s = StripToken(s, Affix);
        s = StripToken(s, ExplicitMark);
        // Older default mark (squared E).
        s = StripToken(s, "🅴");
        return s.Trim();
    }

    private static string StripToken(string name, string token)
    {
        var mark = token.Trim();
        if (mark.Length == 0)
        {
            return name;
        }

        var s = name;
        // With or without a surrounding space (users used to bake the space into the mark field).
        foreach (var edge in new[] { mark, mark + " ", " " + mark })
        {
            if (s.StartsWith(edge, StringComparison.Ordinal))
            {
                s = s[edge.Length..].TrimStart();
                break;
            }
        }

        foreach (var edge in new[] { mark, " " + mark, mark + " " })
        {
            if (s.EndsWith(edge, StringComparison.Ordinal))
            {
                s = s[..^edge.Length].TrimEnd();
                break;
            }
        }

        return s;
    }

    public static bool HasExplicitMark(string name)
        => !string.Equals(name.Trim(), StripMark(name), StringComparison.Ordinal);

    public static string DesiredTitle(string name, bool explicitFlag)
    {
        var bas = StripMark(name);
        if (bas.Length == 0)
        {
            return string.Empty;
        }

        if (!explicitFlag)
        {
            return bas;
        }

        var mark = Affix.Trim();
        if (mark.Length == 0)
        {
            mark = ExplicitMark;
        }

        // Always insert exactly one space; do not require it in the config field.
        return PrependMark ? mark + " " + bas : bas + " " + mark;
    }

    public static string Norm(string text)
    {
        var s = StripMark(text).ToLowerInvariant();
        var b = new StringBuilder();
        var prevSpace = false;
        foreach (var r in s)
        {
            if ((r is >= 'a' and <= 'z') || (r is >= '0' and <= '9') || r == '&' || r == ' ')
            {
                if (r == ' ')
                {
                    if (prevSpace)
                    {
                        continue;
                    }

                    prevSpace = true;
                }
                else
                {
                    prevSpace = false;
                }

                b.Append(r);
                continue;
            }

            if (!prevSpace)
            {
                b.Append(' ');
                prevSpace = true;
            }
        }

        return b.ToString().Trim();
    }

    public static List<string> DistinctNames(IEnumerable<string> names)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var text = name.Trim();
            if (text.Length == 0 || !seen.Add(text))
            {
                continue;
            }

            output.Add(text);
        }

        return output;
    }

    public static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
