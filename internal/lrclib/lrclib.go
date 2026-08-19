package lrclib

import (
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/TidBits16/peanut-butter-jelly/internal/cache"
	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"github.com/TidBits16/peanut-butter-jelly/internal/httpx"
	"github.com/TidBits16/peanut-butter-jelly/internal/jellyfin"
	"github.com/TidBits16/peanut-butter-jelly/internal/titles"
)

var (
	syncRE    = regexp.MustCompile(`\[\d{1,2}:\d{2}`)
	parenTail = regexp.MustCompile(`\s*\([^)]*\)\s*$`)
	bracket   = regexp.MustCompile(`^\[.*\]$`)
)

type Match struct {
	Synced       string
	Plain        string
	Instrumental bool
	Source       string
	TrackName    string
}

func (m Match) Usable() bool { return m.Instrumental || m.Synced != "" || m.Plain != "" }

type Client struct {
	http *httpx.Client
	cfg  *config.Config
}

func New(cfg *config.Config, store *cache.Store) *Client {
	return &Client{
		cfg: cfg,
		http: httpx.New(store, map[string]string{
			"User-Agent": cfg.UserAgent,
			"Accept":     "application/json",
		}, 250*time.Millisecond),
	}
}

func (c *Client) Stats() (httpN, hits int) { return c.http.HTTPN, c.http.Hits }

func clean(s string) string {
	s = strings.ReplaceAll(s, "\r\n", "\n")
	s = strings.ReplaceAll(s, "\r", "\n")
	return strings.TrimSpace(s)
}

func fromPayload(p map[string]any, source string) Match {
	synced := clean(httpx.AsString(p["syncedLyrics"]))
	plain := clean(httpx.AsString(p["plainLyrics"]))
	if synced != "" && !syncRE.MatchString(synced) {
		if plain == "" {
			plain = synced
		}
		synced = ""
	}
	name := httpx.AsString(p["trackName"])
	if name == "" {
		name = httpx.AsString(p["name"])
	}
	return Match{Synced: synced, Plain: plain, Instrumental: truthy(p["instrumental"]), Source: source, TrackName: name}
}

func truthy(v any) bool {
	b, ok := v.(bool)
	return ok && b
}

func titleVariants(title string) []string {
	var out []string
	cur := strings.TrimSpace(titles.StripMark(title))
	for cur != "" {
		seen := false
		for _, x := range out {
			if x == cur {
				seen = true
				break
			}
		}
		if !seen {
			out = append(out, cur)
		}
		nxt := strings.TrimSpace(parenTail.ReplaceAllString(cur, ""))
		if nxt == cur {
			break
		}
		cur = nxt
	}
	return out
}

func durationOK(got any, want *int) bool {
	if want == nil {
		return true
	}
	n, err := strconv.ParseFloat(httpx.AsString(got), 64)
	if err != nil {
		return false
	}
	d := n - float64(*want)
	if d < 0 {
		d = -d
	}
	return d <= 2
}

func (c *Client) get(path string, params map[string]string) (map[string]any, error) {
	return c.http.GetJSON("lrclib/"+strings.TrimLeft(path, "/"), config.LRCLibBase+"/"+strings.TrimLeft(path, "/"), params, 7*24*time.Hour)
}

