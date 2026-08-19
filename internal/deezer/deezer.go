package deezer

import (
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/TidBits16/peanut-butter-jelly/internal/cache"
	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"github.com/TidBits16/peanut-butter-jelly/internal/genres"
	"github.com/TidBits16/peanut-butter-jelly/internal/httpx"
	"github.com/TidBits16/peanut-butter-jelly/internal/titles"
)

type Track struct {
	Title    string
	Explicit *bool
	Artists  []string
}

type ArtistInfo struct {
	Name     string
	ArtistID int
	Picture  string
}

type AlbumMatch struct {
	Genres      []string
	Source      string
	AlbumID     int
	Title       string
	AlbumArtist string
	Artists     []string
	ArtistInfos []ArtistInfo
	Tracks      []Track
	Explicit    *bool
}

type Client struct {
	http *httpx.Client
	mu   sync.Mutex
	byKA map[[2]string]AlbumMatch
	byID map[int]AlbumMatch
	arts map[string]*ArtistInfo
}

func New(store *cache.Store) *Client {
	return &Client{
		http: httpx.New(store, map[string]string{
			"User-Agent": "peanut-butter-jelly/" + config.Version + " (deezer; jellyfin)",
			"Accept":     "application/json",
		}, 120*time.Millisecond),
		byKA: map[[2]string]AlbumMatch{},
		byID: map[int]AlbumMatch{},
		arts: map[string]*ArtistInfo{},
	}
}

func (c *Client) Stats() (httpN, hits int) { return c.http.HTTPN, c.http.Hits }

func quote(s string) string { return strings.ReplaceAll(strings.TrimSpace(s), `"`, "") }

func albumVariants(album string) []string {
	var out []string
	cur := strings.TrimSpace(album)
	for cur != "" {
		dup := false
		for _, x := range out {
			if x == cur {
				dup = true
				break
			}
		}
		if !dup {
			out = append(out, cur)
		}
		nxt := strings.TrimSpace(parenTail(cur))
		if nxt == cur {
			break
		}
		cur = nxt
	}
	return out
}

func parenTail(s string) string {
	if i := strings.LastIndex(s, "("); i >= 0 && strings.HasSuffix(s, ")") {
		return strings.TrimSpace(s[:i])
	}
	return s
}

func artistOK(got, want string) bool {
	if want == "" {
		return true
	}
	if got == "" {
		return false
	}
	return got == want || strings.Contains(got, want) || strings.Contains(want, got)
}

func ratio(a, b string) float64 {
	if a == b {
		return 1
	}
	// cheap similarity: longest common subsequence-ish via sequence matcher lite
	return seqRatio(a, b)
}

func seqRatio(a, b string) float64 {
	if a == "" || b == "" {
		return 0
	}
	// SequenceMatcher-ish: 2*matches/len
	matches := 0
	bi := 0
	used := make([]bool, len(b))
	for i := 0; i < len(a); i++ {
		for j := bi; j < len(b); j++ {
			if !used[j] && a[i] == b[j] {
				used[j] = true
				matches++
				bi = j + 1
				break
			}
		}
	}
	return 2 * float64(matches) / float64(len(a)+len(b))
}

func explicitFrom(payload map[string]any, album bool) *bool {
	code, has := payload["explicit_content_lyrics"]
	if has && code != nil {
		n := int(asFloat(code))
		if n == 1 || (album && n == 4) {
			t := true
			return &t
		}
		if n == 0 || n == 3 {
			f := false
			return &f
		}
		if n == 2 {
			return nil
		}
	}
	if v, ok := payload["explicit_lyrics"].(bool); ok {
		return &v
	}
	return nil
}

func asFloat(v any) float64 {
	switch t := v.(type) {
	case float64:
		return t
	default:
		n, _ := strconv.ParseFloat(httpx.AsString(v), 64)
		return n
	}
}

func explicitRank(flag *bool) int {
	if flag == nil {
		return 1
	}
	if *flag {
		return 2
	}
	return 0
}

func pictureURL(payload map[string]any) string {
	for _, k := range []string{"picture_xl", "picture_big", "picture"} {
		s := strings.TrimSpace(httpx.AsString(payload[k]))
		if s != "" {
			return s
		}
	}
	return ""
}

func artistInfos(payload map[string]any) []ArtistInfo {
	var infos []ArtistInfo
	seen := map[string]struct{}{}
	add := func(raw any) {
		m := httpx.AsMap(raw)
		name := strings.TrimSpace(httpx.AsString(m["name"]))
		if name == "" {
			return
		}
		key := strings.ToLower(name)
		if _, ok := seen[key]; ok {
			return
		}
		seen[key] = struct{}{}
		id := int(asFloat(m["id"]))
		infos = append(infos, ArtistInfo{Name: name, ArtistID: id, Picture: pictureURL(m)})
	}
	add(payload["artist"])
	for _, p := range httpx.AsList(payload["contributors"]) {
		add(p)
	}
	return infos
}

