package bios

import (
	"html"
	"net/url"
	"regexp"
	"strings"
	"time"
	"unicode"

	"github.com/TidBits16/peanut-butter-jelly/internal/cache"
	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"github.com/TidBits16/peanut-butter-jelly/internal/httpx"
	"github.com/TidBits16/peanut-butter-jelly/internal/titles"
)

var (
	htmlRE  = regexp.MustCompile(`<[^>]+>`)
	parenRE = regexp.MustCompile(`\s*\([^)]*\)\s*`)
	musicRE = regexp.MustCompile(`(?i)\b(rapper|singer|songwriter|musician|vocalist|band|dj|composer|record producer|music producer|hip[\s-]?hop|folk[\s-]?punk|indie pop|folk-pop|pop (?:band|group|duo|trio|artist)|rock (?:band|group)|multi-instrumentalist|musical (?:artist|group|duo|trio)|recording artist|guitarist|drummer|bassist|pianist|youtuber|ensemble|orchestra|choir|mc)\b`)
	skipRE  = regexp.MustCompile(`(?i)\b(discography|filmography|list of|politician)\b|\((album|song|ep|single|soundtrack)\)`)
)

type Match struct {
	Overview string
	Source   string
}

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
		}, 200*time.Millisecond),
	}
}

func (c *Client) Stats() (httpN, hits int) { return c.http.HTTPN, c.http.Hits }

func compact(s string) string {
	var b strings.Builder
	for _, r := range titles.Norm(s) {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			b.WriteRune(r)
		}
	}
	return b.String()
}

func nameScore(want, got string) float64 {
	a := titles.Norm(want)
	b := titles.Norm(parenRE.ReplaceAllString(got, " "))
	if a == "" || b == "" {
		return 0
	}
	if a == b {
		return 1
	}
	score := seq(a, b)
	if compact(a) == compact(b) && score < 0.98 {
		score = 0.98
	}
	return score
}

