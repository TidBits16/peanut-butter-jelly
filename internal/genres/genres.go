package genres

import (
	_ "embed"
	"encoding/json"
	"strings"
	"unicode"
)

//go:embed data.json
var dataJSON []byte

var prettyMap map[string]string
var smallWords map[string]struct{}
var hyphenHeads map[string]struct{}

func init() {
	var raw struct {
		Pretty map[string]string `json:"pretty"`
		Small  []string          `json:"small"`
		Hyphen []string          `json:"hyphen"`
	}
	if err := json.Unmarshal(dataJSON, &raw); err != nil {
		panic(err)
	}
	prettyMap = raw.Pretty
	smallWords = make(map[string]struct{}, len(raw.Small))
	for _, w := range raw.Small {
		smallWords[w] = struct{}{}
	}
	hyphenHeads = make(map[string]struct{}, len(raw.Hyphen))
	for _, w := range raw.Hyphen {
		hyphenHeads[w] = struct{}{}
	}
}

var equivalents = map[string]string{
	"darkwave": "dark wave", "coldwave": "cold wave", "hiphop": "hip hop", "hip hop": "hip hop",
	"lofi": "lo-fi", "lo fi": "lo-fi", "rnb": "r&b", "drum & bass": "drum and bass",
	"d&b": "drum and bass", "video game soundtrack": "video game music",
	"game soundtrack": "video game music", "kpop": "k pop", "jpop": "j pop",
	"neosoul": "neo soul", "avantgarde": "avant garde", "singersongwriter": "singer songwriter",
	"singer & songwriter": "singer songwriter", "rap/hip hop": "hip hop",
	"soul & funk": "soul", "films/games": "soundtrack",
}

func NormKey(name string) string {
	s := strings.ToLower(strings.TrimSpace(name))
	s = strings.ReplaceAll(s, "_", " ")
	s = strings.ReplaceAll(s, "-", " ")
	for strings.Contains(s, "  ") {
		s = strings.ReplaceAll(s, "  ", " ")
	}
	if v, ok := equivalents[s]; ok {
		return v
	}
	return s
}

func Pretty(name string) string {
	raw := strings.TrimSpace(name)
	if raw == "" {
		return ""
	}
	key := NormKey(raw)
	if v, ok := prettyMap[key]; ok {
		return v
	}
	words := strings.Fields(key)
	out := make([]string, 0, len(words))
	for i, word := range words {
		if i > 0 {
			if _, ok := smallWords[word]; ok {
				out = append(out, word)
				continue
			}
		}
		switch word {
		case "uk", "us", "tv", "dj", "mc", "dnb", "edm", "idm", "ebm", "ost", "vgm":
			out = append(out, strings.ToUpper(word))
			continue
		case "r&b":
			out = append(out, "R&B")
			continue
		}
		out = append(out, titleWord(word))
	}
	pretty := strings.Join(out, " ")
	parts := strings.Fields(pretty)
	if len(parts) >= 2 {
		if _, ok := hyphenHeads[strings.ToLower(parts[0])]; ok {
			pretty = parts[0] + "-" + parts[1]
			if len(parts) > 2 {
				pretty += " " + strings.Join(parts[2:], " ")
			}
		}
	}
	return pretty
}

func titleWord(word string) string {
	if word == "" {
		return word
	}
	r := []rune(word)
	r[0] = unicode.ToUpper(r[0])
	for i := 1; i < len(r); i++ {
		r[i] = unicode.ToLower(r[i])
	}
	return string(r)
}

func PrettyList(names []string, max int) []string {
	out := make([]string, 0, len(names))
	seen := map[string]struct{}{}
	for _, name := range names {
		p := Pretty(name)
		if p == "" {
			continue
		}
		k := NormKey(p)
		if _, ok := seen[k]; ok {
			continue
		}
		seen[k] = struct{}{}
		out = append(out, p)
		if max > 0 && len(out) >= max {
			break
		}
	}
	return out
}
