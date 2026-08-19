using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButterJelly;

public class TaggerEngine
{
    private static readonly HashSet<string> SkipArtistMeta = new(StringComparer.OrdinalIgnoreCase)
    {
        "various artists", "various", "unknown artist", "unknown"
    };

    private readonly ILibraryManager _library;
    private readonly ILyricManager _lyrics;
    private readonly IProviderManager _providers;
    private readonly IUserManager _users;
    private readonly IPlaylistManager _playlists;
    private readonly DeezerClient _deezer;
    private readonly LrcLibClient _lrclib;
    private readonly BioClient _bios;
    private readonly MediaBrowser.Common.Configuration.IApplicationPaths _paths;
    private readonly ILogger<TaggerEngine> _logger;

    public TaggerEngine(
        ILibraryManager library,
        ILyricManager lyrics,
        IProviderManager providers,
        IUserManager users,
        IPlaylistManager playlists,
        DeezerClient deezer,
        LrcLibClient lrclib,
        BioClient bios,
        MediaBrowser.Common.Configuration.IApplicationPaths paths,
        ILogger<TaggerEngine> logger)
    {
        _library = library;
        _lyrics = lyrics;
        _providers = providers;
        _users = users;
        _playlists = playlists;
        _deezer = deezer;
        _lrclib = lrclib;
        _bios = bios;
        _paths = paths;
        _logger = logger;
    }

    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var force = cfg.Force;
        var workers = cfg.Workers <= 0 ? Environment.ProcessorCount : cfg.Workers;
        workers = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount));
        using var gate = new SemaphoreSlim(workers, workers);

        var tracks = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true
        }).OfType<Audio>().Where(t => t.Id != Guid.Empty).ToList();

        var albums = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true
        }).OfType<MusicAlbum>().Where(a => a.Id != Guid.Empty).ToDictionary(a => a.Id);

        var artists = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            Recursive = true
        }).OfType<MusicArtist>().Where(a => a.Id != Guid.Empty && !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("PBJ library: {Tracks} tracks, {Albums} albums, {Artists} artists, {Workers} workers", tracks.Count, albums.Count, artists.Count, workers);

        var grouped = tracks
            .GroupBy(t => (AlbumArtistOf(t), Titles.StripMark((t.Album ?? string.Empty).Trim())))
            .OrderBy(g => g.Key.Item1, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lookups = new ConcurrentDictionary<(string Artist, string Album), DeezerAlbumMatch>();
        var done = 0;
        var albumJobs = grouped.Select(async g =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sample = g.FirstOrDefault()?.Name ?? string.Empty;
                var match = await _deezer.LookupAlbumAsync(g.Key.Item1, g.Key.Item2, Titles.StripMark(sample), cancellationToken).ConfigureAwait(false);
                lookups[(g.Key.Item1, g.Key.Item2)] = match;
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress.Report(5 + 40.0 * n / Math.Max(1, grouped.Count));
            }
        });
        await Task.WhenAll(albumJobs).ConfigureAwait(false);

        var patches = new ConcurrentDictionary<Guid, Patch>();
        void Queue(Patch p)
        {
            if (p.Empty)
            {
                return;
            }

            patches.AddOrUpdate(p.ItemId, p, (_, existing) => existing.Merge(p));
        }

        var lyricsJobs = new ConcurrentDictionary<Guid, LyricsJob>();
        var artistPhotos = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = lookups.GetValueOrDefault((g.Key.Item1, g.Key.Item2)) ?? new DeezerAlbumMatch();
            var items = g.ToList();
            if (!cfg.TagAlbums && !cfg.TagTracks && !cfg.FetchLyrics)
            {
                continue;
            }

            if (match.AlbumId == 0)
            {
                foreach (var item in items)
                {
                    RememberLyrics(item, lyricsJobs, force, cfg);
                    if (cfg.TagTracks && !HasTag(item, Titles.NoMatchTag))
                    {
                        Queue(new Patch { ItemId = item.Id, Item = item, AddTags = [Titles.NoMatchTag] });
                    }
                }

                continue;
            }

            foreach (var info in match.ArtistInfos)
            {
                if (info.Picture.Length > 0 && !SkipMeta(info.Name))
                {
                    artistPhotos.TryAdd(info.Name, info.Picture);
                }
            }

            var wantAlbumArtists = match.Artists.Count > 0 ? match.Artists : (string.IsNullOrEmpty(match.AlbumArtist) ? [] : new List<string> { match.AlbumArtist });
            var albumIds = items.Select(t => t.AlbumEntity?.Id ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
            if (cfg.TagAlbums)
            {
                foreach (var albumId in albumIds)
                {
                    albums.TryGetValue(albumId, out var albumItem);
                    var albumName = albumItem?.Name ?? g.Key.Item2;
                    if (string.IsNullOrEmpty(albumName) && items.Count > 0)
                    {
                        albumName = items[0].Album ?? string.Empty;
                    }

                    var marked = Titles.HasExplicitMark(albumName);
                    var tagged = albumItem is not null && HasTag(albumItem, "Explicit");
                    var needUntag = tagged;
                    var needMark = match.Explicit == true && !marked;
                    var needUnmark = match.Explicit == false && marked;
                    var gotGenres = albumItem is null ? [] : Genres.PrettyList(albumItem.Genres, 0);
                    var needGenres = match.Genres.Count > 0 && ShouldWrite(gotGenres.Count > 0, !gotGenres.SequenceEqual(match.Genres), force);
                    var gotArtists = albumItem?.Artists.ToList() ?? [];
                    var gotAlbumArtists = albumItem?.AlbumArtists.ToList() ?? [];
                    var gotAlbumArtist = gotAlbumArtists.FirstOrDefault() ?? string.Empty;
                    var needArtists = wantAlbumArtists.Count > 0 && ShouldWrite(
                        gotArtists.Count > 0 || gotAlbumArtists.Count > 0,
                        !gotArtists.SequenceEqual(wantAlbumArtists) || gotAlbumArtist != match.AlbumArtist || !gotAlbumArtists.SequenceEqual(wantAlbumArtists),
                        force);

                    if (!needGenres && !needArtists && !needMark && !needUnmark && !needUntag)
                    {
                        continue;
                    }

                    string? nameWrite = null;
                    if (needMark)
                    {
                        nameWrite = Titles.DesiredTitle(albumName, true);
                    }
                    else if (needUnmark)
                    {
                        nameWrite = Titles.DesiredTitle(albumName, false);
                    }

                    Queue(new Patch
                    {
                        ItemId = albumId,
                        Item = albumItem,
                        Name = nameWrite,
                        Genres = needGenres ? match.Genres : null,
                        Artists = needArtists ? wantAlbumArtists : null,
                        AlbumArtist = needArtists && match.AlbumArtist.Length > 0 ? match.AlbumArtist : null,
                        Explicit = needUntag ? false : null
                    });
                }
            }

            foreach (var item in items)
            {
                var dzTrack = DeezerClient.MatchTrack(item.Name, match.Tracks);
                var tagged = HasTag(item, "Explicit");
                var marked = Titles.HasExplicitMark(item.Name);
                List<string>? addTags = null;
                List<string>? removeTags = null;
                if (cfg.TagTracks)
                {
                    if (dzTrack is null)
                    {
                        if (!HasTag(item, Titles.NoMatchTag))
                        {
                            addTags = [Titles.NoMatchTag];
                        }
                    }
                    else if (HasTag(item, Titles.NoMatchTag))
                    {
                        removeTags = [Titles.NoMatchTag];
                    }
                }

                var wantE = cfg.TagTracks && dzTrack?.Explicit == true;
                var clearE = cfg.TagTracks && dzTrack?.Explicit == false && (tagged || marked);
                var needE = wantE && (!tagged || !marked);
                var wantArtists = dzTrack is { Artists.Count: > 0 } ? dzTrack.Artists
                    : match.AlbumArtist.Length > 0 ? [match.AlbumArtist] : new List<string>();
                RememberLyrics(item, lyricsJobs, force, cfg, wantArtists, g.Key.Item2);
                var needArtists = cfg.TagTracks && wantArtists.Count > 0 && ShouldWrite(
                    item.Artists.Count > 0 || item.AlbumArtists.Count > 0,
                    !item.Artists.SequenceEqual(wantArtists) || (item.AlbumArtists.FirstOrDefault() ?? string.Empty) != match.AlbumArtist,
                    force);
                if (!needE && !clearE && !needArtists && addTags is null && removeTags is null)
                {
                    continue;
                }

                string? newName = null;
                if (needE)
                {
                    newName = Titles.DesiredTitle(item.Name, true);
                }
                else if (clearE && marked)
                {
                    newName = Titles.DesiredTitle(item.Name, false);
                }

                Queue(new Patch
                {
                    ItemId = item.Id,
                    Item = item,
                    Name = newName,
                    Artists = needArtists ? wantArtists : null,
                    AlbumArtist = needArtists && match.AlbumArtist.Length > 0 ? match.AlbumArtist : null,
                    Explicit = needE ? true : clearE && tagged ? false : null,
                    AddTags = addTags ?? [],
                    RemoveTags = removeTags ?? []
                });
            }
        }

        progress.Report(50);

        if (cfg.FetchArtists && artists.Count > 0)
        {
            var jobs = artists.Values.Where(a => !SkipMeta(a.Name) && a.Id != Guid.Empty).Select(a =>
            {
                var needPhoto = force || !a.HasImage(ImageType.Primary);
                var needBio = force || string.IsNullOrWhiteSpace(a.Overview);
                return (Artist: a, NeedPhoto: needPhoto, NeedBio: needBio);
            }).Where(j => j.NeedPhoto || j.NeedBio).ToList();

            var artistDone = 0;
            await Task.WhenAll(jobs.Select(async job =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var picture = string.Empty;
                    if (job.NeedPhoto)
                    {
                        artistPhotos.TryGetValue(job.Artist.Name, out picture!);
                        picture ??= string.Empty;
                        if (picture.Length == 0)
                        {
                            var found = await _deezer.SearchArtistAsync(job.Artist.Name, cancellationToken).ConfigureAwait(false);
                            picture = found?.Picture ?? string.Empty;
                        }
                    }

                    BioMatch bio = new();
                    if (job.NeedBio)
                    {
                        bio = await _bios.LookupAsync(job.Artist.Name, cancellationToken).ConfigureAwait(false);
                    }

                    if (picture.Length == 0 && bio.Overview.Length == 0)
                    {
                        return;
                    }

                    Queue(new Patch
                    {
                        ItemId = job.Artist.Id,
                        Item = job.Artist,
                        ImageUrl = picture,
                        Overview = bio.Overview.Length > 0 ? bio.Overview : null
                    });
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref artistDone);
                    progress.Report(50 + 15.0 * n / Math.Max(1, jobs.Count));
                }
            })).ConfigureAwait(false);
        }

        progress.Report(70);

        if (cfg.FetchLyrics && !lyricsJobs.IsEmpty)
        {
            var ids = lyricsJobs.Keys.ToList();
            var lyricDone = 0;
            await Task.WhenAll(ids.Select(async id =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var job = lyricsJobs[id];
                    var m = await _lrclib.LookupArtistsAsync(job.Title, job.Artists, job.Album, job.Duration, cancellationToken).ConfigureAwait(false);
                    if (m.Instrumental && m.Synced.Length == 0 && m.Plain.Length == 0)
                    {
                        return;
                    }

                    string ext;
                    string text;
                    if (m.Synced.Length > 0)
                    {
                        ext = "lrc";
                        text = m.Synced;
                    }
                    else if (m.Plain.Length > 0)
                    {
                        ext = "txt";
                        text = m.Plain;
                    }
                    else
                    {
                        return;
                    }

                    Queue(new Patch { ItemId = id, Item = job.Item, LyricsText = text, LyricsFormat = ext });
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref lyricDone);
                    progress.Report(70 + 15.0 * n / Math.Max(1, ids.Count));
                }
            })).ConfigureAwait(false);
        }

        progress.Report(85);
        var list = patches.Values.ToList();
        var applyDone = 0;
        await Task.WhenAll(list.Select(async p =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ApplyPatchAsync(p, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PBJ failed to update {Id}", p.ItemId);
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref applyDone);
                progress.Report(85 + 10.0 * n / Math.Max(1, list.Count));
            }
        })).ConfigureAwait(false);

        if (cfg.RepairPlaylists)
        {
            try
            {
                var repair = new PlaylistRepair(_playlists, _users, _paths, _logger);
                var (plans, states) = await repair.PlanAsync(tracks, cancellationToken).ConfigureAwait(false);
                repair.SaveSnapshot(states);
                foreach (var plan in plans.Where(p => p.NeedsWrite))
                {
                    try
                    {
                        await repair.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("PBJ rewrote playlist {Name} ({Live} → {Desired})", plan.Name, plan.LiveIds.Count, plan.DesiredIds.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "PBJ playlist {Name} failed", plan.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PBJ playlist repair skipped");
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "PBJ finished: {Patches} writes, Deezer http {Dz}/{DzC} cache, LRCLIB {L}/{Lc}, bios {B}/{Bc}",
            list.Count,
            _deezer.HttpCount,
            _deezer.CacheHits,
            _lrclib.HttpCount,
            _lrclib.CacheHits,
            _bios.HttpCount,
            _bios.CacheHits);
    }

    private async Task ApplyPatchAsync(Patch p, CancellationToken cancellationToken)
    {
        var item = p.Item ?? _library.GetItemById(p.ItemId);
        if (item is null)
        {
            return;
        }

        var dirty = false;
        if (p.Name is not null && item.Name != p.Name)
        {
            item.Name = p.Name;
            dirty = true;
        }

        if (p.Genres is not null)
        {
            item.Genres = p.Genres.ToArray();
            dirty = true;
        }

        if (p.Overview is not null && item.Overview != p.Overview)
        {
            item.Overview = p.Overview;
            dirty = true;
        }

        if (item is Audio audio)
        {
            if (p.Artists is not null)
            {
                audio.Artists = p.Artists;
                dirty = true;
            }

            if (p.AlbumArtist is not null)
            {
                audio.AlbumArtists = [p.AlbumArtist];
                dirty = true;
            }
        }
        else if (item is MusicAlbum album)
        {
            if (p.Artists is not null)
            {
                album.Artists = p.Artists;
                dirty = true;
            }

            if (p.AlbumArtist is not null)
            {
                album.AlbumArtists = [p.AlbumArtist];
                dirty = true;
            }
        }

        var tags = item.Tags.ToList();
        var tagDirty = false;
        if (p.Explicit is not null)
        {
            tags = tags.Where(t => !t.Equals("Explicit", StringComparison.OrdinalIgnoreCase)).ToList();
            if (p.Explicit.Value)
            {
                tags.Add("Explicit");
            }

            tagDirty = true;
        }

        foreach (var t in p.RemoveTags)
        {
            var n = tags.RemoveAll(x => x.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                tagDirty = true;
            }
        }

        foreach (var t in p.AddTags)
        {
            if (!tags.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)))
            {
                tags.Add(t);
                tagDirty = true;
            }
        }

        if (tagDirty)
        {
            item.Tags = tags.ToArray();
            dirty = true;
        }

        if (dirty)
        {
            await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(p.LyricsText) && item is Audio lyricItem)
        {
            await _lyrics.SaveLyricAsync(lyricItem, p.LyricsFormat, p.LyricsText).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(p.ImageUrl))
        {
            var img = await _deezer.DownloadImageAsync(p.ImageUrl, cancellationToken).ConfigureAwait(false);
            if (img is { } got)
            {
                var mime = got.Mime.ToLowerInvariant().Split(';')[0].Trim();
                if (mime is "image/jpg" or "jpg" or "jpeg" || !mime.StartsWith("image/", StringComparison.Ordinal))
                {
                    mime = "image/jpeg";
                }

                await using var stream = new MemoryStream(got.Data);
                await _providers.SaveImage(item, stream, mime, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
                await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void RememberLyrics(Audio item, ConcurrentDictionary<Guid, LyricsJob> jobs, bool force, Configuration.PluginConfiguration cfg, IReadOnlyList<string>? extra = null, string? album = null)
    {
        if (!cfg.FetchLyrics || item.Id == Guid.Empty || jobs.ContainsKey(item.Id))
        {
            return;
        }

        if (!force && item.HasLyrics == true)
        {
            return;
        }

        var title = Titles.StripMark(item.Name);
        var arts = LrcLibClient.QueryArtists(item, extra);
        if (title.Length == 0 || arts.Count == 0)
        {
            return;
        }

        var alb = Titles.StripMark(album ?? item.Album ?? string.Empty);
        int? duration = null;
        if (item.RunTimeTicks is > 10_000_000 and < 36_000_000_000)
        {
            duration = (int)Math.Round(item.RunTimeTicks.Value / 10_000_000.0);
        }

        jobs[item.Id] = new LyricsJob(item, title, alb, arts, duration);
    }

    private static bool ShouldWrite(bool present, bool differs, bool force) => (force && differs) || (!force && !present);

    private static bool SkipMeta(string name)
    {
        var key = name.Trim();
        if (key.Length == 0 || SkipArtistMeta.Contains(key))
        {
            return true;
        }

        return key.StartsWith('[') && key.EndsWith(']');
    }

    private static string AlbumArtistOf(Audio item)
    {
        if (item.AlbumArtists.Count > 0)
        {
            return item.AlbumArtists[0];
        }

        return item.Artists.Count > 0 ? item.Artists[0] : string.Empty;
    }

    private static bool HasTag(BaseItem item, string name)
        => item.Tags.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed class LyricsJob(Audio item, string title, string album, List<string> artists, int? duration)
    {
        public Audio Item { get; } = item;

        public string Title { get; } = title;

        public string Album { get; } = album;

        public List<string> Artists { get; } = artists;

        public int? Duration { get; } = duration;
    }

    private sealed class Patch
    {
        public Guid ItemId { get; init; }

        public BaseItem? Item { get; init; }

        public List<string>? Genres { get; init; }

        public List<string>? Artists { get; init; }

        public string? AlbumArtist { get; init; }

        public bool? Explicit { get; init; }

        public string? Name { get; init; }

        public List<string> AddTags { get; init; } = [];

        public List<string> RemoveTags { get; init; } = [];

        public string? Overview { get; init; }

        public string LyricsText { get; init; } = string.Empty;

        public string LyricsFormat { get; init; } = "lrc";

        public string ImageUrl { get; init; } = string.Empty;

        public bool Empty =>
            Genres is null && Artists is null && AlbumArtist is null && Explicit is null && Name is null
            && AddTags.Count == 0 && RemoveTags.Count == 0 && Overview is null
            && LyricsText.Length == 0 && ImageUrl.Length == 0;

        public Patch Merge(Patch src) => new()
        {
            ItemId = ItemId,
            Item = Item ?? src.Item,
            Genres = src.Genres ?? Genres,
            Artists = src.Artists ?? Artists,
            AlbumArtist = src.AlbumArtist ?? AlbumArtist,
            Explicit = src.Explicit ?? Explicit,
            Name = src.Name ?? Name,
            AddTags = AddTags.Concat(src.AddTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RemoveTags = RemoveTags.Concat(src.RemoveTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Overview = src.Overview ?? Overview,
            LyricsText = src.LyricsText.Length > 0 ? src.LyricsText : LyricsText,
            LyricsFormat = src.LyricsText.Length > 0 ? src.LyricsFormat : LyricsFormat,
            ImageUrl = src.ImageUrl.Length > 0 ? src.ImageUrl : ImageUrl
        };
    }
}
