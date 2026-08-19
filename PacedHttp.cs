using System.Net.Http;
using System.Text.Json;

namespace Jellyfin.Plugin.PeanutButterJelly;

public class PacedHttp
{
    private readonly HttpClient _http;
    private readonly HttpCache _cache;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _next = DateTime.MinValue;
    private readonly TimeSpan _minDelay;
    private int _httpN;
    private int _hits;

    public PacedHttp(IHttpClientFactory factory, HttpCache cache, TimeSpan minDelay)
    {
        _http = factory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "peanut-butter-jelly/4.0 (jellyfin-plugin)");
        }

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _cache = cache;
        _minDelay = minDelay;
    }

    public int HttpCount => _httpN;

    public int CacheHits => _hits;

    public async Task<JsonElement?> GetJsonAsync(
        string cacheKey,
        string url,
        IDictionary<string, string>? query,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (query is { Count: > 0 })
        {
            var qs = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + qs;
        }

        var key = cacheKey + " " + url;
        if (_cache.TryGet(key, ttl, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        await PaceAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _httpN);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        var clone = doc.RootElement.Clone();
        _cache.Set(key, clone);
        return clone;
    }

    public async Task<(byte[] Data, string Mime)?> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _httpN);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return (data, mime);
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        await _pace.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _next = DateTime.UtcNow + _minDelay;
        }
        finally
        {
            _pace.Release();
        }
    }
}

public static class JsonUtil
{
    public static string Str(JsonElement el, string name)
    {
        if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (!el.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : p.ToString();
    }

    public static double Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return 0;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String => double.TryParse(p.GetString(), out var n) ? n : 0,
            _ => 0
        };
    }

    public static bool? Bool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
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

    public static IEnumerable<JsonElement> Arr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var x in p.EnumerateArray())
        {
            yield return x;
        }
    }

    public static JsonElement? Obj(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
}

public static class Similarity
{
    public static double Ratio(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var matches = 0;
        var used = new bool[b.Length];
        var bi = 0;
        foreach (var ch in a)
        {
            for (var j = bi; j < b.Length; j++)
            {
                if (!used[j] && b[j] == ch)
                {
                    used[j] = true;
                    matches++;
                    bi = j + 1;
                    break;
                }
            }
        }

        return 2.0 * matches / (a.Length + b.Length);
    }
}
