package audio

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/TidBits16/peanut-butter-jelly/internal/genres"
	"github.com/bogem/id3v2/v2"
	"github.com/dhowden/tag"
	"github.com/go-flac/flacvorbis"
	flac "github.com/go-flac/go-flac"
)

const AdvisoryExplicit = "1"

var audioExts = map[string]struct{}{
	".mp3": {}, ".flac": {}, ".m4a": {}, ".mp4": {}, ".ogg": {}, ".opus": {},
	".wma": {}, ".aiff": {}, ".aif": {}, ".wv": {},
}

func DefaultMusicDir() string {
	for _, c := range []string{"/media/music", "music"} {
		st, err := os.Stat(c)
		if err == nil && st.IsDir() {
			if filepath.IsAbs(c) {
				return c
			}
			abs, err := filepath.Abs(c)
			if err == nil {
				return abs
			}
			return c
		}
	}
	abs, err := filepath.Abs("music")
	if err != nil {
		return "music"
	}
	return abs
}

func BuildNameIndex(root string) map[string][]string {
	index := map[string][]string{}
	st, err := os.Stat(root)
	if err != nil || !st.IsDir() {
		return index
	}
	_ = filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		ext := strings.ToLower(filepath.Ext(path))
		if _, ok := audioExts[ext]; !ok {
			return nil
		}
		key := strings.ToLower(info.Name())
		index[key] = append(index[key], path)
		return nil
	})
	return index
}

func ResolveLocalPath(jfPath, root string, nameIndex map[string][]string) string {
	raw := strings.TrimSpace(jfPath)
	if raw == "" {
		return ""
	}
	if st, err := os.Stat(raw); err == nil && !st.IsDir() {
		return raw
	}
	parts := splitPath(raw)
	if st, err := os.Stat(root); err == nil && st.IsDir() {
		for i := 0; i < len(parts); i++ {
			cand := filepath.Join(append([]string{root}, parts[i:]...)...)
			if st, err := os.Stat(cand); err == nil && !st.IsDir() {
				return cand
			}
		}
	}
	base := strings.ToLower(filepath.Base(raw))
	hits := nameIndex[base]
	if len(hits) == 1 {
		return hits[0]
	}
	if len(hits) > 1 {
		want := make([]string, len(parts))
		for i, p := range parts {
			want[i] = strings.ToLower(p)
		}
		best, bestScore := "", -1
		for _, hit := range hits {
			got := splitPath(hit)
			for i := range got {
				got[i] = strings.ToLower(got[i])
			}
			score := 0
			for i := 1; i <= len(want) && i <= len(got); i++ {
				if want[len(want)-i] != got[len(got)-i] {
					break
				}
				score++
			}
			if score > bestScore {
				best, bestScore = hit, score
			}
		}
		return best
	}
	return ""
}

func splitPath(p string) []string {
	p = strings.ReplaceAll(p, "\\", "/")
	p = strings.Trim(p, "/")
	if p == "" {
		return nil
	}
	return strings.Split(p, "/")
}

type FileTags struct {
	Title    string
	Genres   []string
	Advisory string
}

func ReadFileTags(path string) (FileTags, bool) {
	f, err := os.Open(path)
	if err != nil {
		return FileTags{}, false
	}
	defer f.Close()
	m, err := tag.ReadFrom(f)
	if err != nil || m == nil {
		return FileTags{}, false
	}
	title := strings.TrimSpace(m.Title())
	var gs []string
	if g := strings.TrimSpace(m.Genre()); g != "" {
		for _, part := range strings.Split(g, ";") {
			part = strings.TrimSpace(part)
			if part != "" {
				gs = append(gs, part)
			}
		}
	}
	advisory := ""
	if raw, ok := m.Raw()["ITUNESADVISORY"]; ok {
		advisory = strings.TrimSpace(asText(raw))
	}
	if advisory == "" {
		if raw, ok := m.Raw()["----:com.apple.iTunes:ITUNESADVISORY"]; ok {
			advisory = strings.TrimSpace(asText(raw))
		}
	}
	return FileTags{Title: title, Genres: genres.PrettyList(gs, 0), Advisory: advisory}, true
}

func asText(v any) string {
	switch t := v.(type) {
	case string:
		return t
	case []byte:
		return string(t)
	case []string:
		if len(t) > 0 {
			return t[0]
		}
	}
	return ""
}

