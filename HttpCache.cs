using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.PeanutButterJelly;

public class HttpCache
{
    private readonly string _dir;
    private readonly object _gate = new();

    public HttpCache(IApplicationPaths paths)
    {
        _dir = Path.Combine(paths.CachePath, "peanut-butter-jelly");
        Directory.CreateDirectory(_dir);
    }

    public bool TryGet(string key, TimeSpan ttl, out JsonElement element)
    {
        element = default;
        var fp = Path.Combine(_dir, Hash(key) + ".json");
        lock (_gate)
        {
            if (!File.Exists(fp))
            {
                return false;
            }

            if (ttl > TimeSpan.Zero && DateTime.UtcNow - File.GetLastWriteTimeUtc(fp) > ttl)
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(fp));
                element = doc.RootElement.Clone();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Set(string key, JsonElement payload)
    {
        var fp = Path.Combine(_dir, Hash(key) + ".json");
        lock (_gate)
        {
            File.WriteAllText(fp, payload.GetRawText());
        }
    }

    public void SetObject(string key, object payload)
    {
        var fp = Path.Combine(_dir, Hash(key) + ".json");
        lock (_gate)
        {
            File.WriteAllText(fp, JsonSerializer.Serialize(payload));
        }
    }

    private static string Hash(string key)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
