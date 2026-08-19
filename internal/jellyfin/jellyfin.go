package jellyfin

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"

	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"github.com/TidBits16/peanut-butter-jelly/internal/httpx"
)

var uuidRE = regexp.MustCompile(`^[0-9a-fA-F-]{32,36}$`)

type Client struct {
	cfg  *config.Config
	http *http.Client
}

func New(cfg *config.Config) *Client {
	return &Client{
		cfg:  cfg,
		http: &http.Client{Timeout: 180 * time.Second},
	}
}

func (c *Client) do(method, path string, query url.Values, body []byte, contentType string) ([]byte, int, error) {
	u := c.cfg.Jellyfin + path
	if query != nil {
		u += "?" + query.Encode()
	}
	var rdr io.Reader
	if body != nil {
		rdr = bytes.NewReader(body)
	}
	req, err := http.NewRequest(method, u, rdr)
	if err != nil {
		return nil, 0, err
	}
	req.Header.Set("X-Emby-Token", c.cfg.APIKey)
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	} else if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := c.http.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer res.Body.Close()
	b, _ := io.ReadAll(res.Body)
	if res.StatusCode >= 400 {
		return b, res.StatusCode, fmt.Errorf("%s %s %s", res.Status, u, truncate(string(b), 300))
	}
	return b, res.StatusCode, nil
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n]
}

type Item map[string]any

func (it Item) ID() string { return strings.TrimSpace(httpx.AsString(it["Id"])) }
func (it Item) PlaylistItemID() string {
	return strings.TrimSpace(httpx.AsString(it["PlaylistItemId"]))
}
func (it Item) Name() string    { return strings.TrimSpace(httpx.AsString(it["Name"])) }
func (it Item) Path() string    { return strings.TrimSpace(httpx.AsString(it["Path"])) }
func (it Item) Album() string   { return strings.TrimSpace(httpx.AsString(it["Album"])) }
func (it Item) AlbumID() string { return strings.TrimSpace(httpx.AsString(it["AlbumId"])) }

func (it Item) AlbumArtist() string {
	return strings.TrimSpace(httpx.AsString(it["AlbumArtist"]))
}

func (it Item) Strings(key string) []string {
	out := []string{}
	for _, v := range httpx.AsList(it[key]) {
		s := strings.TrimSpace(httpx.AsString(v))
		if s != "" {
			out = append(out, s)
		}
	}
	return out
}

func (it Item) Artists() []string { return it.Strings("Artists") }
func (it Item) Genres() []string  { return it.Strings("Genres") }
func (it Item) Tags() []string    { return it.Strings("Tags") }

func (it Item) HasTag(name string) bool {
	want := strings.ToLower(name)
	for _, t := range it.Tags() {
		if strings.ToLower(t) == want {
			return true
		}
	}
	return false
}

func (it Item) AlbumArtistNames() []string {
	out := []string{}
	for _, v := range httpx.AsList(it["AlbumArtists"]) {
		m := httpx.AsMap(v)
		n := strings.TrimSpace(httpx.AsString(m["Name"]))
		if n != "" {
			out = append(out, n)
		}
	}
	return out
}

func (it Item) HasPrimaryImage() bool {
	tags := httpx.AsMap(it["ImageTags"])
	return strings.TrimSpace(httpx.AsString(tags["Primary"])) != ""
}

func (it Item) Overview() string {
	return strings.TrimSpace(httpx.AsString(it["Overview"]))
}

func (it Item) HasLyrics() bool {
	switch v := it["HasLyrics"].(type) {
	case bool:
		return v
	default:
		return false
	}
}

func (it Item) DurationSeconds() *int {
	ticks, ok := it["RunTimeTicks"]
	if !ok || ticks == nil {
		return nil
	}
	var n float64
	switch t := ticks.(type) {
	case float64:
		n = t
	default:
		return nil
	}
	sec := int(n/10_000_000 + 0.5)
	if sec < 1 || sec > 3600 {
		return nil
	}
	return &sec
}

func (c *Client) items(path string, q url.Values) ([]Item, error) {
	b, _, err := c.do(http.MethodGet, path, q, nil, "")
	if err != nil {
		return nil, err
	}
	var wrap struct {
		Items []Item `json:"Items"`
	}
	if err := json.Unmarshal(b, &wrap); err != nil {
		return nil, err
	}
	return wrap.Items, nil
}

func (c *Client) ResolveUserID(ref string) (string, error) {
	if uuidRE.MatchString(ref) {
		return ref, nil
	}
	b, _, err := c.do(http.MethodGet, "/Users", nil, nil, "")
	if err != nil {
		return "", err
	}
	var users []Item
	if err := json.Unmarshal(b, &users); err != nil {
		return "", err
	}
	want := strings.ToLower(ref)
	for _, u := range users {
		if strings.ToLower(u.Name()) == want {
			return u.ID(), nil
		}
	}
	return "", fmt.Errorf("could not resolve user_id from %q", ref)
}

