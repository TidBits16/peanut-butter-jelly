using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.PeanutButterJelly;

public static class Genres
{
    private static readonly Dictionary<string, string> PrettyMap;
    private static readonly HashSet<string> SmallWords;
    private static readonly HashSet<string> HyphenHeads;

    private static readonly Dictionary<string, string> Equivalents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["darkwave"] = "dark wave",
        ["coldwave"] = "cold wave",
        ["hiphop"] = "hip hop",
        ["hip hop"] = "hip hop",
        ["lofi"] = "lo-fi",
        ["lo fi"] = "lo-fi",
        ["rnb"] = "r&b",
        ["drum & bass"] = "drum and bass",
        ["d&b"] = "drum and bass",
        ["video game soundtrack"] = "video game music",
        ["game soundtrack"] = "video game music",
        ["kpop"] = "k pop",
        ["jpop"] = "j pop",
        ["neosoul"] = "neo soul",
        ["avantgarde"] = "avant garde",
        ["singersongwriter"] = "singer songwriter",
        ["singer & songwriter"] = "singer songwriter",
        ["rap/hip hop"] = "hip hop",
        ["soul & funk"] = "soul",
        ["films/games"] = "soundtrack"
    };

    static Genres()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.PeanutButterJelly.Resources.genres.json")
            ?? throw new InvalidOperationException("genres.json missing");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        PrettyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("pretty", out var pretty))
        {
            foreach (var p in pretty.EnumerateObject())
            {
                PrettyMap[p.Name] = p.Value.GetString() ?? p.Name;
            }
        }

        SmallWords = [];
        if (root.TryGetProperty("small", out var small))
        {
            foreach (var x in small.EnumerateArray())
            {
                SmallWords.Add(x.GetString() ?? string.Empty);
            }
        }

        HyphenHeads = [];
        if (root.TryGetProperty("hyphen", out var hyphen))
        {
            foreach (var x in hyphen.EnumerateArray())
            {
                HyphenHeads.Add(x.GetString() ?? string.Empty);
            }
        }
    }

    public static string NormKey(string name)
    {
        var s = name.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        while (s.Contains("  ", StringComparison.Ordinal))
        {
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        }

        return Equivalents.TryGetValue(s, out var v) ? v : s;
    }

    public static string Pretty(string name)
    {
        var raw = name.Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var key = NormKey(raw);
        if (PrettyMap.TryGetValue(key, out var mapped))
        {
            return mapped;
        }

        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outWords = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (i > 0 && SmallWords.Contains(word))
            {
                outWords.Add(word);
                continue;
            }

            outWords.Add(word switch
            {
                "uk" or "us" or "tv" or "dj" or "mc" or "dnb" or "edm" or "idm" or "ebm" or "ost" or "vgm" => word.ToUpperInvariant(),
                "r&b" => "R&B",
                _ => char.ToUpperInvariant(word[0]) + word[1..]
            });
        }

        var pretty = string.Join(' ', outWords);
        var parts = pretty.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && HyphenHeads.Contains(parts[0].ToLowerInvariant()))
        {
            pretty = parts[0] + "-" + parts[1];
            if (parts.Length > 2)
            {
                pretty += " " + string.Join(' ', parts.Skip(2));
            }
        }

        return pretty;
    }

    public static List<string> PrettyList(IEnumerable<string> names, int max = 0)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var part in SplitParts(name))
            {
                var p = Pretty(part);
                if (p.Length == 0)
                {
                    continue;
                }

                var k = NormKey(p);
                if (!seen.Add(k))
                {
                    continue;
                }

                output.Add(p);
                if (max > 0 && output.Count >= max)
                {
                    return output;
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Jellyfin wants one genre per array entry. Old tags often packed several into one string with ";".
    /// </summary>
    public static IEnumerable<string> SplitParts(string name)
    {
        var raw = name.Trim();
        if (raw.Length == 0)
        {
            yield break;
        }

        if (raw.IndexOfAny([';', '|']) < 0)
        {
            yield return raw;
            yield break;
        }

        foreach (var part in raw.Split([';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
        }
    }

    public static bool NeedsRewrite(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return false;
        }

        var cleaned = PrettyList(raw, 0);
        if (cleaned.Count != raw.Count)
        {
            return true;
        }

        for (var i = 0; i < raw.Count; i++)
        {
            if (!raw[i].Equals(cleaned[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
