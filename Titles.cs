using System.Text;

namespace Jellyfin.Plugin.PeanutButterJelly;

public static class Titles
{
    public const string ExplicitMark = " 🅴";
    public const string NoMatchTag = "DeezerNoMatch";

    public static string StripMark(string name)
    {
        if (name.EndsWith(ExplicitMark, StringComparison.Ordinal))
        {
            return name[..^ExplicitMark.Length];
        }

        return name;
    }

    public static bool HasExplicitMark(string name)
        => name.EndsWith(ExplicitMark, StringComparison.Ordinal) || name.EndsWith("🅴", StringComparison.Ordinal);

    public static string DesiredTitle(string name, bool explicitFlag)
    {
        var bas = StripMark(name).Trim();
        if (bas.Length == 0)
        {
            return string.Empty;
        }

        return explicitFlag ? bas + ExplicitMark : bas;
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