func (c *Client) UserIDs(preferred string) ([]string, error) {
	b, _, err := c.do(http.MethodGet, "/Users", nil, nil, "")
	if err != nil {
		return nil, err
	}
	var users []Item
	if err := json.Unmarshal(b, &users); err != nil {
		return nil, err
	}
	out := []string{preferred}
	for _, u := range users {
		id := u.ID()
		if id != "" && id != preferred {
			out = append(out, id)
		}
	}
	return out, nil
}

func (c *Client) Tracks(uid string) ([]Item, error) {
	q := url.Values{
		"IncludeItemTypes": {"Audio"},
		"Fields":           {"Path,Tags,Genres,Album,AlbumArtist,Artists,AlbumArtists,AlbumId,HasLyrics,RunTimeTicks,Overview,ProviderIds"},
		"Recursive":        {"true"},
		"Limit":            {"100000"},
	}
	items, err := c.items("/Users/"+uid+"/Items", q)
	if err != nil {
		return nil, err
	}
	out := items[:0]
	for _, it := range items {
		if it.ID() != "" {
			out = append(out, it)
		}
	}
	return out, nil
}

func (c *Client) Albums(uid string) (map[string]Item, error) {
	q := url.Values{
		"IncludeItemTypes": {"MusicAlbum"},
		"Fields":           {"Path,Tags,Genres,AlbumArtist,Artists,AlbumArtists,Overview,ProviderIds,Name"},
		"Recursive":        {"true"},
		"Limit":            {"100000"},
	}
	items, err := c.items("/Users/"+uid+"/Items", q)
	if err != nil {
		return nil, err
	}
	out := map[string]Item{}
	for _, it := range items {
		if it.ID() != "" {
			out[it.ID()] = it
		}
	}
	return out, nil
}

func (c *Client) Artists() (map[string]Item, error) {
	q := url.Values{
		"Fields":    {"Overview,ImageTags,ProviderIds,Tags,Genres,Name"},
		"Recursive": {"true"},
		"Limit":     {"100000"},
	}
	items, err := c.items("/Artists", q)
	if err != nil {
		return nil, err
	}
	out := map[string]Item{}
	for _, it := range items {
		n := strings.TrimSpace(it.Name())
		if n != "" && it.ID() != "" {
			if _, ok := out[strings.ToLower(n)]; !ok {
				out[strings.ToLower(n)] = it
			}
		}
	}
	return out, nil
}

func (c *Client) Playlists() ([]Item, error) {
	q := url.Values{
		"IncludeItemTypes": {"Playlist"},
		"Recursive":        {"true"},
		"Fields":           {"Path,ChildCount"},
		"Limit":            {"500"},
	}
	items, err := c.items("/Items", q)
	if err != nil {
		return nil, err
	}
	out := items[:0]
	for _, it := range items {
		if it.ID() != "" {
			out = append(out, it)
		}
	}
	return out, nil
}

