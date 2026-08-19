using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButterJelly;

public sealed class PlaylistEntry
{
    public string Path { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public List<string> Artists { get; set; } = [];
}

public sealed class PlaylistState
{
    public string PlaylistId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string OwnerUid { get; set; } = string.Empty;

    public List<PlaylistEntry> Entries { get; set; } = [];
}

public sealed class PlaylistPlan
{
    public string PlaylistId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Guid OwnerUid { get; init; }

    public List<Guid> DesiredIds { get; init; } = [];

    public List<Guid> LiveIds { get; init; } = [];

    public int Missing { get; init; }

    public string Source { get; init; } = "empty";

    public bool NeedsWrite
    {
        get
        {
            if (DesiredIds.Count != LiveIds.Count)
            {
                return true;
            }

            return DesiredIds.Where((t, i) => t != LiveIds[i]).Any();
        }
    }
}

public class PlaylistRepair
{
    private const string MusicMarker = "media/music/";
    private static readonly Regex CleanupRe = new(@"Item in ""([^""]+)"" cannot be found at ""([^""]+)""", RegexOptions.Compiled);
    private readonly IPlaylistManager _playlists;
    private readonly IUserManager _users;
    private readonly IApplicationPaths _paths;
    private readonly ILogger _logger;

    public PlaylistRepair(IPlaylistManager playlists, IUserManager users, IApplicationPaths paths, ILogger logger)
    {
        _playlists = playlists;
        _users = users;
        _paths = paths;
        _logger = logger;
    }

    public async Task<(List<PlaylistPlan> Plans, List<PlaylistState> States)> PlanAsync(IReadOnlyList<Audio> tracks, CancellationToken cancellationToken)
    {
        var snapshot = LoadSnapshot();
        var live = FetchLive(snapshot);
        var needSalvage = live.Any(pl =>
        {
            if (pl.Entries.Count > 0)
            {
                return false;
            }

            if (snapshot.TryGetValue(pl.PlaylistId, out var snap) && snap.Entries.Count > 0)
            {
                return false;
            }

            return !snapshot.TryGetValue(pl.Name.ToLowerInvariant(), out var byName) || byName.Entries.Count == 0;
        });
        var salvage = needSalvage ? SalvageFromLogs() : new Dictionary<string, List<PlaylistEntry>>(StringComparer.OrdinalIgnoreCase);
        var desired = Coalesce(live, snapshot, salvage);
        var roots = UserRoots(tracks);
        var byId = tracks.Where(t => t.Id != Guid.Empty).ToDictionary(t => t.Id.ToString("N"), t => t, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks.Where(t => t.Id != Guid.Empty))
        {
            byId.TryAdd(t.Id.ToString(), t);
        }

        var byRel = new Dictionary<string, Audio>(StringComparer.Ordinal);
        var byBase = new Dictionary<string, List<Audio>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tracks)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            byRel[LibraryRelative(item.Path, roots)] = item;
            var baseName = System.IO.Path.GetFileName(item.Path.Replace('\\', '/'));
            if (!byBase.TryGetValue(baseName, out var list))
            {
                list = [];
                byBase[baseName] = list;
            }

            list.Add(item);
        }