func (c *Client) get(path string, params map[string]string) (map[string]any, error) {
	return c.http.GetJSON("deezer/"+strings.TrimLeft(path, "/"), config.DeezerBase+"/"+strings.TrimLeft(path, "/"), params, 0)
}

func trackFrom(raw map[string]any) (Track, bool) {
	title := strings.TrimSpace(httpx.AsString(raw["title"]))
	if title == "" {
		return Track{}, false
	}
	var arts []string
	for _, inf := range artistInfos(raw) {
		arts = append(arts, inf.Name)
	}
	return Track{Title: title, Explicit: explicitFrom(raw, false), Artists: arts}, true
}

func (c *Client) albumTracks(id int, embedded []any, nb any) []Track {
	var items []Track
	seen := map[string]struct{}{}
	for _, raw := range embedded {
		t, ok := trackFrom(httpx.AsMap(raw))
		if !ok {
			continue
		}
		items = append(items, t)
		seen[strings.ToLower(t.Title)] = struct{}{}
	}
	expected := 0
	if nb != nil {
		expected = int(asFloat(nb))
	}
	if expected > 0 && len(items) >= expected {
		return items
	}
	path := "album/" + strconv.Itoa(id) + "/tracks"
	params := map[string]string{"limit": "100"}
	for path != "" {
		payload, err := c.get(path, params)
		if err != nil {
			break
		}
		for _, raw := range httpx.AsList(payload["data"]) {
			t, ok := trackFrom(httpx.AsMap(raw))
			if !ok {
				continue
			}
			k := strings.ToLower(t.Title)
			if _, ok := seen[k]; ok {
				continue
			}
			seen[k] = struct{}{}
			items = append(items, t)
		}
		nxt := strings.TrimSpace(httpx.AsString(payload["next"]))
		if nxt == "" {
			break
		}
		u, err := url.Parse(nxt)
		if err != nil {
			break
		}
		path = strings.TrimLeft(u.Path, "/")
		if strings.HasPrefix(path, "2.0/") {
			path = path[4:]
		}
		params = map[string]string{}
		for k, vs := range u.Query() {
			if len(vs) > 0 {
				params[k] = vs[0]
			}
		}
	}
	return items
}

func genresFrom(payload map[string]any) []string {
	var names []string
	skip := map[string]struct{}{"unclassified": {}, "unknown": {}, "other": {}, "none": {}}
	g := httpx.AsMap(payload["genres"])
	for _, raw := range httpx.AsList(g["data"]) {
		name := strings.TrimSpace(httpx.AsString(httpx.AsMap(raw)["name"]))
		if name == "" {
			continue
		}
		if _, ok := skip[titles.Norm(name)]; ok {
			continue
		}
		names = append(names, name)
	}
	return genres.PrettyList(names, 3)
}

func (c *Client) albumByID(id int) AlbumMatch {
	c.mu.Lock()
	if m, ok := c.byID[id]; ok {
		c.mu.Unlock()
		return m
	}
	c.mu.Unlock()
	payload, err := c.get("album/"+strconv.Itoa(id), nil)
	if err != nil || payload["error"] != nil || payload["id"] == nil {
		m := AlbumMatch{Source: "no-match"}
		c.mu.Lock()
		c.byID[id] = m
		c.mu.Unlock()
		return m
	}
	infos := artistInfos(payload)
	var arts []string
	for _, inf := range infos {
		arts = append(arts, inf.Name)
	}
	tracks := c.albumTracks(id, httpx.AsList(httpx.AsMap(payload["tracks"])["data"]), payload["nb_tracks"])
	src := "album:" + strconv.Itoa(id)
	gs := genresFrom(payload)
	if len(gs) == 0 && len(tracks) == 0 {
		src = "no-genre"
	}
	aa := ""
	if len(arts) > 0 {
		aa = arts[0]
	}
	m := AlbumMatch{
		Genres: gs, Source: src, AlbumID: int(asFloat(payload["id"])),
		Title: httpx.AsString(payload["title"]), AlbumArtist: aa,
		Artists: arts, ArtistInfos: infos, Tracks: tracks,
		Explicit: explicitFrom(payload, true),
	}
	c.mu.Lock()
	c.byID[id] = m
	c.mu.Unlock()
	return m
}

