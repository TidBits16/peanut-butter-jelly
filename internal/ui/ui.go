package ui

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

const (
	cPeanut = "\033[1;33m"
	cJelly  = "\033[1;35m"
	cAccent = "\033[1;36m"
	cOK     = "\033[1;32m"
	cWarn   = "\033[1;33m"
	cBad    = "\033[1;31m"
	cMute   = "\033[90m"
	cPath   = "\033[97m"
	cReset  = "\033[0m"
	cInv    = "\033[7m"
)

func color(c, s string) string { return c + s + cReset }

func Badge(label, kind string) string {
	switch kind {
	case "apply":
		return "\033[1;30;42m " + label + " " + cReset
	case "dry":
		return color(cAccent, " "+label+" ")
	case "ok":
		return color(cOK, " "+label+" ")
	case "bad":
		return color(cBad, " "+label+" ")
	case "miss":
		return "\033[1;37;41m " + label + " " + cReset
	default:
		return color(cMute, " "+label+" ")
	}
}

func PrintBanner(apply, force bool, root string, workers int, startFrom int) {
	fmt.Fprintf(os.Stderr, "\n  %s × %s  v4.0\n", color(cPeanut, "peanut"), color(cJelly, "jelly"))
	mode := "DRY RUN"
	if apply {
		mode = "APPLY"
	}
	write := "fill missing"
	if force {
		write = "FORCE overwrite"
	}
	fmt.Fprintf(os.Stderr, "  %s  %s  workers %s  %s\n\n", Badge(mode, map[bool]string{true: "apply", false: "dry"}[apply]), write, color(cAccent, fmt.Sprintf("%d", workers)), color(cMute, root))
	if startFrom > 1 {
		fmt.Fprintf(os.Stderr, "  resume at album %d\n\n", startFrom)
	}
}

func PrintLibrary(user string, tracks, albums, artists int) {
	fmt.Fprintf(os.Stderr, "  library  user=%s  tracks=%d  albums=%d  artists=%d\n\n", user, tracks, albums, artists)
}

func Section(name, hint string) {
	fmt.Fprintf(os.Stderr, "\n  %s  %s\n", color(cAccent, strings.ToUpper(name)), color(cMute, hint))
}

func Callout(msg, kind string) {
	col := cMute
	switch kind {
	case "ok":
		col = cOK
	case "warn":
		col = cWarn
	case "bad":
		col = cBad
	}
	fmt.Fprintf(os.Stderr, "  %s\n", color(col, msg))
}

func Event(kind, path, detail string) {
	b := Badge(strings.ToUpper(kind), kind)
	if kind == "miss" {
		b = Badge("MISS", "miss")
	}
	if kind == "fail" {
		b = Badge("FAIL", "bad")
	}
	if kind == "dry" {
		b = Badge("DRY RUN", "dry")
	}
	fmt.Fprintf(os.Stderr, "  %s  %s  %s\n", b, color(cPath, path), color(cMute, detail))
}

type Progress struct {
	mu    sync.Mutex
	label string
	done  int
	total int
	start time.Time
}

func NewProgress(label string, total int) *Progress {
	p := &Progress{label: label, total: total, start: time.Now()}
	p.draw()
	return p
}

func (p *Progress) Add(n int) {
	p.mu.Lock()
	p.done += n
	p.draw()
	p.mu.Unlock()
}

func (p *Progress) Done() {
	p.mu.Lock()
	fmt.Fprint(os.Stderr, "\n")
	p.mu.Unlock()
}

func (p *Progress) draw() {
	pct := 0
	if p.total > 0 {
		pct = p.done * 100 / p.total
	}
	elapsed := time.Since(p.start).Truncate(time.Second)
	fmt.Fprintf(os.Stderr, "\r  %s  %d/%d  %d%%  %s   ", color(cAccent, p.label), p.done, p.total, pct, elapsed)
}

func PrintGap(albums [][3]string, artists map[string][]string, lyrics map[string][]string, fails [][2]string) {
	if len(albums) == 0 && len(artists) == 0 && len(lyrics) == 0 && len(fails) == 0 {
		return
	}
	Section("needs you", "manual list")
	if len(albums) > 0 {
		fmt.Fprintf(os.Stderr, "  albums (%d)\n", len(albums))
		for _, a := range albums {
			fmt.Fprintf(os.Stderr, "    %s  %s  %s tracks\n", a[0], color(cMute, a[1]), a[2])
		}
	}
	if len(artists) > 0 {
		names := make([]string, 0, len(artists))
		for n := range artists {
			names = append(names, n)
		}
		sort.Strings(names)
		fmt.Fprintf(os.Stderr, "  artists (%d)\n", len(names))
		for _, n := range names {
			fmt.Fprintf(os.Stderr, "    %s  %s\n", n, color(cMute, strings.Join(artists[n], " ")))
		}
	}
	if len(lyrics) > 0 {
		names := make([]string, 0, len(lyrics))
		for n := range lyrics {
			names = append(names, n)
		}
		sort.Strings(names)
		fmt.Fprintf(os.Stderr, "  lyrics (%d albums)\n", len(names))
		for _, n := range names {
			fmt.Fprintf(os.Stderr, "    %s  %s\n", n, color(cMute, strings.Join(lyrics[n], ", ")))
		}
	}
	if len(fails) > 0 {
		fmt.Fprintf(os.Stderr, "  fails (%d)\n", len(fails))
		for _, f := range fails {
			fmt.Fprintf(os.Stderr, "    %s  %s\n", f[0], color(cBad, f[1]))
		}
	}
}

func PrintSummary(stats map[string]int) {
	Section("summary", "peanut butter & jelly")
	keys := make([]string, 0, len(stats))
	for k := range stats {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		if stats[k] == 0 {
			continue
		}
		fmt.Fprintf(os.Stderr, "  %-24s %d\n", k, stats[k])
	}
	fmt.Fprintln(os.Stderr)
}

func Rel(path, root string) string {
	rel, err := filepath.Rel(root, path)
	if err != nil {
		return path
	}
	return rel
}