        var liveById = live.ToDictionary(p => p.PlaylistId, StringComparer.OrdinalIgnoreCase);
        var plans = new List<PlaylistPlan>();
        var resolved = new List<PlaylistState>();
        foreach (var pl in desired)
        {
            var found = new List<PlaylistEntry>();
            var ids = new List<Guid>();
            var missing = 0;
            var seen = new HashSet<Guid>();
            foreach (var entry in pl.Entries)
            {
                if (!ResolveEntry(entry, byId, byRel, byBase, roots, out var item) || item.Id == Guid.Empty)
                {
                    missing++;
                    continue;
                }

                if (!seen.Add(item.Id))
                {
                    continue;
                }

                ids.Add(item.Id);
                found.Add(FromItem(item));
            }

            var liveIds = new List<Guid>();
            if (liveById.TryGetValue(pl.PlaylistId, out var livePl))
            {
                foreach (var e in livePl.Entries)
                {
                    if (Guid.TryParse(e.ItemId, out var gid))
                    {
                        liveIds.Add(gid);
                    }
                }
            }

            var source = "empty";
            if (liveById.TryGetValue(pl.PlaylistId, out var lp) && lp.Entries.Count > 0)
            {
                source = "live";
            }
            else if (snapshot.TryGetValue(pl.PlaylistId, out var s1) && s1.Entries.Count > 0)
            {
                source = "snapshot";
            }
            else if (snapshot.TryGetValue(pl.Name.ToLowerInvariant(), out var s2) && s2.Entries.Count > 0)
            {
                source = "snapshot";
            }
            else if (salvage.TryGetValue(pl.Name.ToLowerInvariant(), out var sv) && sv.Count > 0)
            {
                source = "log";
            }

            Guid.TryParse(pl.OwnerUid, out var owner);
            plans.Add(new PlaylistPlan
            {
                PlaylistId = pl.PlaylistId,
                Name = pl.Name,
                OwnerUid = owner,
                DesiredIds = ids,
                LiveIds = liveIds,
                Missing = missing,
                Source = source
            });
            resolved.Add(new PlaylistState
            {
                PlaylistId = pl.PlaylistId,
                Name = pl.Name,
                OwnerUid = pl.OwnerUid,
                Entries = found.Count > 0 ? found : pl.Entries
            });
        }

        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (plans, resolved);
    }

    public async Task ApplyAsync(PlaylistPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.NeedsWrite)
        {
            return;
        }

        var playlist = _playlists.GetPlaylists(plan.OwnerUid).FirstOrDefault(p => p.Id.ToString("N").Equals(plan.PlaylistId, StringComparison.OrdinalIgnoreCase)
            || p.Id.ToString().Equals(plan.PlaylistId, StringComparison.OrdinalIgnoreCase));
        if (playlist is null)
        {
            throw new InvalidOperationException("Playlist not found: " + plan.Name);
        }

        var entryIds = playlist.LinkedChildren
            .Select(c => c.ItemId?.ToString("N"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToArray();
        if (entryIds.Length > 0)
        {
            await _playlists.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), entryIds).ConfigureAwait(false);
        }

        if (plan.DesiredIds.Count > 0)
        {
            await _playlists.AddItemToPlaylistAsync(playlist.Id, plan.DesiredIds, plan.OwnerUid).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void SaveSnapshot(IReadOnlyList<PlaylistState> states)
    {
        var payload = new SnapshotFile
        {
            SavedAt = DateTime.UtcNow.ToString("O"),
            Playlists = states.ToList()
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SnapshotPath())!);
        File.WriteAllText(SnapshotPath(), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private List<PlaylistState> FetchLive(Dictionary<string, PlaylistState> snapshot)
    {
        var states = new List<PlaylistState>();
        var seen = new HashSet<Guid>();
        foreach (var user in _users.Users)
        {
            foreach (var pl in _playlists.GetPlaylists(user.Id))
            {
                if (!seen.Add(pl.Id))
                {
                    continue;
                }

                var entries = new List<PlaylistEntry>();
                foreach (var pair in pl.GetManageableItems())
                {
                    var linked = pair.Item1;
                    var item = pair.Item2;
                    if (linked.ItemId is null && item is null)
                    {
                        continue;
                    }

                    entries.Add(new PlaylistEntry
                    {
                        ItemId = (linked.ItemId ?? item?.Id)?.ToString("N") ?? string.Empty,
                        Path = item?.Path ?? linked.Path ?? string.Empty,
                        Name = item?.Name ?? string.Empty,
                        Album = (item as Audio)?.Album ?? string.Empty,
                        Artists = (item as Audio)?.Artists.ToList() ?? []
                    });
                }

                states.Add(new PlaylistState
                {
                    PlaylistId = pl.Id.ToString("N"),
                    Name = pl.Name,
                    OwnerUid = user.Id.ToString("N"),
                    Entries = entries
                });
            }
        }

        _ = snapshot;
        return states;
    }

    private Dictionary<string, List<PlaylistEntry>> SalvageFromLogs()
    {
        var byName = new Dictionary<string, List<PlaylistEntry>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var logDir = _paths.LogDirectoryPath;
        if (!Directory.Exists(logDir))
        {
            return byName;
        }

        foreach (var file in Directory.EnumerateFiles(logDir, "*.log"))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read log {File}", file);
                continue;
            }

            foreach (Match m in CleanupRe.Matches(text))
            {
                var plName = m.Groups[1].Value;
                var path = m.Groups[2].Value;
                var key = plName.ToLowerInvariant();
                if (!seen.TryGetValue(key, out var used))
                {
                    used = [];
                    seen[key] = used;
                }

                if (!used.Add(path))
                {
                    continue;
                }

                if (!byName.TryGetValue(key, out var list))
                {
                    list = [];
                    byName[key] = list;
                }

                list.Add(new PlaylistEntry { Path = path });
            }
        }

        return byName;
    }

    private Dictionary<string, PlaylistState> LoadSnapshot()
    {
        var outMap = new Dictionary<string, PlaylistState>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SnapshotPath()))
        {
            return outMap;
        }

        try
        {
            var data = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(SnapshotPath()));
            if (data?.Playlists is null)
            {
                return outMap;
            }

            foreach (var state in data.Playlists)
            {
                if (!string.IsNullOrEmpty(state.PlaylistId))
                {
                    outMap[state.PlaylistId] = state;
                }

                if (!string.IsNullOrEmpty(state.Name))
                {
                    outMap.TryAdd(state.Name.ToLowerInvariant(), state);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load playlist snapshot");
        }

        return outMap;
    }

    private string SnapshotPath() => System.IO.Path.Combine(_paths.DataPath, "plugins", "peanut-butter-jelly", "playlist_snapshot.json");

    private static List<PlaylistState> Coalesce(List<PlaylistState> live, Dictionary<string, PlaylistState> snapshot, Dictionary<string, List<PlaylistEntry>> salvage)
    {
        var desired = new List<PlaylistState>();
        foreach (var pl in live)
        {
            var entries = pl.Entries.ToList();
            snapshot.TryGetValue(pl.PlaylistId, out var snap);
            if (snap is null || snap.Entries.Count == 0)
            {
                snapshot.TryGetValue(pl.Name.ToLowerInvariant(), out snap);
            }

            if (entries.Count == 0 && snap is { Entries.Count: > 0 })
            {
                entries = snap.Entries.ToList();
            }

            if (entries.Count == 0 && salvage.TryGetValue(pl.Name.ToLowerInvariant(), out var salvaged))
            {
                entries = salvaged.ToList();
            }

            desired.Add(new PlaylistState { PlaylistId = pl.PlaylistId, Name = pl.Name, OwnerUid = pl.OwnerUid, Entries = entries });
        }

        return desired;
    }

    private static bool ResolveEntry(
        PlaylistEntry entry,
        Dictionary<string, Audio> byId,
        Dictionary<string, Audio> byRel,
        Dictionary<string, List<Audio>> byBase,
        HashSet<string> roots,
        out Audio item)
    {
        item = null!;
        if (!string.IsNullOrEmpty(entry.ItemId) && byId.TryGetValue(entry.ItemId.Replace("-", string.Empty, StringComparison.Ordinal), out item!))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(entry.ItemId) && byId.TryGetValue(entry.ItemId, out item!))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(entry.Path))
        {
            var rel = LibraryRelative(entry.Path, roots);
            if (byRel.TryGetValue(rel, out item!))
            {
                return true;
            }

            var baseName = System.IO.Path.GetFileName(entry.Path.Replace('\\', '/'));
            if (byBase.TryGetValue(baseName, out var names) && names.Count == 1)
            {
                item = names[0];
                return true;
            }
        }

        if (!string.IsNullOrEmpty(entry.Name))
        {
            var want = Titles.StripMark(entry.Name).Trim().ToLowerInvariant();
            var wantAlbum = Titles.StripMark(entry.Album).Trim().ToLowerInvariant();
            var hits = byId.Values.Where(it =>
            {
                if (Titles.StripMark(it.Name).Trim().ToLowerInvariant() != want)
                {
                    return false;
                }

                return wantAlbum.Length == 0 || Titles.StripMark(it.Album ?? string.Empty).Trim().ToLowerInvariant() == wantAlbum;
            }).Distinct().ToList();
            if (hits.Count == 1)
            {
                item = hits[0];
                return true;
            }
        }

        return false;
    }

    private static PlaylistEntry FromItem(Audio item) => new()
    {
        Path = item.Path ?? string.Empty,
        ItemId = item.Id.ToString("N"),
        Name = item.Name ?? string.Empty,
        Album = item.Album ?? string.Empty,
        Artists = item.Artists.ToList()
    };

    private static HashSet<string> UserRoots(IEnumerable<Audio> tracks)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tracks)
        {
            var rel = AfterMusic(item.Path ?? string.Empty);
            var first = rel.Contains('/', StringComparison.Ordinal) ? rel.Split('/')[0] : rel;
            if (!string.IsNullOrWhiteSpace(first))
            {
                roots.Add(first.Trim());
            }
        }

        return roots;
    }

    private static string AfterMusic(string path)
    {
        var raw = path.Trim().Replace('\\', '/');
        var idx = raw.ToLowerInvariant().IndexOf(MusicMarker, StringComparison.Ordinal);
        return idx >= 0 ? raw[(idx + MusicMarker.Length)..] : raw.TrimStart('/');
    }

    private static string LibraryRelative(string path, HashSet<string> roots)
    {
        var rel = AfterMusic(path);
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && roots.Contains(parts[0]))
        {
            parts.RemoveAt(0);
        }

        return string.Join('/', parts).ToLowerInvariant();
    }

    private sealed class SnapshotFile
    {
        public string SavedAt { get; set; } = string.Empty;

        public List<PlaylistState> Playlists { get; set; } = [];
    }
}
