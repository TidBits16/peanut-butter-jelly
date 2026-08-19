using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButterJelly;

public sealed class DeezerTrack
{
    public string Title { get; init; } = string.Empty;

    public bool? Explicit { get; init; }

    public List<string> Artists { get; init; } = [];
}

public sealed class DeezerArtistInfo
{
    public string Name { get; init; } = string.Empty;

    public int ArtistId { get; init; }

    public string Picture { get; init; } = string.Empty;
}

public sealed class DeezerAlbumMatch
{
    public List<string> Genres { get; init; } = [];

    public string Source { get; init; } = "no-match";

    public int AlbumId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string AlbumArtist { get; init; } = string.Empty;

    public List<string> Artists { get; init; } = [];

    public List<DeezerArtistInfo> ArtistInfos { get; init; } = [];

    public List<DeezerTrack> Tracks { get; init; } = [];

    public bool? Explicit { get; init; }
}

public class DeezerClient
{
    private const string Base = "https://api.deezer.com";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    private readonly PacedHttp _http;
    private readonly HttpCache _cache;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), DeezerAlbumMatch> _byKa = [];
    private readonly Dictionary<int, DeezerAlbumMatch> _byId = [];
    private readonly Dictionary<string, DeezerArtistInfo?> _arts = new(StringComparer.Ordinal);

    public DeezerClient(IHttpClientFactory factory, HttpCache cache, ILogger<DeezerClient> logger)
    {
        _ = logger;
        _cache = cache;
        _http = new PacedHttp(factory, cache, TimeSpan.FromMilliseconds(120));
    }

    public int HttpCount => _http.HttpCount;

    public int CacheHits => _http.CacheHits;

    public async Task<DeezerAlbumMatch> LookupAlbumAsync(string artist, string album, string sampleTitle, CancellationToken cancellationToken)
    {
        var key = (Titles.Norm(artist), Titles.Norm(album));
        lock (_gate)
        {
            if (_byKa.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var cacheKey = $"deezer/album-match/v1|{key.Item1}|{key.Item2}";
        if (_cache.TryGet(cacheKey, Ttl, out var disk))
        {
            var cached = MatchFromCache(disk);
            lock (_gate)
            {
                _byKa[key] = cached;
                if (cached.AlbumId != 0)
                {
                    _byId[cached.AlbumId] = cached;
                }
            }

            return cached;
        }

        var match = new DeezerAlbumMatch();
        foreach (var variant in AlbumVariants(album))
        {
            match = await SearchAlbumAsync(artist, variant, cancellationToken).ConfigureAwait(false);
            if (match.AlbumId != 0)
            {
                break;
            }
        }

        if (match.AlbumId == 0 && !string.IsNullOrWhiteSpace(sampleTitle))
        {
            var tm = await SearchTrackAlbumAsync(artist, sampleTitle, cancellationToken).ConfigureAwait(false);
            if (tm.AlbumId != 0)
            {
                match = tm;
            }
        }

        _cache.SetObject(cacheKey, MatchToCache(match));
        lock (_gate)
        {
            _byKa[key] = match;
            if (match.AlbumId != 0)
            {
                _byId[match.AlbumId] = match;
            }
        }

        return match;
    }

    public async Task<DeezerArtistInfo?> SearchArtistAsync(string name, CancellationToken cancellationToken)
    {
        var want = Titles.Norm(name);
        if (want.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_arts.TryGetValue(want, out var cached))
            {
                return cached;
            }
        }

        var payload = await _http.GetJsonAsync(
            "deezer/search/artist",
            Base + "/search/artist",
            new Dictionary<string, string> { ["q"] = name, ["limit"] = "8" },
            Ttl,
            cancellationToken).ConfigureAwait(false);

        DeezerArtistInfo? best = null;
        var bestScore = 0.0;
        if (payload is { } p)
        {
            foreach (var raw in JsonUtil.Arr(p, "data"))
            {
                var got = JsonUtil.Str(raw, "name").Trim();
                if (got.Length == 0)
                {
                    continue;
                }

                var gotN = Titles.Norm(got);
                var score = Similarity.Ratio(gotN, want);
                if (gotN == want)
                {
                    score = 1;
                }

                if (score < 0.86)
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = new DeezerArtistInfo
                    {
                        Name = got,
                        ArtistId = (int)JsonUtil.Num(raw, "id"),
                        Picture = PictureUrl(raw)
                    };
                }
            }
        }

        if (best is { Picture: "", ArtistId: not 0 })
        {
            var detail = await GetAsync("artist/" + best.ArtistId, null, cancellationToken).ConfigureAwait(false);
            if (detail is { } d && !d.TryGetProperty("error", out _))
            {
                best = new DeezerArtistInfo { Name = best.Name, ArtistId = best.ArtistId, Picture = PictureUrl(d) };
            }
        }

        lock (_gate)
        {
            _arts[want] = best;
        }

        return best;
    }

    public async Task<(byte[] Data, string Mime)?> DownloadImageAsync(string url, CancellationToken cancellationToken)
        => await _http.GetBytesAsync(url, cancellationToken).ConfigureAwait(false);

    public static DeezerTrack? MatchTrack(string title, IReadOnlyList<DeezerTrack> tracks)
    {
        var want = Titles.Norm(title);
        if (want.Length == 0 || tracks.Count == 0)
        {
            return null;
        }

        DeezerTrack? best = null;
        var bestScore = -1.0;
        var bestRank = -1;
        foreach (var t in tracks)
        {
            var got = Titles.Norm(t.Title);
            if (got.Length == 0)
            {
                continue;
            }

            var score = Similarity.Ratio(got, want);
            if (got == want)
            {
                score = 1;
            }
            else if (got.Contains(want, StringComparison.Ordinal) || want.Contains(got, StringComparison.Ordinal))
            {
                score = Math.Max(score, 0.84);
            }

            if (score < 0.72)
            {
                continue;
            }

            var rank = ExplicitRank(t.Explicit);
            if (score > bestScore || (Math.Abs(score - bestScore) < 0.0001 && rank > bestRank))
            {
                best = t;
                bestScore = score;
                bestRank = rank;
            }
        }

        return best;
    }

    private async Task<DeezerAlbumMatch> SearchAlbumAsync(string artist, string album, CancellationToken cancellationToken)
    {
        var q = $"artist:\"{Quote(artist)}\" album:\"{Quote(album)}\"";
        var payload = await GetAsync("search/album", new Dictionary<string, string> { ["q"] = q, ["limit"] = "25" }, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return new DeezerAlbumMatch();
        }

        var hit = PickAlbum(JsonUtil.Arr(payload.Value, "data"), artist, album);
        if (hit is null)
        {
            return new DeezerAlbumMatch();
        }

        return await AlbumByIdAsync((int)JsonUtil.Num(hit.Value, "id"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeezerAlbumMatch> SearchTrackAlbumAsync(string artist, string title, CancellationToken cancellationToken)
    {
        var q = $"artist:\"{Quote(artist)}\" track:\"{Quote(Titles.StripMark(title))}\"";
        var payload = await GetAsync("search/track", new Dictionary<string, string> { ["q"] = q, ["limit"] = "15" }, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return new DeezerAlbumMatch();
        }

        var wantA = Titles.Norm(artist);
        var wantT = Titles.Norm(title);
        JsonElement? best = null;
        var bestScore = -1.0;
        var bestRank = -1;
        foreach (var item in JsonUtil.Arr(payload.Value, "data"))
        {
            var gotA = Titles.Norm(JsonUtil.Str(JsonUtil.Obj(item, "artist") ?? default, "name"));
            var gotT = Titles.Norm(JsonUtil.Str(item, "title"));
            if (!ArtistOk(gotA, wantA) || gotT.Length == 0)
            {
                continue;
            }

            var score = Similarity.Ratio(gotT, wantT);
            if (gotT == wantT)
            {
                score = 1;
            }
            else if (gotT.Contains(wantT, StringComparison.Ordinal) || wantT.Contains(gotT, StringComparison.Ordinal))
            {
                score = Math.Max(score, 0.84);
            }

            if (score < 0.72)
            {
                continue;
            }

            var rank = ExplicitRank(ExplicitFrom(item, false));
            if (score > bestScore || (Math.Abs(score - bestScore) < 0.0001 && rank > bestRank))
            {
                best = item;
                bestScore = score;
                bestRank = rank;
            }
        }

        if (best is null)
        {
            return new DeezerAlbumMatch();
        }

        var album = JsonUtil.Obj(best.Value, "album");
        var id = album is null ? 0 : (int)JsonUtil.Num(album.Value, "id");
        return id == 0 ? new DeezerAlbumMatch() : await AlbumByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private JsonElement? PickAlbum(IEnumerable<JsonElement> results, string artist, string album)
    {
        var wantA = Titles.Norm(artist);
        var wantB = Titles.Norm(album);
        JsonElement? best = null;
        var bestScore = -1.0;
        var bestRank = -1;
        foreach (var item in results)
        {
            var gotA = Titles.Norm(JsonUtil.Str(JsonUtil.Obj(item, "artist") ?? default, "name"));
            if (!ArtistOk(gotA, wantA))
            {
                continue;
            }

            var gotT = Titles.Norm(JsonUtil.Str(item, "title"));
            if (gotT.Length == 0)
            {
                continue;
            }

            var score = Similarity.Ratio(gotT, wantB);
            if (gotT == wantB)
            {
                score = 1;
            }
            else if (gotT.Contains(wantB, StringComparison.Ordinal) || wantB.Contains(gotT, StringComparison.Ordinal))
            {
                score = Math.Max(score, 0.82);
            }

            var rank = ExplicitRank(ExplicitFrom(item, true));
            if (score > bestScore || (Math.Abs(score - bestScore) < 0.0001 && rank > bestRank))
            {
                best = item;
                bestScore = score;
                bestRank = rank;
            }
        }

        return best is not null && bestScore >= 0.72 ? best : null;
    }

    private async Task<DeezerAlbumMatch> AlbumByIdAsync(int id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var hit))
            {
                return hit;
            }
        }

        var payload = await GetAsync("album/" + id, null, cancellationToken).ConfigureAwait(false);
        if (payload is null || payload.Value.TryGetProperty("error", out _) || !payload.Value.TryGetProperty("id", out _))
        {
            var miss = new DeezerAlbumMatch();
            lock (_gate)
            {
                _byId[id] = miss;
            }

            return miss;
        }

        var p = payload.Value;
        var infos = ArtistInfos(p);
        var arts = infos.Select(i => i.Name).ToList();
        var embedded = JsonUtil.Obj(p, "tracks") is { } tracksObj ? JsonUtil.Arr(tracksObj, "data") : [];
        var tracks = await AlbumTracksAsync(id, embedded, p.TryGetProperty("nb_tracks", out var nb) ? nb : default, cancellationToken).ConfigureAwait(false);
        var src = "album:" + id;
        var gs = GenresFrom(p);
        if (gs.Count == 0 && tracks.Count == 0)
        {
            src = "no-genre";
        }

        var m = new DeezerAlbumMatch
        {
            Genres = gs,
            Source = src,
            AlbumId = (int)JsonUtil.Num(p, "id"),
            Title = JsonUtil.Str(p, "title"),
            AlbumArtist = arts.Count > 0 ? arts[0] : string.Empty,
            Artists = arts,
            ArtistInfos = infos,
            Tracks = tracks,
            Explicit = ExplicitFrom(p, true)
        };
        lock (_gate)
        {
            _byId[id] = m;
        }

        return m;
    }

    private async Task<List<DeezerTrack>> AlbumTracksAsync(int id, IEnumerable<JsonElement> embedded, JsonElement nb, CancellationToken cancellationToken)
    {
        var items = new List<DeezerTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in embedded)
        {
            if (TrackFrom(raw) is { } t)
            {
                items.Add(t);
                seen.Add(t.Title);
            }
        }

        var expected = nb.ValueKind == JsonValueKind.Number ? (int)nb.GetDouble() : 0;
        if (expected > 0 && items.Count >= expected)
        {
            return items;
        }

        var path = "album/" + id + "/tracks";
        var query = new Dictionary<string, string> { ["limit"] = "100" };
        while (path.Length > 0)
        {
            var payload = await GetAsync(path, query, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                break;
            }

            foreach (var raw in JsonUtil.Arr(payload.Value, "data"))
            {
                if (TrackFrom(raw) is not { } t)
                {
                    continue;
                }

                if (!seen.Add(t.Title))
                {
                    continue;
                }

                items.Add(t);
            }

            var next = JsonUtil.Str(payload.Value, "next").Trim();
            if (next.Length == 0)
            {
                break;
            }

            if (!Uri.TryCreate(next, UriKind.Absolute, out var u))
            {
                break;
            }

            path = u.AbsolutePath.TrimStart('/');
            if (path.StartsWith("2.0/", StringComparison.Ordinal))
            {
                path = path[4..];
            }

            query = [];
            foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                query[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return items;
    }

    private async Task<JsonElement?> GetAsync(string path, Dictionary<string, string>? query, CancellationToken cancellationToken)
    {
        path = path.TrimStart('/');
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Base + "/" + path;
        return await _http.GetJsonAsync("deezer/" + path, url, query, Ttl, cancellationToken).ConfigureAwait(false);
    }

    private static DeezerTrack? TrackFrom(JsonElement raw)
    {
        var title = JsonUtil.Str(raw, "title").Trim();
        if (title.Length == 0)
        {
            return null;
        }

        return new DeezerTrack
        {
            Title = title,
            Explicit = ExplicitFrom(raw, false),
            Artists = ArtistInfos(raw).Select(i => i.Name).ToList()
        };
    }

    private static List<string> GenresFrom(JsonElement payload)
    {
        var names = new List<string>();
        var skip = new HashSet<string>(StringComparer.Ordinal) { "unclassified", "unknown", "other", "none" };
        var g = JsonUtil.Obj(payload, "genres");
        if (g is null)
        {
            return names;
        }

        foreach (var raw in JsonUtil.Arr(g.Value, "data"))
        {
            var name = JsonUtil.Str(raw, "name").Trim();
            if (name.Length == 0 || skip.Contains(Titles.Norm(name)))
            {
                continue;
            }

            names.Add(name);
        }

        return Genres.PrettyList(names, 3);
    }

    private static List<DeezerArtistInfo> ArtistInfos(JsonElement payload)
    {
        var infos = new List<DeezerArtistInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonElement? raw)
        {
            if (raw is null || raw.Value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var name = JsonUtil.Str(raw.Value, "name").Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                return;
            }

            infos.Add(new DeezerArtistInfo
            {
                Name = name,
                ArtistId = (int)JsonUtil.Num(raw.Value, "id"),
                Picture = PictureUrl(raw.Value)
            });
        }

        Add(JsonUtil.Obj(payload, "artist"));
        foreach (var p in JsonUtil.Arr(payload, "contributors"))
        {
            Add(p);
        }

        return infos;
    }

    private static string PictureUrl(JsonElement payload)
    {
        foreach (var k in new[] { "picture_xl", "picture_big", "picture" })
        {
            var s = JsonUtil.Str(payload, k).Trim();
            if (s.Length > 0)
            {
                return s;
            }
        }

        return string.Empty;
    }

    private static bool? ExplicitFrom(JsonElement payload, bool album)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.TryGetProperty("explicit_content_lyrics", out var code) && code.ValueKind != JsonValueKind.Null)
        {
            var n = code.ValueKind == JsonValueKind.Number ? (int)code.GetDouble() : 0;
            if (n == 1 || (album && n == 4))
            {
                return true;
            }

            if (n is 0 or 3)
            {
                return false;
            }

            if (n == 2)
            {
                return null;
            }
        }

        return JsonUtil.Bool(payload, "explicit_lyrics");
    }

    private static int ExplicitRank(bool? flag) => flag is null ? 1 : flag.Value ? 2 : 0;

    private static bool ArtistOk(string got, string want)
    {
        if (want.Length == 0)
        {
            return true;
        }

        if (got.Length == 0)
        {
            return false;
        }

        return got == want || got.Contains(want, StringComparison.Ordinal) || want.Contains(got, StringComparison.Ordinal);
    }

    private static IEnumerable<string> AlbumVariants(string album)
    {
        var cur = album.Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (cur.Length > 0)
        {
            if (seen.Add(cur))
            {
                yield return cur;
            }

            var nxt = ParenTail(cur).Trim();
            if (nxt == cur)
            {
                yield break;
            }

            cur = nxt;
        }
    }

    private static string ParenTail(string s)
    {
        var i = s.LastIndexOf('(');
        return i >= 0 && s.EndsWith(')') ? s[..i] : s;
    }

    private static string Quote(string s) => s.Trim().Replace("\"", string.Empty, StringComparison.Ordinal);

    private static DeezerAlbumMatch MatchFromCache(JsonElement raw)
    {
        bool? Ex(JsonElement el)
        {
            if (!el.TryGetProperty("explicit", out var p))
            {
                return null;
            }

            return p.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        var m = new DeezerAlbumMatch
        {
            Source = JsonUtil.Str(raw, "source"),
            Title = JsonUtil.Str(raw, "title"),
            AlbumArtist = JsonUtil.Str(raw, "album_artist"),
            Explicit = Ex(raw),
            AlbumId = (int)JsonUtil.Num(raw, "album_id"),
            Genres = JsonUtil.Arr(raw, "genres").Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList(),
            Artists = JsonUtil.Arr(raw, "artists").Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
        };
        foreach (var inf in JsonUtil.Arr(raw, "artist_infos"))
        {
            m.ArtistInfos.Add(new DeezerArtistInfo
            {
                Name = JsonUtil.Str(inf, "name"),
                ArtistId = (int)JsonUtil.Num(inf, "artist_id"),
                Picture = JsonUtil.Str(inf, "picture")
            });
        }

        foreach (var t in JsonUtil.Arr(raw, "tracks"))
        {
            m.Tracks.Add(new DeezerTrack
            {
                Title = JsonUtil.Str(t, "title"),
                Explicit = Ex(t),
                Artists = JsonUtil.Arr(t, "artists").Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
            });
        }

        return m;
    }

    private static Dictionary<string, object?> MatchToCache(DeezerAlbumMatch m) => new()
    {
        ["genres"] = m.Genres,
        ["source"] = m.Source,
        ["album_id"] = m.AlbumId,
        ["title"] = m.Title,
        ["album_artist"] = m.AlbumArtist,
        ["artists"] = m.Artists,
        ["explicit"] = m.Explicit,
        ["artist_infos"] = m.ArtistInfos.Select(i => new { name = i.Name, artist_id = i.ArtistId, picture = i.Picture }).ToList(),
        ["tracks"] = m.Tracks.Select(t => new { title = t.Title, @explicit = t.Explicit, artists = t.Artists }).ToList()
    };
}
