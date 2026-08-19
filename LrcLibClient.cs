using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.PeanutButterJelly;

public sealed class LrcMatch
{
    public string Synced { get; init; } = string.Empty;

    public string Plain { get; init; } = string.Empty;

    public bool Instrumental { get; init; }

    public string Source { get; init; } = "no-match";

    public string TrackName { get; init; } = string.Empty;

    public bool Usable => Instrumental || Synced.Length > 0 || Plain.Length > 0;
}

public class LrcLibClient
{
    private const string Base = "https://lrclib.net";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly Regex SyncRe = new(@"\[\d{1,2}:\d{2}", RegexOptions.Compiled);
    private static readonly Regex ParenTail = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex Bracket = new(@"^\[.*\]$", RegexOptions.Compiled);
    private readonly PacedHttp _http;
    private readonly HttpCache _cache;

    public LrcLibClient(IHttpClientFactory factory, HttpCache cache)
    {
        _cache = cache;
        _http = new PacedHttp(factory, cache, TimeSpan.FromMilliseconds(250));
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    public async Task<LrcMatch> LookupArtistsAsync(string title, IReadOnlyList<string> artists, string album, int? duration, CancellationToken cancellationToken)
    {
        if (artists.Count == 0)
        {
            return new LrcMatch();
        }

        var m = await LookupAsync(title, artists[0], album, duration, cancellationToken).ConfigureAwait(false);
        if (m.Synced.Length > 0 || m.Plain.Length > 0 || m.Instrumental)
        {
            return m;
        }

        foreach (var a in artists.Skip(1))
        {
            var cand = await LookupAsync(title, a, album, duration, cancellationToken).ConfigureAwait(false);
            if (cand.Synced.Length > 0 || cand.Plain.Length > 0 || cand.Instrumental)
            {
                return cand;
            }
        }

        return m;
    }

    public async Task<LrcMatch> LookupAsync(string title, string artist, string album, int? duration, CancellationToken cancellationToken)
    {
        title = Titles.StripMark(title).Trim();
        artist = artist.Trim();
        album = Titles.StripMark(album).Trim();
        if (title.Length == 0 || artist.Length == 0)
        {
            return new LrcMatch();
        }

        var cp = $"lrclib/match/v2|{title.ToLowerInvariant()}|{artist.ToLowerInvariant()}|{album.ToLowerInvariant()}|{duration}";
        if (_cache.TryGet(cp, Ttl, out var cached))
        {
            if (JsonUtil.Bool(cached, "_miss") == true)
            {
                return new LrcMatch();
            }

            return new LrcMatch
            {
                Synced = Clean(JsonUtil.Str(cached, "synced")),
                Plain = Clean(JsonUtil.Str(cached, "plain")),
                Instrumental = JsonUtil.Bool(cached, "instrumental") == true,
                Source = JsonUtil.Str(cached, "source"),
                TrackName = JsonUtil.Str(cached, "track_name")
            };
        }

        var titlesV = TitleVariants(title);
        LrcMatch? fallback = null;
        LrcMatch? Take(LrcMatch m)
        {
            if (m.Synced.Length > 0)
            {
                return m;
            }

            if (m.Instrumental && m.Plain.Length == 0 && m.Source.StartsWith("get", StringComparison.Ordinal))
            {
                return m;
            }

            if (m.Plain.Length > 0 && fallback is null)
            {
                fallback = m;
            }

            return null;
        }

        async Task<LrcMatch?> TryGet(string name, bool withAlbum, string source)
        {
            var parameters = new Dictionary<string, string>
            {
                ["track_name"] = name,
                ["artist_name"] = artist
            };
            if (withAlbum && album.Length > 0)
            {
                parameters["album_name"] = album;
            }

            if (duration is { } d)
            {
                parameters["duration"] = d.ToString();
            }

            var payload = await GetAsync("api/get", parameters, cancellationToken).ConfigureAwait(false);
            if (payload is null || JsonUtil.Bool(payload.Value, "_miss") == true)
            {
                return null;
            }

            return Take(FromPayload(payload.Value, source + ":" + JsonUtil.Str(payload.Value, "id")));
        }

        void Store(LrcMatch m)
        {
            if (m.Source == "no-match")
            {
                _cache.SetObject(cp, new Dictionary<string, object> { ["_miss"] = true });
                return;
            }

            _cache.SetObject(cp, new Dictionary<string, object?>
            {
                ["synced"] = m.Synced,
                ["plain"] = m.Plain,
                ["instrumental"] = m.Instrumental,
                ["source"] = m.Source,
                ["track_name"] = m.TrackName
            });
        }

        if (await TryGet(titlesV[0], true, "get").ConfigureAwait(false) is { } done)
        {
            Store(done);
            return done;
        }

        if (album.Length > 0 && await TryGet(titlesV[0], false, "get-noalbum").ConfigureAwait(false) is { } done2)
        {
            Store(done2);
            return done2;
        }

        var search = await GetAsync("api/search", new Dictionary<string, string>
        {
            ["track_name"] = titlesV[0],
            ["artist_name"] = artist
        }, cancellationToken).ConfigureAwait(false);
        if (search is { } payload)
        {
            var results = payload.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray()
                : JsonUtil.Arr(payload, "data");
            LrcMatch? best = null;
            var bestScore = -1.0;
            foreach (var raw in results)
            {
                if (duration is { } want && !DurationOk(raw, want))
                {
                    continue;
                }

                var match = FromPayload(raw, "search:" + JsonUtil.Str(raw, "id"));
                if (!match.Usable)
                {
                    continue;
                }

                var score = match.Synced.Length > 0 ? 3.0 : 1.0;
                if (duration is { } w)
                {
                    var got = JsonUtil.Num(raw, "duration");
                    score -= Math.Abs(got - w) / 10.0;
                }

                if (score > bestScore)
                {
                    best = match;
                    bestScore = score;
                }
            }

            if (best is not null && Take(best) is { } taken)
            {
                Store(taken);
                return taken;
            }
        }

        foreach (var name in titlesV.Skip(1))
        {
            if (await TryGet(name, true, "get").ConfigureAwait(false) is { } d3)
            {
                Store(d3);
                return d3;
            }

            if (album.Length > 0 && await TryGet(name, false, "get-noalbum").ConfigureAwait(false) is { } d4)
            {
                Store(d4);
                return d4;
            }
        }

        if (fallback is not null)
        {
            Store(fallback);
            return fallback;
        }

        Store(new LrcMatch());
        return new LrcMatch();
    }

    public static List<string> QueryArtists(Audio item, IEnumerable<string>? extra)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            var text = (v ?? string.Empty).Trim();
            if (text.Length == 0 || Bracket.IsMatch(text))
            {
                return;
            }

            foreach (var cand in new[] { text, text.Replace('‐', '-').Replace('‑', '-').Replace('‒', '-').Replace('–', '-').Replace('—', '-') })
            {
                if (seen.Add(cand))
                {
                    names.Add(cand);
                }
            }
        }

