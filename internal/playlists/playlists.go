package playlists

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/TidBits16/peanut-butter-jelly/internal/jellyfin"
	"github.com/TidBits16/peanut-butter-jelly/internal/titles"
)

const musicMarker = "media/music/"

var cleanupRE = regexp.MustCompile(`Item in "([^"]+)" cannot be found at "([^"]+)"`)

type Entry struct {
	Path    string   `json:"path"`
	ItemID  string   `json:"item_id"`
	Name    string   `json:"name"`
	Album   string   `json:"album"`
	Artists []string `json:"artists"`
}

type State struct {
	PlaylistID string  `json:"playlist_id"`
	Name       string  `json:"name"`
	OwnerUID   string  `json:"owner_uid"`
	Entries    []Entry `json:"entries"`
}

type Plan struct {
	PlaylistID string
	Name       string
	OwnerUID   string
	DesiredIDs []string
	LiveIDs    []string
	Missing    int
	Source     string
}

func (p Plan) NeedsWrite() bool {
	if len(p.DesiredIDs) != len(p.LiveIDs) {
		return true
	}
	for i := range p.DesiredIDs {
		if p.DesiredIDs[i] != p.LiveIDs[i] {
			return true
		}
	}
	return false
}

func SnapshotPath(root string) string {
	return filepath.Join(root, ".playlist_snapshot.json")
}

func afterMusic(path string) string {
	raw := strings.TrimSpace(strings.ReplaceAll(path, "\\", "/"))
	lower := strings.ToLower(raw)
	if idx := strings.Index(lower, musicMarker); idx >= 0 {
		return raw[idx+len(musicMarker):]
	}
	return strings.TrimPrefix(raw, "/")
}

func userRoots(tracks []jellyfin.Item) map[string]struct{} {
	roots := map[string]struct{}{}
	for _, item := range tracks {
		rel := afterMusic(item.Path())
		first := ""
		if i := strings.Index(rel, "/"); i >= 0 {
			first = strings.ToLower(strings.TrimSpace(rel[:i]))
		} else {
			first = strings.ToLower(strings.TrimSpace(rel))
		}
		if first != "" {
			roots[first] = struct{}{}
		}
	}
	return roots
}

func libraryRelative(path string, roots map[string]struct{}) string {
	rel := afterMusic(path)
	parts := []string{}
	for _, p := range strings.Split(rel, "/") {
		if p != "" {
			parts = append(parts, p)
		}
	}
	if len(parts) > 0 {
		if _, ok := roots[strings.ToLower(parts[0])]; ok {
			parts = parts[1:]
		}
	}
	return strings.ToLower(strings.Join(parts, "/"))
}

func entryFromItem(item jellyfin.Item) Entry {
	return Entry{
		Path:    item.Path(),
		ItemID:  item.ID(),
		Name:    item.Name(),
		Album:   item.Album(),
		Artists: item.Artists(),
	}
}

func LoadSnapshot(root string) map[string]State {
	out := map[string]State{}
	b, err := os.ReadFile(SnapshotPath(root))
	if err != nil {
		return out
	}
	var data struct {
		Playlists []State `json:"playlists"`
	}
	if json.Unmarshal(b, &data) != nil {
		return out
	}
	for _, state := range data.Playlists {
		if state.PlaylistID != "" {
			out[state.PlaylistID] = state
		}
		if state.Name != "" {
			if _, ok := out[strings.ToLower(state.Name)]; !ok {
				out[strings.ToLower(state.Name)] = state
			}
		}
	}
	return out
}

