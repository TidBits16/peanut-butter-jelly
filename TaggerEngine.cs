using System;
using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PeanutButterJelly.Configuration;
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
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var force = cfg.Force;
        var workers = cfg.Workers <= 0 ? Environment.ProcessorCount : cfg.Workers;
        workers = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount));
        Titles.UseStyle(cfg.ExplicitMark, cfg.PrependExplicitMark);
        try
        {
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
        var artistDeezer = new ConcurrentDictionary<string, DeezerArtistInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = lookups.GetValueOrDefault((g.Key.Item1, g.Key.Item2)) ?? new DeezerAlbumMatch();
            var items = g.ToList();
            if (!cfg.TagAlbums && !cfg.TagTracks && !cfg.WriteLyrics)
            {
                continue;
            }

            if (match.AlbumId == 0)
            {
                if (cfg.TagAlbums)
                {
                    foreach (var albumId in items.Select(t => t.AlbumEntity?.Id ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct())
                    {
                        albums.TryGetValue(albumId, out var albumItem);
                        if (GenreWant([], albumItem?.Genres) is not { } genres)
                        {
                            continue;
                        }

                        Queue(new Patch { ItemId = albumId, Item = albumItem, Genres = genres });
                    }
                }

                foreach (var item in items)
                {
                    RememberLyrics(item, lyricsJobs, force, cfg);
                    List<string>? addTags = null;
                    if (cfg.TagTracks && !HasTag(item, Titles.NoMatchTag))
                    {
                        addTags = [Titles.NoMatchTag];
                    }

                    var genres = cfg.TagTracks ? GenreWant([], item.Genres) : null;
                    if (addTags is null && genres is null)
                    {
                        continue;
                    }

                    Queue(new Patch
                    {
                        ItemId = item.Id,
                        Item = item,
                        Genres = genres,
                        AddTags = addTags ?? []
                    });
                }

                continue;
            }

            foreach (var info in match.ArtistInfos)
            {
                if (!SkipMeta(info.Name))
                {
                    artistDeezer.TryAdd(info.Name, info);
                }
            }

            var wantAlbumArtists = match.AlbumArtists;
            var wantAlbumSongArtists = match.Artists.Count > 0
                ? match.Artists
                : Titles.DistinctNames(match.Tracks.SelectMany(t => t.Artists));
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

                    var tagged = albumItem is not null && HasAnyTag(albumItem, cfg.EffectiveExplicitTags);
                    var needUntag = tagged;
                    // Never put the explicit mark on MusicAlbum.Name — Jellyfin orphans tracks and leaves empty albums.
                    // Undo marks from older plugin versions.
                    string? nameWrite = null;
                    if (cfg.RenameExplicitTitles && Titles.HasExplicitMark(albumName))
                    {
                        var stripped = Titles.StripMark(albumName);
                        if (stripped.Length > 0 && stripped != albumName)
                        {
                            nameWrite = stripped;
                        }
                    }

                    var genreWrite = GenreWant(match.Genres, albumItem?.Genres);
                    var needAlbumArtists = NeedList(wantAlbumArtists, albumItem?.AlbumArtists.ToList() ?? []);
                    var needSongArtists = NeedList(wantAlbumSongArtists, albumItem?.Artists.ToList() ?? []);
                    var needYear = NeedInt(match.Year, albumItem?.ProductionYear);
                    var needDate = NeedDate(match.ReleaseDate, albumItem?.PremiereDate);
                    var needLabel = match.Label.Length > 0 && NeedList([match.Label], albumItem?.Studios.ToList() ?? []);
                    var needDeezerId = NeedProvider(albumItem, "Deezer", match.AlbumId);
                    var needUpc = NeedProvider(albumItem, "UPC", match.Upc);
                    var needCover = match.CoverUrl.Length > 0 && albumItem is not null && (force || !albumItem.HasImage(ImageType.Primary));

                    if (genreWrite is null && !needAlbumArtists && !needSongArtists && nameWrite is null && !needUntag
                        && !needYear && !needDate && !needLabel && !needDeezerId && !needUpc && !needCover)
                    {
                        continue;
                    }

                    Queue(new Patch
                    {
                        ItemId = albumId,
                        Item = albumItem,
                        Name = nameWrite,
                        Genres = genreWrite,
                        Artists = needSongArtists ? wantAlbumSongArtists : null,
                        AlbumArtists = needAlbumArtists ? wantAlbumArtists : null,
                        Explicit = needUntag ? false : null,
                        ProductionYear = needYear ? match.Year : null,
                        PremiereDate = needDate ? match.ReleaseDate : null,
                        Studios = needLabel ? [match.Label] : null,
                        DeezerId = needDeezerId ? match.AlbumId.ToString(CultureInfo.InvariantCulture) : null,
                        Upc = needUpc ? match.Upc : null,
                        ImageUrl = needCover ? match.CoverUrl : string.Empty
                    });
                }
            }

            foreach (var item in items)
            {
                var dzTrack = DeezerClient.MatchTrack(item.Name, match.Tracks);
                var tagged = HasAnyTag(item, cfg.EffectiveExplicitTags);
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
                var newName = cfg.TagTracks ? TitlePatch(item.Name, dzTrack?.Explicit, cfg) : null;
                bool? explicitWrite = null;
                if (cfg.TagTracks && cfg.WriteExplicitTags && cfg.EffectiveExplicitTags.Count > 0)
                {
                    if (wantE && !tagged)
                    {
                        explicitWrite = true;
                    }
                    else if (clearE && tagged)
                    {
                        explicitWrite = false;
                    }
                    else if (wantE && cfg.EffectiveExplicitTags.Any(t => !HasTag(item, t)))
                    {
                        explicitWrite = true;
                    }
                }
                var wantSongArtists = dzTrack is { Artists.Count: > 0 } ? dzTrack.Artists : [];
                var lyricsNames = Titles.DistinctNames(wantSongArtists.Concat(wantAlbumArtists));
                RememberLyrics(item, lyricsJobs, force, cfg, lyricsNames, g.Key.Item2);
                var needSongArtists = cfg.TagTracks && NeedList(wantSongArtists, item.Artists);
                var needAlbumArtists = cfg.TagTracks && NeedList(wantAlbumArtists, item.AlbumArtists);
                var genreWrite = cfg.TagTracks ? GenreWant(match.Genres, item.Genres) : null;
                // Keep Audio.Album free of the explicit mark so tracks stay attached to the album entity.
                string? albumFieldWrite = null;
                if (cfg.TagTracks && cfg.RenameExplicitTitles && Titles.HasExplicitMark(item.Album ?? string.Empty))
                {
                    var stripped = Titles.StripMark(item.Album ?? string.Empty);
                    if (stripped.Length > 0 && stripped != item.Album)
                    {
                        albumFieldWrite = stripped;
                    }
                }

                var trackRelease = dzTrack?.ReleaseDate ?? match.ReleaseDate;
                var trackYear = trackRelease is { Year: >= 1000 } d ? d.Year : (int?)null;
                var needYear = cfg.TagTracks && NeedInt(trackYear, item.ProductionYear);
                var needDate = cfg.TagTracks && NeedDate(trackRelease, item.PremiereDate);
                var needIndex = cfg.TagTracks && NeedInt(dzTrack?.TrackPosition, item.IndexNumber);
                var needDisc = cfg.TagTracks && NeedInt(dzTrack?.DiskNumber, item.ParentIndexNumber);
                var needDeezerId = cfg.TagTracks && NeedProvider(item, "Deezer", dzTrack?.TrackId ?? 0);
                var needIsrc = cfg.TagTracks && NeedProvider(item, "ISRC", dzTrack?.Isrc);
                if (newName is null && explicitWrite is null && !needSongArtists && !needAlbumArtists
                    && genreWrite is null && albumFieldWrite is null && !needYear && !needDate && !needIndex && !needDisc
                    && !needDeezerId && !needIsrc && addTags is null && removeTags is null)
                {
                    continue;
                }

                Queue(new Patch
                {
                    ItemId = item.Id,
                    Item = item,
                    Name = newName,
                    Album = albumFieldWrite,
                    Genres = genreWrite,
                    Artists = needSongArtists ? wantSongArtists : null,
                    AlbumArtists = needAlbumArtists ? wantAlbumArtists : null,
                    Explicit = explicitWrite,
                    AddTags = addTags ?? [],
                    RemoveTags = removeTags ?? [],
                    ProductionYear = needYear ? trackYear : null,
                    PremiereDate = needDate ? trackRelease : null,
                    IndexNumber = needIndex ? dzTrack!.TrackPosition : null,
                    ParentIndexNumber = needDisc ? dzTrack!.DiskNumber : null,
                    DeezerId = needDeezerId ? dzTrack!.TrackId.ToString(CultureInfo.InvariantCulture) : null,
                    Isrc = needIsrc ? dzTrack!.Isrc : null
                });
            }
        }

        progress.Report(50);

        if ((cfg.WritePhotos || cfg.WriteBios || cfg.TagAlbums || cfg.TagTracks) && artists.Count > 0)
        {
            var jobs = artists.Values.Where(a => !SkipMeta(a.Name) && a.Id != Guid.Empty).Select(a =>
            {
                artistDeezer.TryGetValue(a.Name, out var info);
                var needPhoto = cfg.WritePhotos && (force || !a.HasImage(ImageType.Primary));
                var needBio = cfg.WriteBios && (force || string.IsNullOrWhiteSpace(a.Overview));
                var needId = info is { ArtistId: > 0 } && a.GetProviderId("Deezer") != info.ArtistId.ToString(CultureInfo.InvariantCulture);
                return (Artist: a, Info: info, NeedPhoto: needPhoto, NeedBio: needBio, NeedId: needId);
            }).Where(j => j.NeedPhoto || j.NeedBio || j.NeedId).ToList();

            var artistDone = 0;
            await Task.WhenAll(jobs.Select(async job =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var picture = string.Empty;
                    var info = job.Info;
                    if (job.NeedPhoto && Providers.Enabled(cfg.EffectivePhotoProviders, Providers.Deezer))
                    {
                        picture = info?.Picture ?? string.Empty;
                        if (picture.Length == 0)
                        {
                            var found = await _deezer.SearchArtistAsync(job.Artist.Name, cancellationToken).ConfigureAwait(false);
                            if (found is not null)
                            {
                                info = found;
                                picture = found.Picture;
                            }
                        }
                    }

                    BioMatch bio = new();
                    if (job.NeedBio)
                    {
                        bio = await _bios.LookupAsync(job.Artist.Name, cfg.EffectiveBioProviders, cancellationToken).ConfigureAwait(false);
                    }

                    var deezerId = info is { ArtistId: > 0 }
                        ? info.ArtistId.ToString(CultureInfo.InvariantCulture)
                        : null;
                    var writeId = deezerId is not null && job.Artist.GetProviderId("Deezer") != deezerId;
                    if (picture.Length == 0 && bio.Overview.Length == 0 && !writeId)
                    {
                        return;
                    }

                    Queue(new Patch
                    {
                        ItemId = job.Artist.Id,
                        Item = job.Artist,
                        ImageUrl = picture,
                        Overview = bio.Overview.Length > 0 ? bio.Overview : null,
                        DeezerId = writeId ? deezerId : null
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

        if (cfg.WriteLyrics && !lyricsJobs.IsEmpty && Providers.Enabled(cfg.EffectiveLyricProviders, Providers.LrcLib))
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
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PBJ lyrics failed for {Id}", id);
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
        finally
        {
            Titles.ResetStyle();
        }
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

        if (p.Album is not null && item is Audio audio && audio.Album != p.Album)
        {
            audio.Album = p.Album;
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

        if (item is IHasArtist hasArtist && p.Artists is not null)
        {
            hasArtist.Artists = p.Artists;
            dirty = true;
        }

        if (item is IHasAlbumArtist hasAlbumArtist && p.AlbumArtists is not null)
        {
            hasAlbumArtist.AlbumArtists = p.AlbumArtists;
            dirty = true;
        }

        if (p.ProductionYear is not null && item.ProductionYear != p.ProductionYear)
        {
            item.ProductionYear = p.ProductionYear;
            dirty = true;
        }

        if (p.PremiereDate is not null && item.PremiereDate?.Date != p.PremiereDate.Value.Date)
        {
            item.PremiereDate = p.PremiereDate;
            dirty = true;
        }

        if (p.IndexNumber is not null && item.IndexNumber != p.IndexNumber)
        {
            item.IndexNumber = p.IndexNumber;
            dirty = true;
        }

        if (p.ParentIndexNumber is not null && item.ParentIndexNumber != p.ParentIndexNumber)
        {
            item.ParentIndexNumber = p.ParentIndexNumber;
            dirty = true;
        }

        if (p.Studios is not null)
        {
            item.Studios = p.Studios.ToArray();
            dirty = true;
        }

        if (p.DeezerId is not null && item.GetProviderId("Deezer") != p.DeezerId)
        {
            item.SetProviderId("Deezer", p.DeezerId);
            dirty = true;
        }

        if (p.Isrc is not null && !string.Equals(item.GetProviderId("ISRC"), p.Isrc, StringComparison.OrdinalIgnoreCase))
        {
            item.SetProviderId("ISRC", p.Isrc);
            dirty = true;
        }

        if (p.Upc is not null && !string.Equals(item.GetProviderId("UPC"), p.Upc, StringComparison.OrdinalIgnoreCase))
        {
            item.SetProviderId("UPC", p.Upc);
            dirty = true;
        }

        var tags = item.Tags.ToList();
        var tagDirty = false;
        if (p.Explicit is not null)
        {
            var names = Plugin.Instance?.Configuration.EffectiveExplicitTags ?? ["Explicit"];
            tags = tags.Where(t => !names.Any(n => t.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (p.Explicit.Value)
            {
                foreach (var n in names)
                {
                    if (!tags.Any(t => t.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    {
                        tags.Add(n);
                    }
                }
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
            await _library.UpdateItemAsync(item, item.GetParent() ?? item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
                await _library.UpdateItemAsync(item, item.GetParent() ?? item, ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void RememberLyrics(Audio item, ConcurrentDictionary<Guid, LyricsJob> jobs, bool force, PluginConfiguration cfg, IReadOnlyList<string>? extra = null, string? album = null)
    {
        if (!cfg.WriteLyrics || item.Id == Guid.Empty || jobs.ContainsKey(item.Id))
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

    private static bool HasAnyTag(BaseItem item, IReadOnlyList<string> names)
        => names.Any(n => HasTag(item, n));

    private static string? TitlePatch(string current, bool? deezerExplicit, PluginConfiguration cfg)
    {
        if (!cfg.RenameExplicitTitles || deezerExplicit is null)
        {
            return null;
        }

        var desired = Titles.DesiredTitle(current, deezerExplicit.Value);
        return current == desired ? null : desired;
    }

    private static List<string>? GenreWant(IReadOnlyList<string> deezer, IReadOnlyList<string>? current)
    {
        var raw = current ?? [];
        if (deezer.Count > 0)
        {
            return NeedList(deezer, raw) || Genres.NeedsRewrite(raw) ? deezer.ToList() : null;
        }

        if (!Genres.NeedsRewrite(raw))
        {
            return null;
        }

        var cleaned = Genres.PrettyList(raw, 0);
        return cleaned.Count > 0 ? cleaned : null;
    }

    private static bool NeedList(IReadOnlyList<string> want, IReadOnlyList<string> got)
        => want.Count > 0 && !Titles.SameNames(want, got);

    private static bool NeedInt(int? want, int? got)
        => want is > 0 && got != want;

    private static bool NeedDate(DateTime? want, DateTime? got)
        => want is { } d && got?.Date != d.Date;

    private static bool NeedProvider(BaseItem? item, string key, int id)
        => item is not null && id > 0 && item.GetProviderId(key) != id.ToString(CultureInfo.InvariantCulture);

    private static bool NeedProvider(BaseItem? item, string key, string? value)
    {
        var v = value?.Trim() ?? string.Empty;
        return item is not null && v.Length > 0
            && !string.Equals(item.GetProviderId(key), v, StringComparison.OrdinalIgnoreCase);
    }

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

        public List<string>? AlbumArtists { get; init; }

        public bool? Explicit { get; init; }

        public string? Name { get; init; }

        /// <summary>Audio.Album only — kept free of the explicit mark so tracks stay on the album.</summary>
        public string? Album { get; init; }

        public List<string> AddTags { get; init; } = [];

        public List<string> RemoveTags { get; init; } = [];

        public string? Overview { get; init; }

        public string LyricsText { get; init; } = string.Empty;

        public string LyricsFormat { get; init; } = "lrc";

        public string ImageUrl { get; init; } = string.Empty;

        public int? ProductionYear { get; init; }

        public DateTime? PremiereDate { get; init; }

        public int? IndexNumber { get; init; }

        public int? ParentIndexNumber { get; init; }

        public List<string>? Studios { get; init; }

        public string? DeezerId { get; init; }

        public string? Isrc { get; init; }

        public string? Upc { get; init; }

        public bool Empty =>
            Genres is null && Artists is null && AlbumArtists is null && Explicit is null && Name is null && Album is null
            && AddTags.Count == 0 && RemoveTags.Count == 0 && Overview is null
            && LyricsText.Length == 0 && ImageUrl.Length == 0
            && ProductionYear is null && PremiereDate is null && IndexNumber is null && ParentIndexNumber is null
            && Studios is null && DeezerId is null && Isrc is null && Upc is null;

        public Patch Merge(Patch src) => new()
        {
            ItemId = ItemId,
            Item = Item ?? src.Item,
            Genres = src.Genres ?? Genres,
            Artists = src.Artists ?? Artists,
            AlbumArtists = src.AlbumArtists ?? AlbumArtists,
            Explicit = src.Explicit ?? Explicit,
            Name = src.Name ?? Name,
            Album = src.Album ?? Album,
            AddTags = AddTags.Concat(src.AddTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RemoveTags = RemoveTags.Concat(src.RemoveTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Overview = src.Overview ?? Overview,
            LyricsText = src.LyricsText.Length > 0 ? src.LyricsText : LyricsText,
            LyricsFormat = src.LyricsText.Length > 0 ? src.LyricsFormat : LyricsFormat,
            ImageUrl = src.ImageUrl.Length > 0 ? src.ImageUrl : ImageUrl,
            ProductionYear = src.ProductionYear ?? ProductionYear,
            PremiereDate = src.PremiereDate ?? PremiereDate,
            IndexNumber = src.IndexNumber ?? IndexNumber,
            ParentIndexNumber = src.ParentIndexNumber ?? ParentIndexNumber,
            Studios = src.Studios ?? Studios,
            DeezerId = src.DeezerId ?? DeezerId,
            Isrc = src.Isrc ?? Isrc,
            Upc = src.Upc ?? Upc
        };
    }
}