        if (extra is not null)
        {
            foreach (var n in extra)
            {
                Add(n);
            }
        }

        foreach (var n in item.Artists)
        {
            Add(n);
        }

        Add(item.AlbumArtists.FirstOrDefault());
        return names;
    }

    private async Task<JsonElement?> GetAsync(string path, Dictionary<string, string> parameters, CancellationToken cancellationToken)
        => await _http.GetJsonAsync("lrclib/" + path, Base + "/" + path, parameters, Ttl, cancellationToken).ConfigureAwait(false);

    private static LrcMatch FromPayload(JsonElement p, string source)
    {
        var synced = Clean(JsonUtil.Str(p, "syncedLyrics"));
        var plain = Clean(JsonUtil.Str(p, "plainLyrics"));
        if (synced.Length > 0 && !SyncRe.IsMatch(synced))
        {
            if (plain.Length == 0)
            {
                plain = synced;
            }

            synced = string.Empty;
        }

        var name = JsonUtil.Str(p, "trackName");
        if (name.Length == 0)
        {
            name = JsonUtil.Str(p, "name");
        }

        return new LrcMatch
        {
            Synced = synced,
            Plain = plain,
            Instrumental = JsonUtil.Bool(p, "instrumental") == true,
            Source = source,
            TrackName = name
        };
    }

    private static string Clean(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static List<string> TitleVariants(string title)
    {
        var cur = Titles.StripMark(title).Trim();
        var outList = new List<string>();
        while (cur.Length > 0)
        {
            if (!outList.Contains(cur, StringComparer.Ordinal))
            {
                outList.Add(cur);
            }

            var nxt = ParenTail.Replace(cur, string.Empty).Trim();
            if (nxt == cur)
            {
                break;
            }

            cur = nxt;
        }

        return outList.Count > 0 ? outList : [title];
    }

    private static bool DurationOk(JsonElement got, int want)
    {
        var n = JsonUtil.Num(got, "duration");
        return Math.Abs(n - want) <= 2;
    }
}