func SaveSnapshot(root string, states []State) error {
	type payload struct {
		SavedAt   string  `json:"saved_at"`
		Playlists []State `json:"playlists"`
	}
	b, err := json.MarshalIndent(payload{SavedAt: time.Now().UTC().Format(time.RFC3339), Playlists: states}, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(SnapshotPath(root), append(b, '\n'), 0o644)
}

func salvageFromLogs(jf *jellyfin.Client) map[string][]Entry {
	byName := map[string][]Entry{}
	seen := map[string]map[string]struct{}{}
	logs, err := jf.Logs()
	if err != nil {
		return byName
	}
	for _, name := range logs {
		text, err := jf.LogText(name)
		if err != nil {
			continue
		}
		for _, m := range cleanupRE.FindAllStringSubmatch(text, -1) {
			plName, path := m[1], m[2]
			key := strings.ToLower(plName)
			used := seen[key]
			if used == nil {
				used = map[string]struct{}{}
				seen[key] = used
			}
			if _, ok := used[path]; ok {
				continue
			}
			used[path] = struct{}{}
			byName[key] = append(byName[key], Entry{Path: path})
		}
	}
	return byName
}

func fetchLive(jf *jellyfin.Client, uid string, hints map[string]string) ([]State, error) {
	userIDs, err := jf.UserIDs(uid)
	if err != nil {
		return nil, err
	}
	pls, err := jf.Playlists()
	if err != nil {
		return nil, err
	}
	var states []State
	for _, item := range pls {
		pid, name := item.ID(), item.Name()
		if name == "" {
			name = pid
		}
		ordered := []string{}
		if h := hints[pid]; h != "" {
			ordered = append(ordered, h)
		}
		if h := hints[strings.ToLower(name)]; h != "" {
			dup := false
			for _, x := range ordered {
				if x == h {
					dup = true
					break
				}
			}
			if !dup {
				ordered = append(ordered, h)
			}
		}
		for _, cand := range userIDs {
			dup := false
			for _, x := range ordered {
				if x == cand {
					dup = true
					break
				}
			}
			if !dup {
				ordered = append(ordered, cand)
			}
		}
		owner := uid
		var children []jellyfin.Item
		for _, cand := range ordered {
			got, err := jf.PlaylistItems(pid, cand)
			if err != nil {
				continue
			}
			if got != nil {
				owner = cand
				children = got
				break
			}
		}
		entries := make([]Entry, 0, len(children))
		for _, child := range children {
			entries = append(entries, entryFromItem(child))
		}
		states = append(states, State{PlaylistID: pid, Name: name, OwnerUID: owner, Entries: entries})
	}
	return states, nil
}

func coalesce(live []State, snapshot map[string]State, salvage map[string][]Entry) []State {
	var desired []State
	for _, pl := range live {
		entries := append([]Entry{}, pl.Entries...)
		snap := snapshot[pl.PlaylistID]
		if len(snap.Entries) == 0 {
			snap = snapshot[strings.ToLower(pl.Name)]
		}
		if len(entries) == 0 && len(snap.Entries) > 0 {
			entries = append([]Entry{}, snap.Entries...)
		}
		if len(entries) == 0 {
			entries = append([]Entry{}, salvage[strings.ToLower(pl.Name)]...)
		}
		desired = append(desired, State{PlaylistID: pl.PlaylistID, Name: pl.Name, OwnerUID: pl.OwnerUID, Entries: entries})
	}
	return desired
}

func resolveEntry(entry Entry, byID map[string]jellyfin.Item, byRel map[string]jellyfin.Item, byBase map[string][]jellyfin.Item, roots map[string]struct{}) (jellyfin.Item, bool) {
	if entry.ItemID != "" {
		if it, ok := byID[entry.ItemID]; ok {
			return it, true
		}
	}
	if entry.Path != "" {
		rel := libraryRelative(entry.Path, roots)
		if it, ok := byRel[rel]; ok {
			return it, true
		}
		base := strings.ToLower(filepath.Base(strings.ReplaceAll(entry.Path, "\\", "/")))
		names := byBase[base]
		if len(names) == 1 {
			return names[0], true
		}
	}
	if entry.Name != "" {
		want := strings.ToLower(strings.TrimSpace(titles.StripMark(entry.Name)))
		wantAlbum := strings.ToLower(strings.TrimSpace(titles.StripMark(entry.Album)))
		var hits []jellyfin.Item
		for _, item := range byID {
			if strings.ToLower(strings.TrimSpace(titles.StripMark(item.Name()))) != want {
				continue
			}
			if wantAlbum != "" && strings.ToLower(strings.TrimSpace(titles.StripMark(item.Album()))) != wantAlbum {
				continue
			}
			hits = append(hits, item)
		}
		if len(hits) == 1 {
			return hits[0], true
		}
	}
	return nil, false
}

func PlanRepair(jf *jellyfin.Client, uid string, tracks []jellyfin.Item, root string) ([]Plan, []State, error) {
	snapshot := LoadSnapshot(root)
	hints := map[string]string{}
	for _, state := range snapshot {
		if state.OwnerUID == "" {
			continue
		}
		if state.PlaylistID != "" {
			hints[state.PlaylistID] = state.OwnerUID
		}
		if state.Name != "" {
			hints[strings.ToLower(state.Name)] = state.OwnerUID
		}
	}
	live, err := fetchLive(jf, uid, hints)
	if err != nil {
		return nil, nil, err
	}
	needSalvage := false
	for _, pl := range live {
		snap := snapshot[pl.PlaylistID]
		if len(snap.Entries) == 0 {
			snap = snapshot[strings.ToLower(pl.Name)]
		}
		if len(pl.Entries) == 0 && len(snap.Entries) == 0 {
			needSalvage = true
			break
		}
	}
	salvage := map[string][]Entry{}
	if needSalvage {
		salvage = salvageFromLogs(jf)
	}
	desired := coalesce(live, snapshot, salvage)
	roots := userRoots(tracks)
	byID := map[string]jellyfin.Item{}
	byRel := map[string]jellyfin.Item{}
	byBase := map[string][]jellyfin.Item{}
	for _, item := range tracks {
		if item.ID() != "" {
			byID[item.ID()] = item
		}
		if p := item.Path(); p != "" {
			byRel[libraryRelative(p, roots)] = item
			base := strings.ToLower(filepath.Base(strings.ReplaceAll(p, "\\", "/")))
			byBase[base] = append(byBase[base], item)
		}
	}
	liveByID := map[string]State{}
	for _, p := range live {
		liveByID[p.PlaylistID] = p
	}
	var plans []Plan
	var resolved []State
	for _, pl := range desired {
		var found []Entry
		var ids []string
		missing := 0
		seenID := map[string]struct{}{}
		for _, entry := range pl.Entries {
			item, ok := resolveEntry(entry, byID, byRel, byBase, roots)
			if !ok || item.ID() == "" {
				missing++
				continue
			}
			resolvedEntry := entryFromItem(item)
			if _, ok := seenID[resolvedEntry.ItemID]; ok {
				continue
			}
			seenID[resolvedEntry.ItemID] = struct{}{}
			ids = append(ids, resolvedEntry.ItemID)
			found = append(found, resolvedEntry)
		}
		liveIDs := []string{}
		for _, e := range liveByID[pl.PlaylistID].Entries {
			if e.ItemID != "" {
				liveIDs = append(liveIDs, e.ItemID)
			}
		}
		source := "empty"
		if len(liveByID[pl.PlaylistID].Entries) > 0 {
			source = "live"
		} else if snap := snapshot[pl.PlaylistID]; len(snap.Entries) > 0 {
			source = "snapshot"
		} else if snap := snapshot[strings.ToLower(pl.Name)]; len(snap.Entries) > 0 {
			source = "snapshot"
		} else if len(salvage[strings.ToLower(pl.Name)]) > 0 {
			source = "log"
		}
		plans = append(plans, Plan{
			PlaylistID: pl.PlaylistID, Name: pl.Name, OwnerUID: pl.OwnerUID,
			DesiredIDs: ids, LiveIDs: liveIDs, Missing: missing, Source: source,
		})
		keep := found
		if len(keep) == 0 {
			keep = pl.Entries
		}
		resolved = append(resolved, State{PlaylistID: pl.PlaylistID, Name: pl.Name, OwnerUID: pl.OwnerUID, Entries: keep})
	}
	return plans, resolved, nil
}

func Apply(jf *jellyfin.Client, uid string, plan Plan) error {
	if !plan.NeedsWrite() {
		return nil
	}
	owner := plan.OwnerUID
	if owner == "" {
		owner = uid
	}
	live, err := jf.PlaylistItems(plan.PlaylistID, owner)
	if err != nil {
		return err
	}
	var entryIDs []string
	for _, item := range live {
		if id := item.PlaylistItemID(); id != "" {
			entryIDs = append(entryIDs, id)
		}
	}
	if err := jf.RemovePlaylistEntries(plan.PlaylistID, entryIDs); err != nil {
		return err
	}
	return jf.AddPlaylistItems(plan.PlaylistID, owner, plan.DesiredIDs)
}
