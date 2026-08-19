package engine

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"

	"github.com/TidBits16/peanut-butter-jelly/internal/audio"
	"github.com/TidBits16/peanut-butter-jelly/internal/bios"
	"github.com/TidBits16/peanut-butter-jelly/internal/cache"
	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"github.com/TidBits16/peanut-butter-jelly/internal/deezer"
	"github.com/TidBits16/peanut-butter-jelly/internal/genres"
	"github.com/TidBits16/peanut-butter-jelly/internal/jellyfin"
	"github.com/TidBits16/peanut-butter-jelly/internal/lrclib"
	"github.com/TidBits16/peanut-butter-jelly/internal/playlists"
	"github.com/TidBits16/peanut-butter-jelly/internal/titles"
	"github.com/TidBits16/peanut-butter-jelly/internal/ui"
)

var skipArtistMeta = map[string]struct{}{
	"various artists": {}, "various": {}, "unknown artist": {}, "unknown": {},
}

type Patch struct {
	ItemID        string
	Path          string
	Genres        []string
	SetGenres     bool
	Artists       []string
	SetArtists    bool
	AlbumArtist   *string
	Explicit      *bool
	Name          *string
	AddTags       []string
	RemoveTags    []string
	FilePath      string
	FileTitle     *string
	FileGenres    []string
	SetFileGenres bool
	FileAdvisory  *string
	Detail        string
	LyricsText    string
	LyricsName    string
	ImageURL      string
	Overview      *string
	JFItem        jellyfin.Item
}

func (p Patch) metadataEmpty() bool {
	return !p.SetGenres && !p.SetArtists && p.AlbumArtist == nil && p.Explicit == nil &&
		p.Name == nil && len(p.AddTags) == 0 && len(p.RemoveTags) == 0 && p.Overview == nil
}

func (p Patch) empty() bool {
	return p.metadataEmpty() && p.ImageURL == "" && p.FileTitle == nil && !p.SetFileGenres &&
		p.FileAdvisory == nil && p.LyricsText == ""
}

func shouldWrite(present, differs, force bool) bool {
	return (force && differs) || (!force && !present)
}

func skipMeta(name string) bool {
	key := strings.ToLower(strings.TrimSpace(name))
	if key == "" {
		return true
	}
	if _, ok := skipArtistMeta[key]; ok {
		return true
	}
	return strings.HasPrefix(key, "[") && strings.HasSuffix(key, "]")
}

func albumArtistOf(item jellyfin.Item) string {
	if a := item.AlbumArtist(); a != "" {
		return a
	}
	arts := item.Artists()
	if len(arts) > 0 {
		return arts[0]
	}
	return ""
}

func albumLabel(item jellyfin.Item) string {
	bits := []string{}
	if a := albumArtistOf(item); a != "" {
		bits = append(bits, a)
	}
	if a := item.Album(); a != "" {
		bits = append(bits, a)
	}
	if len(bits) == 0 {
		return "unknown album"
	}
	return strings.Join(bits, " — ")
}

