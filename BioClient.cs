using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PeanutButterJelly;

public sealed class BioMatch
{
    public string Overview { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;
}

public class BioClient
{
    private const string AudioDb = "https://www.theaudiodb.com/api/v1/json/2";
    private const string WikiApi = "https://en.wikipedia.org/w/api.php";
    private const string WikiSummary = "https://en.wikipedia.org/api/rest_v1/page/summary";
    private const string Wikidata = "https://www.wikidata.org/w/api.php";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly Regex HtmlRe = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ParenRe = new(@"\s*\([^)]*\)\s*", RegexOptions.Compiled);
    private static readonly Regex MusicRe = new(
        @"\b(rapper|singer|songwriter|musician|vocalist|band|dj|composer|record producer|music producer|hip[\s-]?hop|folk[\s-]?punk|indie pop|folk-pop|pop (?:band|group|duo|trio|artist)|rock (?:band|group)|multi-instrumentalist|musical (?:artist|group|duo|trio)|recording artist|guitarist|drummer|bassist|pianist|youtuber|ensemble|orchestra|choir|mc)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SkipRe = new(
        @"\b(discography|filmography|list of|politician)\b|\((album|song|ep|single|soundtrack)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PacedHttp _http;
    private readonly HttpCache _cache;

    public BioClient(IHttpClientFactory factory, HttpCache cache)
    {
        _cache = cache;
        _http = new PacedHttp(factory, cache, TimeSpan.FromMilliseconds(200));
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    public async Task<BioMatch> LookupAsync(string name, CancellationToken cancellationToken)
    {
        var want = name.Trim();
        if (want.Length == 0)
        {
            return new BioMatch();
        }

        var key = "bio/v3|" + want.ToLowerInvariant();
        if (_cache.TryGet(key, Ttl, out var cached))
        {
            if (JsonUtil.Bool(cached, "_miss") == true)
            {
                return new BioMatch();
            }

            return new BioMatch { Overview = JsonUtil.Str(cached, "overview"), Source = JsonUtil.Str(cached, "source") };
        }

        var queries = new List<string> { want };
        var stripped = Titles.Norm(want);
        if (stripped.Length > 0 && !stripped.Equals(want, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add(stripped);
        }

        BioMatch? match = null;
        foreach (var q in queries)
        {
            var payload = await _http.GetJsonAsync("audiodb/search", AudioDb + "/search.php", new Dictionary<string, string> { ["s"] = q }, Ttl, cancellationToken).ConfigureAwait(false);
            if (payload is { } p && PickAudioDb(want, p) is { } m)
            {
                match = m;
                break;
            }
        }

        if (match is null)
        {
            match = await FromWikipediaAsync(want, cancellationToken).ConfigureAwait(false)
                ?? await FromWikidataAsync(want, cancellationToken).ConfigureAwait(false);
        }

        if (match is { Overview.Length: > 0 })
        {
            _cache.SetObject(key, new Dictionary<string, object> { ["overview"] = match.Overview, ["source"] = match.Source });
            return match;
        }

        _cache.SetObject(key, new Dictionary<string, object> { ["_miss"] = true });
        return new BioMatch();
    }

    private static BioMatch? PickAudioDb(string name, JsonElement payload)
    {
        BioMatch? best = null;
        var bestScore = 0.0;
        foreach (var raw in JsonUtil.Arr(payload, "artists"))
        {
            var got = JsonUtil.Str(raw, "strArtist").Trim();
            var score = NameScore(name, got);
            if (score < 0.72 && !Compact(got).Contains(Compact(name), StringComparison.Ordinal))
            {
                continue;
            }

            var bio = CleanBio(FirstNonEmpty(raw, "strBiography", "strBiographyEN", "strBiographyDE", "strBiographyFR"), 4000);
            if (bio.Length == 0)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = new BioMatch { Overview = bio, Source = "audiodb:" + (got.Length > 0 ? got : name) };
            }
        }

        return best;
    }

    private async Task<BioMatch?> FromWikipediaAsync(string name, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in new[] { name, name + " (musician OR singer OR band OR rapper)" })
        {
            var titles = new List<string>();
            foreach (var t in await WikiSearchAsync(q, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(t))
                {
                    titles.Add(t);
                }
            }

            if (await FromTitlesAsync(name, titles, cancellationToken).ConfigureAwait(false) is { } m)
            {
                return m;
            }
        }

        return null;
    }

    private async Task<List<string>> WikiSearchAsync(string query, CancellationToken cancellationToken)
    {
        var payload = await _http.GetJsonAsync(
            "wikipedia/search",
            WikiApi,
            new Dictionary<string, string>
            {
                ["action"] = "query",
                ["list"] = "search",
                ["srsearch"] = query,
                ["srlimit"] = "8",
                ["srprop"] = string.Empty,
                ["format"] = "json"
            },
            Ttl,
            cancellationToken).ConfigureAwait(false);
        var titles = new List<string>();
        if (payload is null)
        {
            return titles;
        }

        var q = JsonUtil.Obj(payload.Value, "query");
        if (q is null)
        {
            return titles;
        }

        foreach (var hit in JsonUtil.Arr(q.Value, "search"))
        {
            var t = JsonUtil.Str(hit, "title").Trim();
            if (t.Length > 0)
            {
                titles.Add(t);
            }
        }

        return titles;
    }

    private async Task<BioMatch?> FromTitlesAsync(string name, IEnumerable<string> list, CancellationToken cancellationToken)
    {
        BioMatch? best = null;
        var bestNs = -1.0;
        var bestMusic = -1;
        foreach (var title in list)
        {
            if (SkipRe.IsMatch(title))
            {
                continue;
            }

            var ns = NameScore(name, title);
            if (ns < 0.72)
            {
                continue;
            }

            var payload = await WikiSummaryAsync(title, cancellationToken).ConfigureAwait(false);
            if (payload is null || JsonUtil.Str(payload.Value, "type") == "disambiguation")
            {
                continue;
            }

            var extract = CleanBio(JsonUtil.Str(payload.Value, "extract"), 4000);
            var desc = JsonUtil.Str(payload.Value, "description");
            if (extract.Length == 0)
            {
                continue;
            }

            if (!MusicRe.IsMatch(desc + " " + Clip(extract, 500)))
            {
                continue;
            }

            var md = MusicRe.IsMatch(desc) ? 1 : 0;
            if (ns > bestNs || (Math.Abs(ns - bestNs) < 0.0001 && md > bestMusic))
            {
                bestNs = ns;
                bestMusic = md;
                best = new BioMatch { Overview = extract, Source = "wikipedia:" + title };
                if (ns >= 0.98 && md == 1)
                {
                    break;
                }
            }
        }

        return best;
    }

    private async Task<JsonElement?> WikiSummaryAsync(string title, CancellationToken cancellationToken)
    {
        var slug = Uri.EscapeDataString(title.Replace(' ', '_'));
        return await _http.GetJsonAsync("wikipedia/summary/" + title, WikiSummary + "/" + slug, null, Ttl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BioMatch?> FromWikidataAsync(string name, CancellationToken cancellationToken)
    {
        var payload = await _http.GetJsonAsync(
            "wikidata/search",
            Wikidata,
            new Dictionary<string, string>
            {
                ["action"] = "wbsearchentities",
                ["search"] = name,
                ["language"] = "en",
                ["type"] = "item",
                ["limit"] = "5",
                ["format"] = "json"
            },
            Ttl,
            cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        foreach (var hit in JsonUtil.Arr(payload.Value, "search"))
        {
            var label = JsonUtil.Str(hit, "label");
            var desc = JsonUtil.Str(hit, "description");
            if (NameScore(name, label) < 0.72 && Compact(name) != Compact(label))
            {
                continue;
            }

            if (desc.Length > 0 && !MusicRe.IsMatch(desc))
            {
                continue;
            }

            var qid = JsonUtil.Str(hit, "id");
            if (qid.Length == 0)
            {
                continue;
            }

            var entP = await _http.GetJsonAsync(
                "wikidata/entity/" + qid,
                Wikidata,
                new Dictionary<string, string>
                {
                    ["action"] = "wbgetentities",
                    ["ids"] = qid,
                    ["props"] = "sitelinks|descriptions",
                    ["languages"] = "en",
                    ["sitefilter"] = "enwiki",
                    ["format"] = "json"
                },
                Ttl,
                cancellationToken).ConfigureAwait(false);
            if (entP is null)
            {
                continue;
            }

            var entities = JsonUtil.Obj(entP.Value, "entities");
            var ent = entities is null ? null : JsonUtil.Obj(entities.Value, qid);
            var sitelinks = ent is null ? null : JsonUtil.Obj(ent.Value, "sitelinks");
            var enwiki = sitelinks is null ? null : JsonUtil.Obj(sitelinks.Value, "enwiki");
            var title = enwiki is null ? string.Empty : JsonUtil.Str(enwiki.Value, "title").Trim();
            if (title.Length > 0)
            {
                if (await FromTitlesAsync(name, [title], cancellationToken).ConfigureAwait(false) is { } m)
                {
                    return m;
                }
            }

            var descriptions = ent is null ? null : JsonUtil.Obj(ent.Value, "descriptions");
            var en = descriptions is null ? null : JsonUtil.Obj(descriptions.Value, "en");
            var d = en is null ? string.Empty : JsonUtil.Str(en.Value, "value").Trim();
            if (d.Length == 0)
            {
                d = desc;
            }

            if (d.Length > 0 && MusicRe.IsMatch(d))
            {
                return new BioMatch { Overview = char.ToUpperInvariant(d[0]) + d[1..] + ".", Source = "wikidata:" + qid };
            }
        }

        return null;
    }

    private static string FirstNonEmpty(JsonElement m, params string[] keys)
    {
        foreach (var k in keys)
        {
            var s = JsonUtil.Str(m, k).Trim();
            if (s.Length > 0)
            {
                return s;
            }
        }

        return string.Empty;
    }

    private static string CleanBio(string text, int limit)
    {
        var value = HtmlRe.Replace(WebUtility.HtmlDecode(text), " ").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (value.Length > limit)
        {
            var cut = value[..limit];
            var i = cut.LastIndexOf('\n');
            if (i > 0)
            {
                cut = cut[..i];
            }

            value = cut.Trim();
        }

        return value;
    }

    private static string Compact(string s) => new string(Titles.Norm(s).Where(char.IsLetterOrDigit).ToArray());

    private static double NameScore(string want, string got)
    {
        var a = Titles.Norm(want);
        var b = Titles.Norm(ParenRe.Replace(got, " "));
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        var score = Similarity.Ratio(a, b);
        if (Compact(a) == Compact(b) && score < 0.98)
        {
            score = 0.98;
        }

        return score;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
}