func (c *Client) Lookup(title, artist, album string, duration *int) Match {
	title = strings.TrimSpace(titles.StripMark(title))
	artist = strings.TrimSpace(artist)
	album = strings.TrimSpace(titles.StripMark(album))
	if title == "" || artist == "" {
		return Match{Source: "no-match"}
	}
	cp := map[string]string{"title": strings.ToLower(title), "artist": strings.ToLower(artist), "album": strings.ToLower(album), "duration": ""}
	if duration != nil {
		cp["duration"] = strconv.Itoa(*duration)
	}
	var cached map[string]any
	if c.http.Cache != nil && c.http.Cache.Get("lrclib/match/v2", cp, &cached, 7*24*time.Hour) {
		c.http.Hits++
		if truthy(cached["_miss"]) {
			return Match{Source: "no-match"}
		}
		return Match{
			Synced: clean(httpx.AsString(cached["synced"])), Plain: clean(httpx.AsString(cached["plain"])),
			Instrumental: truthy(cached["instrumental"]), Source: httpx.AsString(cached["source"]),
			TrackName: httpx.AsString(cached["track_name"]),
		}
	}
	titlesV := titleVariants(title)
	var fallback *Match
	take := func(m Match) *Match {
		if m.Synced != "" {
			return &m
		}
		if m.Instrumental && m.Plain == "" && strings.HasPrefix(m.Source, "get") {
			return &m
		}
		if m.Plain != "" && fallback == nil {
			mm := m
			fallback = &mm
		}
		return nil
	}
	tryGet := func(name string, withAlbum bool, source string) *Match {
		params := map[string]string{"track_name": name, "artist_name": artist}
		if withAlbum && album != "" {
			params["album_name"] = album
		}
		if duration != nil {
			params["duration"] = strconv.Itoa(*duration)
		}
		payload, err := c.get("api/get", params)
		if err != nil || payload == nil || truthy(payload["_miss"]) {
			return nil
		}
		m := fromPayload(payload, source+":"+httpx.AsString(payload["id"]))
		return take(m)
	}
	store := func(m Match) {
		if c.http.Cache == nil {
			return
		}
		if m.Source == "no-match" {
			c.http.Cache.Set("lrclib/match/v2", cp, map[string]any{"_miss": true})
			return
		}
		c.http.Cache.Set("lrclib/match/v2", cp, map[string]any{
			"synced": m.Synced, "plain": m.Plain, "instrumental": m.Instrumental,
			"source": m.Source, "track_name": m.TrackName,
		})
	}
	if done := tryGet(titlesV[0], true, "get"); done != nil {
		store(*done)
		return *done
	}
	if album != "" {
		if done := tryGet(titlesV[0], false, "get-noalbum"); done != nil {
			store(*done)
			return *done
		}
	}
	payload, err := c.get("api/search", map[string]string{"track_name": titlesV[0], "artist_name": artist})
	if err == nil {
		results := httpx.AsList(payload["results"])
		if results == nil {
			results = httpx.AsList(payload["data"])
		}
		var best *Match
		bestScore := -1.0
		for _, raw := range results {
			m := httpx.AsMap(raw)
			if duration != nil && !durationOK(m["duration"], duration) {
				continue
			}
			match := fromPayload(m, "search:"+httpx.AsString(m["id"]))
			if !match.Usable() {
				continue
			}
			score := 1.0
			if match.Synced != "" {
				score = 3
			}
			if duration != nil {
				got, _ := strconv.ParseFloat(httpx.AsString(m["duration"]), 64)
				d := got - float64(*duration)
				if d < 0 {
					d = -d
				}
				score -= d / 10
			}
			if score > bestScore {
				mm := match
				best, bestScore = &mm, score
			}
		}
		if best != nil {
			if done := take(*best); done != nil {
				store(*done)
				return *done
			}
		}
	}
	for _, name := range titlesV[1:] {
		if done := tryGet(name, true, "get"); done != nil {
			store(*done)
			return *done
		}
		if album != "" {
			if done := tryGet(name, false, "get-noalbum"); done != nil {
				store(*done)
				return *done
			}
		}
	}
	if fallback != nil {
		store(*fallback)
		return *fallback
	}
	store(Match{Source: "no-match"})
	return Match{Source: "no-match"}
}

func (c *Client) LookupArtists(title string, artists []string, album string, duration *int) Match {
	if len(artists) == 0 {
		return Match{Source: "no-match"}
	}
	m := c.Lookup(title, artists[0], album, duration)
	if m.Synced != "" || m.Plain != "" || m.Instrumental {
		return m
	}
	for _, a := range artists[1:] {
		cand := c.Lookup(title, a, album, duration)
		if cand.Synced != "" || cand.Plain != "" || cand.Instrumental {
			return cand
		}
	}
	return m
}

func QueryArtists(item jellyfin.Item, extra []string) []string {
	var names []string
	seen := map[string]struct{}{}
	add := func(v string) {
		text := strings.TrimSpace(v)
		if text == "" || bracket.MatchString(text) {
			return
		}
		for _, cand := range []string{text, strings.NewReplacer("‐", "-", "‑", "-", "‒", "-", "–", "-", "—", "-").Replace(text)} {
			k := strings.ToLower(cand)
			if _, ok := seen[k]; ok {
				continue
			}
			seen[k] = struct{}{}
			names = append(names, cand)
		}
	}
	for _, n := range extra {
		add(n)
	}
	for _, n := range item.Artists() {
		add(n)
	}
	add(item.AlbumArtist())
	return names
}

func LyricsFilename(jfPath, ext string) string {
	name := filepath.Base(jfPath)
	if name != "" && name != "." && name != "/" {
		return strings.TrimSuffix(name, filepath.Ext(name)) + ext
	}
	return "lyrics" + ext
}