func WriteFileTags(path string, title *string, gs []string, advisory *string) (bool, error) {
	st, err := os.Stat(path)
	if err != nil {
		return false, err
	}
	if st.IsDir() {
		return false, os.ErrNotExist
	}
	f, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		return false, err
	}
	f.Close()
	if title == nil && gs == nil && advisory == nil {
		return false, nil
	}
	ext := strings.ToLower(filepath.Ext(path))
	switch ext {
	case ".flac":
		return writeFLAC(path, title, gs, advisory)
	case ".mp3":
		return writeMP3(path, title, gs, advisory)
	default:
		return false, fmt.Errorf("unsupported audio format for writes (flac/mp3 only): %s", path)
	}
}

func writeFLAC(path string, title *string, gs []string, advisory *string) (bool, error) {
	f, err := flac.ParseFile(path)
	if err != nil {
		return false, err
	}
	var cmt *flacvorbis.MetaDataBlockVorbisComment
	cmtIdx := -1
	for i, meta := range f.Meta {
		if meta.Type == flac.VorbisComment {
			parsed, err := flacvorbis.ParseFromMetaDataBlock(*meta)
			if err != nil {
				return false, err
			}
			cmt = parsed
			cmtIdx = i
			break
		}
	}
	if cmt == nil {
		cmt = flacvorbis.New()
	}
	changed := false
	set := func(key, value string) {
		cur, _ := cmt.Get(key)
		have := ""
		if len(cur) > 0 {
			have = cur[0]
		}
		if have == value {
			return
		}
		_ = replaceVorbis(cmt, key, value)
		changed = true
	}
	if title != nil {
		set(flacvorbis.FIELD_TITLE, *title)
	}
	if gs != nil {
		want := strings.Join(gs, "; ")
		cur, _ := cmt.Get(flacvorbis.FIELD_GENRE)
		have := genres.PrettyList(cur, 0)
		if strings.Join(have, "; ") != strings.Join(gs, "; ") {
			_ = replaceVorbis(cmt, flacvorbis.FIELD_GENRE, want)
			changed = true
		}
	}
	if advisory != nil {
		set("ITUNESADVISORY", *advisory)
	}
	if !changed {
		return false, nil
	}
	block := cmt.Marshal()
	if cmtIdx >= 0 {
		f.Meta[cmtIdx] = &block
	} else {
		f.Meta = append([]*flac.MetaDataBlock{&block}, f.Meta...)
	}
	return true, f.Save(path)
}

func replaceVorbis(cmt *flacvorbis.MetaDataBlockVorbisComment, key, value string) error {
	next := cmt.Comments[:0]
	prefix := strings.ToUpper(key) + "="
	for _, c := range cmt.Comments {
		if strings.HasPrefix(strings.ToUpper(c), prefix) {
			continue
		}
		next = append(next, c)
	}
	cmt.Comments = next
	return cmt.Add(key, value)
}

func writeMP3(path string, title *string, gs []string, advisory *string) (bool, error) {
	tag, err := id3v2.Open(path, id3v2.Options{Parse: true})
	if err != nil {
		return false, err
	}
	defer tag.Close()
	changed := false
	if title != nil && tag.Title() != *title {
		tag.SetTitle(*title)
		changed = true
	}
	if gs != nil {
		want := strings.Join(gs, "; ")
		got := genres.PrettyList([]string{tag.Genre()}, 0)
		if strings.Join(got, "; ") != strings.Join(gs, "; ") {
			tag.SetGenre(want)
			changed = true
		}
	}
	if advisory != nil {
		cur := mp3Advisory(tag)
		if cur != *advisory {
			setMP3Advisory(tag, *advisory)
			changed = true
		}
	}
	if !changed {
		return false, nil
	}
	return true, tag.Save()
}

func mp3Advisory(tag *id3v2.Tag) string {
	frames := tag.GetFrames(tag.CommonID("User defined text information frame"))
	for _, f := range frames {
		ud, ok := f.(id3v2.UserDefinedTextFrame)
		if ok && strings.EqualFold(ud.Description, "ITUNESADVISORY") {
			return strings.TrimSpace(ud.Value)
		}
	}
	return ""
}

func setMP3Advisory(tag *id3v2.Tag, value string) {
	id := tag.CommonID("User defined text information frame")
	frames := tag.GetFrames(id)
	tag.DeleteFrames(id)
	for _, f := range frames {
		ud, ok := f.(id3v2.UserDefinedTextFrame)
		if ok && strings.EqualFold(ud.Description, "ITUNESADVISORY") {
			continue
		}
		tag.AddFrame(id, f)
	}
	tag.AddUserDefinedTextFrame(id3v2.UserDefinedTextFrame{
		Description: "ITUNESADVISORY",
		Value:       value,
	})
}