func (c *Client) pickAlbum(results []any, artist, album string) map[string]any {
	wantA, wantB := titles.Norm(artist), titles.Norm(album)
	var best map[string]any
	bestScore, bestRank := -1.0, -1
	for _, raw := range results {
		item := httpx.AsMap(raw)
		gotA := titles.Norm(httpx.AsString(httpx.AsMap(item["artist"])["name"]))
		if !artistOK(gotA, wantA) {
			continue
		}
		gotT := titles.Norm(httpx.AsString(item["title"]))
		if gotT == "" {
			continue
		}
		score := ratio(gotT, wantB)
		if gotT == wantB {
			score = 1
		} else if strings.Contains(gotT, wantB) || strings.Contains(wantB, gotT) {
			if score < 0.82 {
				score = 0.82
			}
		}
		rank := explicitRank(explicitFrom(item, true))
		if score > bestScore || (score == bestScore && rank > bestRank) {
			best, bestScore, bestRank = item, score, rank
		}
	}
	if best != nil && bestScore >= 0.72 {
		return best
	}
	return nil
}

func (c *Client) searchAlbum(artist, album string) AlbumMatch {
	q := `artist:"` + quote(artist) + `" album:"` + quote(album) + `"`
	payload, err := c.get("search/album", map[string]string{"q": q, "limit": "25"})
	if err != nil {
		return AlbumMatch{Source: "no-match"}
	}
	hit := c.pickAlbum(httpx.AsList(payload["data"]), artist, album)
	if hit == nil {
		return AlbumMatch{Source: "no-match"}
	}
	return c.albumByID(int(asFloat(hit["id"])))
}

func (c *Client) searchTrackAlbum(artist, title string) AlbumMatch {
	q := `artist:"` + quote(artist) + `" track:"` + quote(titles.StripMark(title)) + `"`
	payload, err := c.get("search/track", map[string]string{"q": q, "limit": "15"})
	if err != nil {
		return AlbumMatch{Source: "no-match"}
	}
	wantA, wantT := titles.Norm(artist), titles.Norm(title)
	var best map[string]any
	bestScore, bestRank := -1.0, -1
	for _, raw := range httpx.AsList(payload["data"]) {
		item := httpx.AsMap(raw)
		gotA := titles.Norm(httpx.AsString(httpx.AsMap(item["artist"])["name"]))
		gotT := titles.Norm(httpx.AsString(item["title"]))
		if !artistOK(gotA, wantA) || gotT == "" {
			continue
		}
		score := ratio(gotT, wantT)
		if gotT == wantT {
			score = 1
		} else if strings.Contains(gotT, wantT) || strings.Contains(wantT, gotT) {
			if score < 0.84 {
				score = 0.84
			}
		}
		if score < 0.72 {
			continue
		}
		rank := explicitRank(explicitFrom(item, false))
		if score > bestScore || (score == bestScore && rank > bestRank) {
			best, bestScore, bestRank = item, score, rank
		}
	}
	if best == nil {
		return AlbumMatch{Source: "no-match"}
	}
	id := asFloat(httpx.AsMap(best["album"])["id"])
	if id == 0 {
		return AlbumMatch{Source: "no-match"}
	}
	return c.albumByID(int(id))
}

func MatchTrack(title string, tracks []Track) *Track {
	want := titles.Norm(title)
	if want == "" || len(tracks) == 0 {
		return nil
	}
	var best *Track
	bestScore, bestRank := -1.0, -1
	for i := range tracks {
		t := &tracks[i]
		got := titles.Norm(t.Title)
		if got == "" {
			continue
		}
		score := ratio(got, want)
		if got == want {
			score = 1
		} else if strings.Contains(got, want) || strings.Contains(want, got) {
			if score < 0.84 {
				score = 0.84
			}
		}
		if score < 0.72 {
			continue
		}
		rank := explicitRank(t.Explicit)
		if score > bestScore || (score == bestScore && rank > bestRank) {
			best, bestScore, bestRank = t, score, rank
		}
	}
	return best
}

func (c *Client) SearchArtist(name string) *ArtistInfo {
	want := titles.Norm(name)
	if want == "" {
		return nil
	}
	c.mu.Lock()
	if v, ok := c.arts[want]; ok {
		c.mu.Unlock()
		return v
	}
	c.mu.Unlock()
	payload, err := c.get("search/artist", map[string]string{"q": name, "limit": "8"})
	if err != nil {
		return nil
	}
	var best *ArtistInfo
	bestScore := 0.0
	for _, raw := range httpx.AsList(payload["data"]) {
		m := httpx.AsMap(raw)
		got := strings.TrimSpace(httpx.AsString(m["name"]))
		if got == "" {
			continue
		}
		gotN := titles.Norm(got)
		score := ratio(gotN, want)
		if gotN == want {
			score = 1
		}
		if score < 0.86 {
			continue
		}
		if score > bestScore {
			bestScore = score
			best = &ArtistInfo{Name: got, ArtistID: int(asFloat(m["id"])), Picture: pictureURL(m)}
		}
	}
	if best != nil && best.Picture == "" && best.ArtistID != 0 {
		detail, err := c.get("artist/"+strconv.Itoa(best.ArtistID), nil)
		if err == nil && detail["error"] == nil {
			best.Picture = pictureURL(detail)
		}
	}
	c.mu.Lock()
	c.arts[want] = best
	c.mu.Unlock()
	return best
}