func equalStr(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func uniq(in []string) []string {
	seen := map[string]struct{}{}
	out := []string{}
	for _, s := range in {
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}

func mergePatch(dst *Patch, src Patch) {
	if src.SetGenres {
		dst.Genres, dst.SetGenres = src.Genres, true
	}
	if src.SetArtists {
		dst.Artists, dst.SetArtists = src.Artists, true
	}
	if src.AlbumArtist != nil {
		dst.AlbumArtist = src.AlbumArtist
	}
	if src.Explicit != nil {
		dst.Explicit = src.Explicit
	}
	if src.Name != nil {
		dst.Name = src.Name
	}
	if src.FilePath != "" {
		dst.FilePath = src.FilePath
	}
	if src.FileTitle != nil {
		dst.FileTitle = src.FileTitle
	}
	if src.SetFileGenres {
		dst.FileGenres, dst.SetFileGenres = src.FileGenres, true
	}
	if src.FileAdvisory != nil {
		dst.FileAdvisory = src.FileAdvisory
	}
	if src.LyricsText != "" {
		dst.LyricsText, dst.LyricsName = src.LyricsText, src.LyricsName
	}
	if src.ImageURL != "" {
		dst.ImageURL = src.ImageURL
	}
	if src.Overview != nil {
		dst.Overview = src.Overview
	}
	if src.JFItem != nil && dst.JFItem == nil {
		dst.JFItem = src.JFItem
	}
	dst.AddTags = uniq(append(dst.AddTags, src.AddTags...))
	dst.RemoveTags = uniq(append(dst.RemoveTags, src.RemoveTags...))
	if src.Detail != "" {
		if dst.Detail != "" {
			dst.Detail += " · " + src.Detail
		} else {
			dst.Detail = src.Detail
		}
	}
}

func boolPtr(v bool) *bool { return &v }
func strPtr(v string) *string { return &v }

type applyResult struct {
	jellyfin, file, lyrics, image bool
	fileErr, lyricsErr, imageErr  error
}

func applyPatch(jf *jellyfin.Client, dz *deezer.Client, uid string, p Patch) (applyResult, error) {
	var r applyResult
	if !p.metadataEmpty() {
		ok, err := jf.UpdateItem(uid, p.ItemID, jellyfin.Patch{
			Genres: p.Genres, SetGenres: p.SetGenres,
			Artists: p.Artists, SetArtists: p.SetArtists,
			AlbumArtist: p.AlbumArtist, Explicit: p.Explicit, Name: p.Name,
			AddTags: p.AddTags, RemoveTags: p.RemoveTags, Overview: p.Overview, Item: p.JFItem,
		})
		if err != nil {
			return r, err
		}
		r.jellyfin = ok
	}
	if p.FilePath != "" && (p.FileTitle != nil || p.SetFileGenres || p.FileAdvisory != nil) {
		var gs []string
		if p.SetFileGenres {
			gs = p.FileGenres
		} else {
			gs = nil
		}
		ok, err := audio.WriteFileTags(p.FilePath, p.FileTitle, gs, p.FileAdvisory)
		if err != nil {
			r.fileErr = err
			if !r.jellyfin {
				return r, err
			}
		} else {
			r.file = ok
		}
	}
	if p.LyricsText != "" && p.LyricsName != "" {
		ok, err := jf.UploadLyrics(p.ItemID, p.LyricsName, p.LyricsText)
		if err != nil {
			r.lyricsErr = err
			if !r.jellyfin && !r.file {
				return r, err
			}
		} else {
			r.lyrics = ok
		}
	}
	if p.ImageURL != "" {
		data, mime, err := dz.DownloadImage(p.ImageURL)
		if err != nil {
			r.imageErr = err
			if !r.jellyfin && !r.file && !r.lyrics {
				return r, err
			}
		} else {
			ok, err := jf.UploadImage(p.ItemID, data, mime)
			if err != nil {
				r.imageErr = err
				if !r.jellyfin && !r.file && !r.lyrics {
					return r, err
				}
			} else {
				r.image = ok
			}
		}
	}
	return r, nil
}

type albumKey struct{ Artist, Album string }

func Run(cfg *config.Config) error {
	force := cfg.Force
	startFrom := cfg.StartFrom
	if startFrom < 1 {
		startFrom = 1
	}
	workers := cfg.Workers
	if workers < 1 {
		workers = 1
	}
	musicRoot := cfg.MusicDir
	if musicRoot == "" {
		musicRoot = audio.DefaultMusicDir()
	}
	abs, err := filepath.Abs(musicRoot)
	if err == nil {
		musicRoot = abs
	}

	ui.PrintBanner(cfg.Apply, force, musicRoot, workers, startFrom)

	store := cache.New(cfg.Root)
	jf := jellyfin.New(cfg)
	dz := deezer.New(store)
	lrc := lrclib.New(cfg, store)
	bio := bios.New(cfg, store)

	fmt.Fprintf(os.Stderr, "  loading Jellyfin library…\n")
	uid, err := jf.ResolveUserID(cfg.User)
	if err != nil {
		return err
	}
	tracks, err := jf.Tracks(uid)
	if err != nil {
		return err
	}
	albumsMap, err := jf.Albums(uid)
	if err != nil {
		return err
	}
	artistsMap, err := jf.Artists()
	if err != nil {
		return err
	}
	jfByID := map[string]jellyfin.Item{}
	for _, it := range tracks {
		jfByID[it.ID()] = it
	}
	for id, it := range albumsMap {
		jfByID[id] = it
	}
	for _, it := range artistsMap {
		if it.ID() != "" {
			if _, ok := jfByID[it.ID()]; !ok {
				jfByID[it.ID()] = it
			}
		}
	}
	var nameIndex map[string][]string
	if st, err := os.Stat(musicRoot); err == nil && st.IsDir() {
		nameIndex = audio.BuildNameIndex(musicRoot)
	} else {
		nameIndex = map[string][]string{}
		ui.Callout("No music folder at "+musicRoot+". Jellyfin will still update. Pass --dir to the library.", "warn")
	}

	grouped := map[albumKey][]jellyfin.Item{}
	for _, item := range tracks {
		k := albumKey{albumArtistOf(item), titles.StripMark(strings.TrimSpace(item.Album()))}
		grouped[k] = append(grouped[k], item)
	}
	keys := make([]albumKey, 0, len(grouped))
	for k := range grouped {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool {
		ai, aj := strings.ToLower(keys[i].Artist), strings.ToLower(keys[j].Artist)
		if ai != aj {
			return ai < aj
		}
		return strings.ToLower(keys[i].Album) < strings.ToLower(keys[j].Album)
	})
	totalAlbums := len(keys)
	if startFrom > 1 {
		if startFrom-1 < len(keys) {
			keys = keys[startFrom-1:]
		} else {
			keys = nil
		}
	}
	ui.PrintLibrary(cfg.User, len(tracks), totalAlbums, len(artistsMap))
	ui.Section("plan", "misses print live · full manual list waits at the end")

	patches := map[string]*Patch{}
	var patchMu sync.Mutex
	type lyricsJob struct {
		path, title, album, jfPath string
		artists                    []string
		duration                   *int
	}
	lyricsJobs := map[string]lyricsJob{}
	artistPhotos := map[string][2]string{}

	failed, unchanged, nomatchAlbums, albumsMatched := 0, 0, 0, 0
	explicitYes, explicitNo, explicitUnknown, albumExplicit := 0, 0, 0, 0
	nomatchTracks, filesMissing := 0, 0
	lyricsSkipped, lyricsAlready := 0, 0
	lyricsSynced, lyricsPlain, lyricsInstrumental, lyricsNomatch := 0, 0, 0, 0
	artistPhotosQueued, artistPhotosNomatch, artistPhotosSkip := 0, 0, 0
	artistBiosQueued, artistBiosNomatch, artistBiosSkip := 0, 0, 0
	gapAlbums := [][3]string{}
	gapArtists := map[string][]string{}
	gapLyrics := map[string][]string{}
	gapFails := [][2]string{}

	queue := func(p Patch) {
		if p.empty() {
			return
		}
		patchMu.Lock()
		defer patchMu.Unlock()
		if existing, ok := patches[p.ItemID]; ok {
			mergePatch(existing, p)
			return
		}
		cp := p
		patches[p.ItemID] = &cp
	}

	rememberLyrics := func(item jellyfin.Item, path string, extra []string, album string) {
		id := item.ID()
		if id == "" {
			return
		}
		if _, ok := lyricsJobs[id]; ok {
			return
		}
		if !force && item.HasLyrics() {
			lyricsAlready++
			return
		}
		title := titles.StripMark(item.Name())
		arts := lrclib.QueryArtists(item, extra)
		if title == "" || len(arts) == 0 {
			lyricsSkipped++
			key := titles.StripMark(album)
			if key == "" {
				key = "unknown album"
			}
			gapLyrics[key] = append(gapLyrics[key], or(title, "(no title)"))
			return
		}
		alb := titles.StripMark(album)
		if alb == "" {
			alb = titles.StripMark(item.Album())
		}
		lyricsJobs[id] = lyricsJob{path: path, title: title, album: alb, jfPath: item.Path(), artists: arts, duration: item.DurationSeconds()}
	}

	tagNomatch := func(items []jellyfin.Item, fake string) {
		nomatchTracks += len(items)
		for _, item := range items {
			p := filepath.Join(fake, or(titles.StripMark(item.Name()), "track"))
			rememberLyrics(item, p, nil, item.Album())
			if !item.HasTag(config.NoMatchTag) {
				queue(Patch{ItemID: item.ID(), Path: p, AddTags: []string{config.NoMatchTag}, Detail: config.NoMatchTag, JFItem: item})
			}
		}
	}

	type dzResult struct {
		match deezer.AlbumMatch
		err   error
	}
	lookups := map[albumKey]dzResult{}
	prog := ui.NewProgress("deezer", len(keys))
	sem := make(chan struct{}, workers)
	var wg sync.WaitGroup
	var luMu sync.Mutex
	for _, k := range keys {
		k, sample := k, ""
		tr := grouped[k]
		if len(tr) > 0 {
			sample = titles.StripMark(tr[0].Name())
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			m := dz.LookupAlbum(k.Artist, k.Album, sample)
			luMu.Lock()
			lookups[k] = dzResult{match: m}
			luMu.Unlock()
			prog.Add(1)
		}()
	}
	wg.Wait()
	prog.Done()

	for albumN, k := range keys {
		n := albumN + startFrom
		_ = n
		tr := grouped[k]
		label := albumLabel(tr[0])
		fake := label
		res := lookups[k]
		if res.err != nil {
			failed++
			tagNomatch(tr, fake)
			gapFails = append(gapFails, [2]string{label, res.err.Error()})
			gapAlbums = append(gapAlbums, [3]string{label, "Deezer error", fmt.Sprintf("%d", len(tr))})
			ui.Event("fail", fake, res.err.Error())
			continue
		}
		match := res.match
		if match.AlbumID == 0 {
			nomatchAlbums++
			tagNomatch(tr, fake)
			gapAlbums = append(gapAlbums, [3]string{label, "Deezer no match", fmt.Sprintf("%d", len(tr))})
			ui.Event("miss", fake, fmt.Sprintf("%d tracks", len(tr)))
			continue
		}
		albumsMatched++
		for _, info := range match.ArtistInfos {
			if info.Picture != "" && !skipMeta(info.Name) {
				if _, ok := artistPhotos[strings.ToLower(info.Name)]; !ok {
					artistPhotos[strings.ToLower(info.Name)] = [2]string{info.Name, info.Picture}
				}
			}
		}
		albumIDs := map[string]struct{}{}
		for _, t := range tr {
			if t.AlbumID() != "" {
				albumIDs[t.AlbumID()] = struct{}{}
			}
		}
		dzAlbumArtist := match.AlbumArtist
		wantAlbumArtists := match.Artists
		if len(wantAlbumArtists) == 0 && dzAlbumArtist != "" {
			wantAlbumArtists = []string{dzAlbumArtist}
		}
		needAlbumGenres, needAlbumArtists, needAlbumMark, needAlbumUnmark, needAlbumUntag := false, false, false, false, false
		albumNames := map[string]string{}
		albumBits := []string{}
		for albumID := range albumIDs {
			albumItem := albumsMap[albumID]
			albumName := k.Album
			if albumItem != nil {
				if n := albumItem.Name(); n != "" {
					albumName = n
				}
			}
			if albumName == "" && len(tr) > 0 {
				albumName = tr[0].Album()
			}
			albumNames[albumID] = albumName
			marked := titles.HasExplicitMark(albumName)
			tagged := albumItem != nil && albumItem.HasTag("Explicit")
			if tagged {
				needAlbumUntag = true
			}
			if match.Explicit != nil && *match.Explicit && !marked {
				needAlbumMark = true
			}
			if match.Explicit != nil && !*match.Explicit && marked {
				needAlbumUnmark = true
			}
			if albumItem == nil {
				needAlbumGenres = needAlbumGenres || len(match.Genres) > 0
				needAlbumArtists = needAlbumArtists || len(wantAlbumArtists) > 0
				continue
			}
			gotGenres := genres.PrettyList(albumItem.Genres(), 0)
			if len(match.Genres) > 0 && shouldWrite(len(gotGenres) > 0, !equalStr(gotGenres, match.Genres), force) {
				needAlbumGenres = true
			}
			gotAlbumArtists := albumItem.AlbumArtistNames()
			gotArtists := albumItem.Artists()
			gotAlbumArtist := albumItem.AlbumArtist()
			if len(wantAlbumArtists) > 0 && shouldWrite(
				len(gotArtists) > 0 || len(gotAlbumArtists) > 0 || gotAlbumArtist != "",
				!equalStr(gotArtists, wantAlbumArtists) || gotAlbumArtist != dzAlbumArtist || !equalStr(gotAlbumArtists, wantAlbumArtists),
				force,
			) {
				needAlbumArtists = true
			}
		}
		if needAlbumGenres {
			albumBits = append(albumBits, "genres → "+strings.Join(match.Genres, ", "))
		}
		if needAlbumArtists {
			aa := dzAlbumArtist
			if aa == "" {
				aa = strings.Join(wantAlbumArtists, ", ")
			}
			albumBits = append(albumBits, "artists → "+aa)
		}
		if needAlbumMark {
			albumBits = append(albumBits, "🅴")
			albumExplicit++
		}
		if needAlbumUntag {
			albumBits = append(albumBits, "clear album Explicit tag")
		}
		if needAlbumUnmark {
			albumBits = append(albumBits, "clear album 🅴")
		}
		if len(albumBits) > 0 {
			for albumID := range albumIDs {
				albumTitle := albumNames[albumID]
				if albumTitle == "" {
					albumTitle = k.Album
				}
				var nameWrite *string
				if needAlbumMark {
					nameWrite = strPtr(titles.DesiredTitle(albumTitle, true))
				} else if needAlbumUnmark {
					nameWrite = strPtr(titles.DesiredTitle(albumTitle, false))
				}
				p := Patch{ItemID: albumID, Path: fake, Name: nameWrite, Detail: strings.Join(albumBits, " · "), JFItem: albumsMap[albumID]}
				if needAlbumGenres {
					p.Genres, p.SetGenres = match.Genres, true
				}
				if needAlbumArtists {
					p.AlbumArtist = nil
					if dzAlbumArtist != "" {
						p.AlbumArtist = &dzAlbumArtist
					}
					p.Artists, p.SetArtists = wantAlbumArtists, true
				}
				if needAlbumUntag {
					p.Explicit = boolPtr(false)
				}
				queue(p)
			}
		}

		stamped, artistWrites, fileWrites := 0, 0, 0
		for _, item := range tr {
			name := item.Name()
			dzTrack := deezer.MatchTrack(name, match.Tracks)
			tagged := item.HasTag("Explicit")
			marked := titles.HasExplicitMark(name)
			var addTags, removeTags []string
			if dzTrack == nil {
				nomatchTracks++
				explicitUnknown++
				if !item.HasTag(config.NoMatchTag) {
					addTags = append(addTags, config.NoMatchTag)
				}
			} else {
				if item.HasTag(config.NoMatchTag) {
					removeTags = append(removeTags, config.NoMatchTag)
				}
				if dzTrack.Explicit != nil && *dzTrack.Explicit {
					explicitYes++
				} else if dzTrack.Explicit != nil && !*dzTrack.Explicit {
					explicitNo++
				} else {
					explicitUnknown++
				}
			}
			wantE := dzTrack != nil && dzTrack.Explicit != nil && *dzTrack.Explicit
			clearE := dzTrack != nil && dzTrack.Explicit != nil && !*dzTrack.Explicit && (tagged || marked)
			needE := wantE && (!tagged || !marked)
			var wantArtists []string
			if dzTrack != nil && len(dzTrack.Artists) > 0 {
				wantArtists = dzTrack.Artists
			} else if dzAlbumArtist != "" {
				wantArtists = []string{dzAlbumArtist}
			}
			rememberLyrics(item, filepath.Join(fake, or(titles.StripMark(name), "track")), wantArtists, k.Album)
			needArtists := len(wantArtists) > 0 && shouldWrite(
				len(item.Artists()) > 0 || item.AlbumArtist() != "",
				!equalStr(item.Artists(), wantArtists) || item.AlbumArtist() != dzAlbumArtist,
				force,
			)
			filePath := audio.ResolveLocalPath(item.Path(), musicRoot, nameIndex)
			var fileTitle *string
			var fileGenres []string
			setFileGenres := false
			var fileAdvisory *string
			if wantE || clearE || len(match.Genres) > 0 {
				if filePath == "" {
					filesMissing++
				} else if tags, ok := audio.ReadFileTags(filePath); !ok {
					filesMissing++
				} else {
					if wantE {
						wantTitle := titles.DesiredTitle(name, true)
						if tags.Title != wantTitle {
							fileTitle = &wantTitle
						}
						if tags.Advisory != audio.AdvisoryExplicit {
							fileAdvisory = strPtr(audio.AdvisoryExplicit)
						}
					} else if clearE {
						wantTitle := titles.DesiredTitle(name, false)
						if tags.Title != wantTitle {
							fileTitle = &wantTitle
						}
						if tags.Advisory == audio.AdvisoryExplicit {
							fileAdvisory = strPtr("0")
						}
					}
					if len(match.Genres) > 0 && shouldWrite(len(tags.Genres) > 0, !equalStr(tags.Genres, match.Genres), force) {
						fileGenres = match.Genres
						setFileGenres = true
					}
				}
			}
			if !needE && !clearE && !needArtists && len(addTags) == 0 && len(removeTags) == 0 && fileTitle == nil && !setFileGenres && fileAdvisory == nil {
				unchanged++
				continue
			}
			var newName *string
			if needE {
				newName = strPtr(titles.DesiredTitle(name, true))
			} else if clearE && marked {
				newName = strPtr(titles.DesiredTitle(name, false))
			}
			details := []string{}
			if needE {
				details = append(details, "Explicit + 🅴")
				stamped++
			} else if clearE {
				details = append(details, "clear inherited Explicit")
			}
			if needArtists {
				details = append(details, "artists → "+strings.Join(wantArtists, ", "))
				artistWrites++
			}
			if fileTitle != nil || setFileGenres || fileAdvisory != nil {
				bits := []string{}
				if fileTitle != nil {
					if wantE {
						bits = append(bits, "title 🅴")
					} else {
						bits = append(bits, "title")
					}
				}
				if fileAdvisory != nil {
					bits = append(bits, "itunes")
				}
				if setFileGenres {
					bits = append(bits, "genre")
				}
				details = append(details, "file "+strings.Join(bits, "+"))
				fileWrites++
			}
			for _, t := range addTags {
				if t == config.NoMatchTag {
					details = append(details, config.NoMatchTag)
				}
			}
			for _, t := range removeTags {
				if t == config.NoMatchTag {
					details = append(details, "clear "+config.NoMatchTag)
				}
			}
			p := Patch{
				ItemID: item.ID(), Path: or(filePath, filepath.Join(fake, or(titles.StripMark(name), "track"))),
				AddTags: addTags, RemoveTags: removeTags, FilePath: filePath, FileTitle: fileTitle,
				FileGenres: fileGenres, SetFileGenres: setFileGenres, FileAdvisory: fileAdvisory,
				Name: newName, Detail: strings.Join(details, " · "), JFItem: item,
			}
			if needArtists {
				p.Artists, p.SetArtists = wantArtists, true
				if dzAlbumArtist != "" {
					p.AlbumArtist = &dzAlbumArtist
				}
			}
			if needE {
				p.Explicit = boolPtr(true)
			} else if clearE && tagged {
				p.Explicit = boolPtr(false)
			}
			queue(p)
		}
		if len(albumBits) > 0 || stamped > 0 || artistWrites > 0 || fileWrites > 0 {
			extra := ""
			if stamped > 0 {
				extra += fmt.Sprintf(" · explicit %d", stamped)
			}
			if fileWrites > 0 {
				extra += fmt.Sprintf(" · files %d", fileWrites)
			}
			detail := strings.Join(albumBits, " · ")
			if detail == "" {
				detail = "ok"
			}
			ui.Event("dry", fake, detail+extra+" · "+match.Source)
		}
	}

	if len(artistsMap) > 0 {
		ui.Section("artists", "only misses print · photo + bio list waits at the end")
		type ajob struct {
			id, name     string
			needPhoto    bool
			needBio      bool
			picture      string
		}
		var jobs []ajob
		for _, jfArtist := range artistsMap {
			name := strings.TrimSpace(jfArtist.Name())
			if skipMeta(name) || jfArtist.ID() == "" {
				continue
			}
			needPhoto := force || !jfArtist.HasPrimaryImage()
			needBio := force || jfArtist.Overview() == ""
			if !needPhoto {
				artistPhotosSkip++
			}
			if !needBio {
				artistBiosSkip++
			}
			if !needPhoto && !needBio {
				continue
			}
			picture := ""
			if needPhoto {
				picture = artistPhotos[strings.ToLower(name)][1]
			}
			jobs = append(jobs, ajob{jfArtist.ID(), name, needPhoto, needBio, picture})
		}
		prog = ui.NewProgress("artists", len(jobs))
		type ares struct {
			job ajob
			bio bios.Match
			err error
		}
		out := make([]ares, len(jobs))
		var awg sync.WaitGroup
		for i, job := range jobs {
			i, job := i, job
			awg.Add(1)
			go func() {
				defer awg.Done()
				sem <- struct{}{}
				defer func() { <-sem }()
				picture := job.picture
				var err error
				if job.needPhoto && picture == "" {
					if a := dz.SearchArtist(job.name); a != nil {
						picture = a.Picture
					}
				}
				var b bios.Match
				if job.needBio {
					b = bio.Lookup(job.name)
				}
				job.picture = picture
				out[i] = ares{job: job, bio: b, err: err}
				prog.Add(1)
			}()
		}
		awg.Wait()
		prog.Done()
		for _, r := range out {
			job := r.job
			if r.err != nil {
				failed++
				gapFails = append(gapFails, [2]string{job.name, r.err.Error()})
				ui.Event("fail", job.name, r.err.Error())
			}
			missing := []string{}
			bits := []string{}
			if job.needPhoto {
				if job.picture != "" {
					artistPhotosQueued++
					if force {
						bits = append(bits, "photo overwrite")
					} else {
						bits = append(bits, "photo fill")
					}
				} else {
					artistPhotosNomatch++
					missing = append(missing, "photo")
				}
			}
			if job.needBio {
				if r.bio.Overview != "" {
					artistBiosQueued++
					bits = append(bits, "bio · "+r.bio.Source)
				} else {
					artistBiosNomatch++
					missing = append(missing, "bio")
				}
			}
			if len(missing) > 0 {
				gapArtists[job.name] = missing
				ui.Event("miss", job.name, strings.Join(missing, " "))
			}
			if len(bits) == 0 {
				continue
			}
			p := Patch{ItemID: job.id, Path: job.name, Detail: strings.Join(bits, " · "), JFItem: artistsMap[strings.ToLower(job.name)]}
			if job.picture != "" {
				p.ImageURL = job.picture
			}
			if r.bio.Overview != "" {
				p.Overview = &r.bio.Overview
			}
			queue(p)
		}
	}

	if len(lyricsJobs) > 0 {
		ui.Section("lyrics", "LRCLIB · unmatched tracks collect into the manual list")
		ids := make([]string, 0, len(lyricsJobs))
		for id := range lyricsJobs {
			ids = append(ids, id)
		}
		prog = ui.NewProgress("lrclib", len(ids))
		type lres struct {
			id    string
			job   lyricsJob
			match lrclib.Match
			err   error
		}
		lout := make([]lres, len(ids))
		var lwg sync.WaitGroup
		for i, id := range ids {
			i, id, job := i, id, lyricsJobs[id]
			lwg.Add(1)
			go func() {
				defer lwg.Done()
				sem <- struct{}{}
				defer func() { <-sem }()
				m := lrc.LookupArtists(job.title, job.artists, job.album, job.duration)
				lout[i] = lres{id: id, job: job, match: m}
				prog.Add(1)
			}()
		}
		lwg.Wait()
		prog.Done()
		for _, r := range lout {
			if r.err != nil {
				failed++
				gapFails = append(gapFails, [2]string{strings.Trim(r.job.album+" — "+r.job.title, " —"), r.err.Error()})
				ui.Event("fail", r.job.path, r.err.Error())
				continue
			}
			m := r.match
			if m.Instrumental && m.Synced == "" && m.Plain == "" {
				lyricsInstrumental++
				continue
			}
			kind, ext, text := "", "", ""
			if m.Synced != "" {
				kind, ext, text = "synced", ".lrc", m.Synced
				lyricsSynced++
			} else if m.Plain != "" {
				kind, ext, text = "plain", ".txt", m.Plain
				lyricsPlain++
			} else {
				lyricsNomatch++
				alb := r.job.album
				if alb == "" {
					alb = "unknown album"
				}
				gapLyrics[alb] = append(gapLyrics[alb], r.job.title)
				continue
			}
			queue(Patch{
				ItemID: r.id, Path: r.job.path, LyricsText: text,
				LyricsName: lrclib.LyricsFilename(r.job.jfPath, ext),
				Detail:     "lyrics " + kind + " · " + m.Source, JFItem: jfByID[r.id],
			})
		}
	}

	pending := len(patches)
	fmt.Fprintf(os.Stderr, "\n  %s  %s  %s  %s\n",
		ui.Badge(fmt.Sprintf("%d WRITES", pending), map[bool]string{true: "apply", false: "dry"}[cfg.Apply]),
		ui.Badge(fmt.Sprintf("%d album misses", nomatchAlbums), map[bool]string{true: "miss", false: "off"}[nomatchAlbums > 0]),
		ui.Badge(fmt.Sprintf("%d artist misses", len(gapArtists)), map[bool]string{true: "miss", false: "off"}[len(gapArtists) > 0]),
		ui.Badge(fmt.Sprintf("%d lyric misses", lyricsNomatch), map[bool]string{true: "miss", false: "off"}[lyricsNomatch > 0]),
	)

	updated, skipped, filesUpdated, filesUnwritable := 0, 0, 0, 0
	lyricsUploaded, photosUploaded, biosUploaded := 0, 0, 0
	if cfg.Apply && pending > 0 {
		ui.Section("apply", fmt.Sprintf("%d parallel Jellyfin metadata + lyrics + photos + bios + file writes", workers))
		list := make([]*Patch, 0, pending)
		for _, p := range patches {
			list = append(list, p)
		}
		prog = ui.NewProgress("jellyfin", len(list))
		var awg sync.WaitGroup
		var statMu sync.Mutex
		for _, p := range list {
			p := p
			awg.Add(1)
			go func() {
				defer awg.Done()
				sem <- struct{}{}
				defer func() { <-sem }()
				result, err := applyPatch(jf, dz, uid, *p)
				statMu.Lock()
				defer statMu.Unlock()
				if err != nil {
					if os.IsPermission(err) {
						filesUnwritable++
					} else if os.IsNotExist(err) {
						filesMissing++
					} else {
						failed++
					}
					gapFails = append(gapFails, [2]string{or(p.FilePath, p.Path), err.Error()})
					ui.Event("fail", or(p.FilePath, p.Path), err.Error())
					prog.Add(1)
					return
				}
				if result.jellyfin || result.file || result.lyrics || result.image {
					updated++
				} else {
					skipped++
				}
				if result.file {
					filesUpdated++
				}
				if result.lyrics {
					lyricsUploaded++
				}
				if result.image {
					photosUploaded++
				}
				if result.jellyfin && p.Overview != nil {
					biosUploaded++
				}
				if result.fileErr != nil {
					if os.IsPermission(result.fileErr) {
						filesUnwritable++
					} else if os.IsNotExist(result.fileErr) {
						filesMissing++
					} else {
						failed++
					}
					ui.Event("fail", or(p.FilePath, p.Path), result.fileErr.Error())
				}
				if result.lyricsErr != nil {
					failed++
					gapFails = append(gapFails, [2]string{p.Path, result.lyricsErr.Error()})
					ui.Event("fail", p.Path, result.lyricsErr.Error())
				}
				if result.imageErr != nil {
					failed++
					gapFails = append(gapFails, [2]string{p.Path, result.imageErr.Error()})
					ui.Event("fail", p.Path, result.imageErr.Error())
				}
				prog.Add(1)
			}()
		}
		awg.Wait()
		prog.Done()
	}

	ui.Section("playlists", "keep membership pointed at the current files")
	playlistRewritten, playlistOK, playlistTracks, playlistMissing := 0, 0, 0, 0
	plans, states, err := playlists.PlanRepair(jf, uid, tracks, cfg.Root)
	if err != nil {
		failed++
		gapFails = append(gapFails, [2]string{"playlists", err.Error()})
		ui.Callout("Playlist repair skipped: "+err.Error(), "bad")
	} else {
		_ = playlists.SaveSnapshot(cfg.Root, states)
		for _, p := range plans {
			playlistTracks += len(p.DesiredIDs)
			playlistMissing += p.Missing
		}
		anyWrite := false
		for _, plan := range plans {
			if !plan.NeedsWrite() {
				playlistOK++
				continue
			}
			anyWrite = true
			detail := fmt.Sprintf("%d → %d tracks (%s)", len(plan.LiveIDs), len(plan.DesiredIDs), plan.Source)
			if plan.Missing > 0 {
				detail += fmt.Sprintf(" · %d unmatched", plan.Missing)
			}
			if cfg.Apply {
				if err := playlists.Apply(jf, uid, plan); err != nil {
					failed++
					gapFails = append(gapFails, [2]string{plan.Name, err.Error()})
					ui.Event("fail", plan.Name, err.Error())
				} else {
					playlistRewritten++
					ui.Event("ok", plan.Name, detail)
				}
			} else {
				playlistRewritten++
				ui.Event("dry", plan.Name, detail)
			}
		}
		if !anyWrite {
			ui.Callout("Playlists already match the current library.", "ok")
		}
	}

	ui.PrintGap(gapAlbums, gapArtists, gapLyrics, gapFails)
	dzHTTP, dzHits := dz.Stats()
	lrcHTTP, lrcHits := lrc.Stats()
	bioHTTP, bioHits := bio.Stats()
	stats := map[string]int{
		"jellyfin_tracks": len(tracks), "albums": totalAlbums, "albums_matched": albumsMatched,
		"unchanged": unchanged, "nomatch": nomatchAlbums, "nomatch_tracks": nomatchTracks,
		"failed": failed, "explicit_yes": explicitYes, "explicit_no": explicitNo,
		"explicit_unknown": explicitUnknown, "album_explicit": albumExplicit,
		"files_missing": filesMissing, "lyrics_synced": lyricsSynced, "lyrics_plain": lyricsPlain,
		"lyrics_instrumental": lyricsInstrumental, "lyrics_nomatch": lyricsNomatch,
		"lyrics_skipped": lyricsSkipped, "already_has": lyricsAlready,
		"artist_photos": artistPhotosQueued, "artist_photos_skip": artistPhotosSkip,
		"artist_photos_nomatch": artistPhotosNomatch, "artist_bios": artistBiosQueued,
		"artist_bios_skip": artistBiosSkip, "artist_bios_nomatch": artistBiosNomatch,
		"playlists": playlistOK + playlistRewritten, "playlists_ok": playlistOK,
		"playlists_rewritten": playlistRewritten, "playlist_tracks": playlistTracks,
		"playlist_missing": playlistMissing, "deezer_http": dzHTTP, "deezer_cache": dzHits,
		"lrclib_http": lrcHTTP, "lrclib_cache": lrcHits, "bio_http": bioHTTP, "bio_cache": bioHits,
	}
	if !cfg.Apply {
		stats["pending"] = pending
	} else {
		stats["updated"] = updated
		stats["skipped"] = skipped
		stats["files_updated"] = filesUpdated
		stats["files_unwritable"] = filesUnwritable
		stats["lyrics_uploaded"] = lyricsUploaded
		stats["artist_photos_uploaded"] = photosUploaded
		stats["artist_bios_uploaded"] = biosUploaded
	}
	ui.PrintSummary(stats)
	return nil
}

func or(a, b string) string {
	if a != "" {
		return a
	}
	return b
}
