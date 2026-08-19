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
}