func seq(a, b string) float64 {
	if a == b {
		return 1
	}
	matches := 0
	used := make([]bool, len(b))
	bi := 0
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

func cleanBio(text string, limit int) string {
	value := htmlRE.ReplaceAllString(html.UnescapeString(text), " ")
	value = strings.ReplaceAll(value, "\r\n", "\n")
	value = strings.ReplaceAll(value, "\r", "\n")
	value = strings.TrimSpace(value)
	if len(value) > limit {
		cut := value[:limit]
		if i := strings.LastIndex(cut, "\n"); i > 0 {
			cut = cut[:i]
		}
		value = strings.TrimSpace(cut)
	}
	return value
}

func (c *Client) get(rawURL, cachePath string, params map[string]string) (map[string]any, error) {
	return c.http.GetJSON(cachePath, rawURL, params, 7*24*time.Hour)
}

func (c *Client) Lookup(name string) Match {
	want := strings.TrimSpace(name)
	if want == "" {
		return Match{}
	}
	var cached map[string]any
	key := map[string]string{"name": strings.ToLower(want)}
	if c.http.Cache != nil && c.http.Cache.Get("bio/v3", key, &cached, 7*24*time.Hour) {
		c.http.Hits++
		if truthy(cached["_miss"]) {
			return Match{}
		}
		return Match{Overview: httpx.AsString(cached["overview"]), Source: httpx.AsString(cached["source"])}
	}
	queries := []string{want}
	stripped := titles.Norm(want)
	if stripped != "" && !strings.EqualFold(stripped, want) {
		queries = append(queries, stripped)
	}
	var match *Match
	for _, q := range queries {
		payload, err := c.get(config.AudioDBBase+"/search.php", "audiodb/search", map[string]string{"s": q})
		if err != nil {
			continue
		}
		if m := pickAudioDB(want, payload); m != nil {
			match = m
			break
		}
	}
	if match == nil {
		if m := c.fromWikipedia(want); m != nil {
			match = m
		} else if m := c.fromWikidata(want); m != nil {
			match = m
		}
	}
	if match != nil && match.Overview != "" {
		if c.http.Cache != nil {
			c.http.Cache.Set("bio/v3", key, map[string]any{"overview": match.Overview, "source": match.Source})
		}
		return *match
	}
	if c.http.Cache != nil {
		c.http.Cache.Set("bio/v3", key, map[string]any{"_miss": true})
	}
	return Match{}
}

func truthy(v any) bool { b, ok := v.(bool); return ok && b }

func pickAudioDB(name string, payload map[string]any) *Match {
	var best *Match
	bestScore := 0.0
	for _, raw := range httpx.AsList(payload["artists"]) {
		m := httpx.AsMap(raw)
		got := strings.TrimSpace(httpx.AsString(m["strArtist"]))
		score := nameScore(name, got)
		if score < 0.72 && !strings.Contains(compact(got), compact(name)) {
			continue
		}
		bio := cleanBio(firstNonEmpty(m, "strBiography", "strBiographyEN", "strBiographyDE", "strBiographyFR"), 4000)
		if bio == "" {
			continue
		}
		if score > bestScore {
			bestScore = score
			best = &Match{Overview: bio, Source: "audiodb:" + or(got, name)}
		}
	}
	return best
}

func firstNonEmpty(m map[string]any, keys ...string) string {
	for _, k := range keys {
		if s := httpx.AsString(m[k]); strings.TrimSpace(s) != "" {
			return s
		}
	}
	return ""
}

func or(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func (c *Client) wikiSearch(query string) []string {
	payload, err := c.get(config.WikiAPI, "wikipedia/search", map[string]string{
		"action": "query", "list": "search", "srsearch": query, "srlimit": "8", "srprop": "", "format": "json",
	})
	if err != nil {
		return nil
	}
	var titlesOut []string
	q := httpx.AsMap(payload["query"])
	for _, hit := range httpx.AsList(q["search"]) {
		t := strings.TrimSpace(httpx.AsString(httpx.AsMap(hit)["title"]))
		if t != "" {
			titlesOut = append(titlesOut, t)
		}
	}
	return titlesOut
}

func (c *Client) wikiSummary(title string) map[string]any {
	u := config.WikiSummary + "/" + url.PathEscape(strings.ReplaceAll(title, " ", "_"))
	p, _ := c.get(u, "wikipedia/summary/"+title, nil)
	return p
}

func (c *Client) fromTitles(name string, list []string) *Match {
	var best *Match
	bestNS, bestMusic := -1.0, -1
	for _, title := range list {
		if skipRE.MatchString(title) {
			continue
		}
		ns := nameScore(name, title)
		if ns < 0.72 {
			continue
		}
		payload := c.wikiSummary(title)
		if payload == nil || httpx.AsString(payload["type"]) == "disambiguation" {
			continue
		}
		extract := cleanBio(httpx.AsString(payload["extract"]), 4000)
		desc := httpx.AsString(payload["description"])
		if extract == "" {
			continue
		}
		if !musicRE.MatchString(desc + " " + clip(extract, 500)) {
			continue
		}
		md := 0
		if musicRE.MatchString(desc) {
			md = 1
		}
		if ns > bestNS || (ns == bestNS && md > bestMusic) {
			bestNS, bestMusic = ns, md
			best = &Match{Overview: extract, Source: "wikipedia:" + title}
			if ns >= 0.98 && md == 1 {
				break
			}
		}
	}
	return best
}

func clip(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n]
}

func (c *Client) fromWikipedia(name string) *Match {
	seen := map[string]struct{}{}
	for _, q := range []string{name, name + " (musician OR singer OR band OR rapper)"} {
		var titles []string
		for _, t := range c.wikiSearch(q) {
			if _, ok := seen[t]; ok {
				continue
			}
			seen[t] = struct{}{}
			titles = append(titles, t)
		}
		if m := c.fromTitles(name, titles); m != nil {
			return m
		}
	}
	return nil
}

func (c *Client) fromWikidata(name string) *Match {
	payload, err := c.get(config.WikidataAPI, "wikidata/search", map[string]string{
		"action": "wbsearchentities", "search": name, "language": "en", "type": "item", "limit": "5", "format": "json",
	})
	if err != nil {
		return nil
	}
	for _, raw := range httpx.AsList(payload["search"]) {
		hit := httpx.AsMap(raw)
		label := httpx.AsString(hit["label"])
		desc := httpx.AsString(hit["description"])
		if nameScore(name, label) < 0.72 && compact(name) != compact(label) {
			continue
		}
		if desc != "" && !musicRE.MatchString(desc) {
			continue
		}
		qid := httpx.AsString(hit["id"])
		if qid == "" {
			continue
		}
		entP, err := c.get(config.WikidataAPI, "wikidata/entity/"+qid, map[string]string{
			"action": "wbgetentities", "ids": qid, "props": "sitelinks|descriptions",
			"languages": "en", "sitefilter": "enwiki", "format": "json",
		})
		if err != nil {
			continue
		}
		ent := httpx.AsMap(httpx.AsMap(entP["entities"])[qid])
		title := strings.TrimSpace(httpx.AsString(httpx.AsMap(httpx.AsMap(ent["sitelinks"])["enwiki"])["title"]))
		if title != "" {
			if m := c.fromTitles(name, []string{title}); m != nil {
				return m
			}
		}
		d := strings.TrimSpace(httpx.AsString(httpx.AsMap(httpx.AsMap(ent["descriptions"])["en"])["value"]))
		if d == "" {
			d = desc
		}
		if d != "" && musicRE.MatchString(d) {
			r := []rune(d)
			r[0] = unicode.ToUpper(r[0])
			return &Match{Overview: string(r) + ".", Source: "wikidata:" + qid}
		}
	}
	return nil
}