func (c *Client) LookupAlbum(artist, album, sampleTitle string) AlbumMatch {
	key := [2]string{titles.Norm(artist), titles.Norm(album)}
	c.mu.Lock()
	if m, ok := c.byKA[key]; ok {
		c.mu.Unlock()
		return m
	}
	c.mu.Unlock()
	var disk map[string]any
	if c.http.Cache != nil && c.http.Cache.Get("deezer/album-match/v1", map[string]string{"artist": key[0], "album": key[1]}, &disk, 0) {
		m := matchFromCache(disk)
		c.mu.Lock()
		c.http.Hits++
		c.byKA[key] = m
		if m.AlbumID != 0 {
			c.byID[m.AlbumID] = m
		}
		c.mu.Unlock()
		return m
	}
	m := AlbumMatch{Source: "no-match"}
	for _, variant := range albumVariants(album) {
		m = c.searchAlbum(artist, variant)
		if m.AlbumID != 0 {
			break
		}
	}
	if m.AlbumID == 0 && sampleTitle != "" {
		tm := c.searchTrackAlbum(artist, sampleTitle)
		if tm.AlbumID != 0 {
			m = tm
		}
	}
	if c.http.Cache != nil {
		c.http.Cache.Set("deezer/album-match/v1", map[string]string{"artist": key[0], "album": key[1]}, matchToCache(m))
	}
	c.mu.Lock()
	c.byKA[key] = m
	if m.AlbumID != 0 {
		c.byID[m.AlbumID] = m
	}
	c.mu.Unlock()
	return m
}

func (c *Client) DownloadImage(url string) ([]byte, string, error) {
	b, mime, err := c.http.GetBytes(url)
	if err != nil {
		return nil, "", err
	}
	mime = strings.ToLower(strings.Split(mime, ";")[0])
	if mime == "image/jpg" || mime == "jpg" || mime == "jpeg" || !strings.HasPrefix(mime, "image/") {
		mime = "image/jpeg"
	}
	return b, mime, nil
}

func boolPtrFrom(v any) *bool {
	if v == nil {
		return nil
	}
	if b, ok := v.(bool); ok {
		return &b
	}
	return nil
}

func matchFromCache(raw map[string]any) AlbumMatch {
	m := AlbumMatch{
		Source:      httpx.AsString(raw["source"]),
		Title:       httpx.AsString(raw["title"]),
		AlbumArtist: httpx.AsString(raw["album_artist"]),
		Explicit:    boolPtrFrom(raw["explicit"]),
	}
	if id := asFloat(raw["album_id"]); id != 0 {
		m.AlbumID = int(id)
	}
	for _, g := range httpx.AsList(raw["genres"]) {
		m.Genres = append(m.Genres, httpx.AsString(g))
	}
	for _, a := range httpx.AsList(raw["artists"]) {
		m.Artists = append(m.Artists, httpx.AsString(a))
	}
	for _, inf := range httpx.AsList(raw["artist_infos"]) {
		im := httpx.AsMap(inf)
		m.ArtistInfos = append(m.ArtistInfos, ArtistInfo{
			Name: httpx.AsString(im["name"]), ArtistID: int(asFloat(im["artist_id"])), Picture: httpx.AsString(im["picture"]),
		})
	}
	for _, t := range httpx.AsList(raw["tracks"]) {
		tm := httpx.AsMap(t)
		var arts []string
		for _, a := range httpx.AsList(tm["artists"]) {
			arts = append(arts, httpx.AsString(a))
		}
		m.Tracks = append(m.Tracks, Track{Title: httpx.AsString(tm["title"]), Explicit: boolPtrFrom(tm["explicit"]), Artists: arts})
	}
	return m
}

func matchToCache(m AlbumMatch) map[string]any {
	infos := []any{}
	for _, i := range m.ArtistInfos {
		infos = append(infos, map[string]any{"name": i.Name, "artist_id": i.ArtistID, "picture": i.Picture})
	}
	tracks := []any{}
	for _, t := range m.Tracks {
		var ex any
		if t.Explicit != nil {
			ex = *t.Explicit
		}
		tracks = append(tracks, map[string]any{"title": t.Title, "explicit": ex, "artists": t.Artists})
	}
	var ex any
	if m.Explicit != nil {
		ex = *m.Explicit
	}
	return map[string]any{
		"genres": m.Genres, "source": m.Source, "album_id": m.AlbumID, "title": m.Title,
		"album_artist": m.AlbumArtist, "artists": m.Artists, "explicit": ex,
		"artist_infos": infos, "tracks": tracks,
	}
}