func (c *Client) PlaylistItems(pid, uid string) ([]Item, error) {
	q := url.Values{
		"UserId": {uid},
		"Fields": {"Path,Album,AlbumArtist,Artists"},
		"Limit":  {"100000"},
	}
	b, status, err := c.do(http.MethodGet, "/Playlists/"+pid+"/Items", q, nil, "")
	if status == 404 {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var wrap struct {
		Items []Item `json:"Items"`
	}
	if err := json.Unmarshal(b, &wrap); err != nil {
		return nil, err
	}
	return wrap.Items, nil
}

func (c *Client) AddPlaylistItems(pid, uid string, ids []string) error {
	if len(ids) == 0 {
		return nil
	}
	q := url.Values{"ids": {strings.Join(ids, ",")}, "userId": {uid}}
	_, _, err := c.do(http.MethodPost, "/Playlists/"+pid+"/Items", q, nil, "")
	return err
}

func (c *Client) RemovePlaylistEntries(pid string, entryIDs []string) error {
	if len(entryIDs) == 0 {
		return nil
	}
	q := url.Values{"entryIds": {strings.Join(entryIDs, ",")}}
	_, _, err := c.do(http.MethodDelete, "/Playlists/"+pid+"/Items", q, nil, "")
	return err
}

func (c *Client) Logs() ([]string, error) {
	b, _, err := c.do(http.MethodGet, "/System/Logs", nil, nil, "")
	if err != nil {
		return nil, err
	}
	var rows []struct {
		Name string `json:"Name"`
	}
	if err := json.Unmarshal(b, &rows); err != nil {
		return nil, err
	}
	out := make([]string, 0, len(rows))
	for _, r := range rows {
		if r.Name != "" {
			out = append(out, r.Name)
		}
	}
	return out, nil
}

func (c *Client) LogText(name string) (string, error) {
	q := url.Values{"name": {name}}
	b, _, err := c.do(http.MethodGet, "/System/Logs/Log", q, nil, "")
	if err != nil {
		return "", err
	}
	return string(b), nil
}

type Patch struct {
	Genres      []string
	SetGenres   bool
	Artists     []string
	SetArtists  bool
	AlbumArtist *string
	Explicit    *bool
	Name        *string
	AddTags     []string
	RemoveTags  []string
	Overview    *string
	Item        Item
}

func snapshot(it Item) string {
	tags := append([]string{}, it.Tags()...)
	for i := range tags {
		tags[i] = strings.ToLower(strings.TrimSpace(tags[i]))
	}
	b, _ := json.Marshal([]any{
		it.Genres(), it.Artists(), it.AlbumArtist(), it.AlbumArtistNames(),
		it.Name(), it.Overview(), tags,
	})
	return string(b)
}

func (c *Client) UpdateItem(uid, itemID string, p Patch) (bool, error) {
	item := p.Item
	if item == nil {
		b, _, err := c.do(http.MethodGet, "/Users/"+uid+"/Items/"+itemID, nil, nil, "")
		if err != nil {
			return false, err
		}
		if err := json.Unmarshal(b, &item); err != nil {
			return false, err
		}
	} else {
		raw, _ := json.Marshal(item)
		_ = json.Unmarshal(raw, &item)
	}
	before := snapshot(item)
	if p.SetGenres {
		g := make([]any, len(p.Genres))
		for i, x := range p.Genres {
			g[i] = x
		}
		item["Genres"] = g
	}
	if p.SetArtists {
		a := make([]any, len(p.Artists))
		aa := make([]any, len(p.Artists))
		for i, n := range p.Artists {
			a[i] = n
			aa[i] = map[string]any{"Name": n}
		}
		item["Artists"] = a
		delete(item, "ArtistItems")
		item["AlbumArtists"] = aa
	}
	if p.AlbumArtist != nil {
		item["AlbumArtist"] = *p.AlbumArtist
		if !p.SetArtists {
			item["AlbumArtists"] = []any{map[string]any{"Name": *p.AlbumArtist}}
		}
	}
	tags := item.Tags()
	if p.Explicit != nil {
		next := tags[:0]
		for _, t := range tags {
			if strings.ToLower(t) != "explicit" {
				next = append(next, t)
			}
		}
		tags = next
		if *p.Explicit {
			tags = append(tags, "Explicit")
		}
	}
	if len(p.RemoveTags) > 0 {
		drop := map[string]struct{}{}
		for _, t := range p.RemoveTags {
			drop[strings.ToLower(t)] = struct{}{}
		}
		next := tags[:0]
		for _, t := range tags {
			if _, ok := drop[strings.ToLower(t)]; !ok {
				next = append(next, t)
			}
		}
		tags = next
	}
	if len(p.AddTags) > 0 {
		have := map[string]struct{}{}
		for _, t := range tags {
			have[strings.ToLower(t)] = struct{}{}
		}
		for _, t := range p.AddTags {
			if _, ok := have[strings.ToLower(t)]; !ok {
				tags = append(tags, t)
				have[strings.ToLower(t)] = struct{}{}
			}
		}
	}
	if p.Explicit != nil || len(p.AddTags) > 0 || len(p.RemoveTags) > 0 {
		arr := make([]any, len(tags))
		for i, t := range tags {
			arr[i] = t
		}
		item["Tags"] = arr
	}
	if p.Name != nil {
		item["Name"] = *p.Name
	}
	if p.Overview != nil {
		item["Overview"] = *p.Overview
	}
	if snapshot(item) == before {
		return false, nil
	}
	if item["Tags"] == nil {
		item["Tags"] = []any{}
	}
	if item["Genres"] == nil {
		item["Genres"] = []any{}
	}
	if item["ProviderIds"] == nil {
		item["ProviderIds"] = map[string]any{}
	}
	delete(item, "UserData")
	body, err := json.Marshal(item)
	if err != nil {
		return false, err
	}
	_, _, err = c.do(http.MethodPost, "/Items/"+itemID, nil, body, "application/json")
	return err == nil, err
}

func (c *Client) UploadImage(itemID string, data []byte, contentType string) (bool, error) {
	if len(data) == 0 {
		return false, nil
	}
	mime := strings.ToLower(strings.Split(contentType, ";")[0])
	switch mime {
	case "image/jpg", "jpg", "jpeg", "":
		mime = "image/jpeg"
	case "png", "image/x-png":
		mime = "image/png"
	case "webp":
		mime = "image/webp"
	}
	if !strings.HasPrefix(mime, "image/") {
		mime = "image/jpeg"
	}
	enc := base64.StdEncoding.EncodeToString(data)
	_, _, err := c.do(http.MethodPost, "/Items/"+itemID+"/Images/Primary", nil, []byte(enc), mime)
	return err == nil, err
}

func (c *Client) UploadLyrics(itemID, fileName, text string) (bool, error) {
	if strings.TrimSpace(text) == "" {
		return false, nil
	}
	q := url.Values{"fileName": {fileName}}
	_, _, err := c.do(http.MethodPost, "/Audio/"+itemID+"/Lyrics", q, []byte(text), "text/plain; charset=utf-8")
	return err == nil, err
}
