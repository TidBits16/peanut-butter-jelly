package tui

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"syscall"

	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"golang.org/x/term"
)

var rows = []string{"apply", "force", "dir", "workers", "start_from", "run", "quit"}

var labels = map[string]string{
	"apply": "Write mode", "force": "Overwrite", "dir": "Music folder",
	"workers": "Workers", "start_from": "Resume at album", "run": "Run", "quit": "Quit",
}

type state struct {
	cfg      *config.Config
	cursor   int
	editing  bool
	editBuf  string
}

func Launch(cfg *config.Config) (*config.Config, bool) {
	if !term.IsTerminal(int(os.Stdin.Fd())) || !term.IsTerminal(int(os.Stdout.Fd())) {
		return cfg, true
	}
	st := &state{cfg: cfg}
	old, err := term.MakeRaw(int(os.Stdin.Fd()))
	if err != nil {
		return cfg, true
	}
	defer term.Restore(int(os.Stdin.Fd()), old)
	fmt.Print("\033[?1049h\033[H")
	defer fmt.Print("\033[?1049l")
	for {
		draw(st)
		key := readKey()
		if st.editing {
			switch key {
			case "\r", "\n":
				commit(st)
			case "\x1b":
				st.editing = false
				st.editBuf = ""
			case "\x7f", "\b":
				r := []rune(st.editBuf)
				if len(r) > 0 {
					st.editBuf = string(r[:len(r)-1])
				}
			case "\x03":
				return nil, false
			default:
				if len(key) == 1 && key[0] >= 32 && key[0] < 127 {
					st.editBuf += key
				}
			}
			continue
		}
		switch key {
		case "q", "Q", "\x03":
			return nil, false
		case "r", "R":
			normalize(st.cfg)
			return st.cfg, true
		case "\x1b[A", "k":
			st.cursor = (st.cursor - 1 + len(rows)) % len(rows)
		case "\x1b[B", "j":
			st.cursor = (st.cursor + 1) % len(rows)
		case " ", "\r", "\n":
			switch rows[st.cursor] {
			case "apply":
				st.cfg.Apply = !st.cfg.Apply
			case "force":
				st.cfg.Force = !st.cfg.Force
			case "dir", "workers", "start_from":
				beginEdit(st)
			case "run":
				normalize(st.cfg)
				return st.cfg, true
			case "quit":
				return nil, false
			}
		}
	}
}

func beginEdit(st *state) {
	st.editing = true
	switch rows[st.cursor] {
	case "dir":
		st.editBuf = st.cfg.MusicDir
	case "workers":
		st.editBuf = strconv.Itoa(st.cfg.Workers)
	case "start_from":
		st.editBuf = strconv.Itoa(st.cfg.StartFrom)
	}
}

func commit(st *state) {
	raw := strings.TrimSpace(st.editBuf)
	switch rows[st.cursor] {
	case "dir":
		if raw != "" {
			st.cfg.MusicDir = raw
		}
	case "workers":
		if n, err := strconv.Atoi(raw); err == nil && n >= 1 {
			st.cfg.Workers = n
		}
	case "start_from":
		if n, err := strconv.Atoi(raw); err == nil && n >= 1 {
			st.cfg.StartFrom = n
		}
	}
	st.editing = false
	st.editBuf = ""
}

func normalize(cfg *config.Config) {
	if cfg.Workers < 1 {
		cfg.Workers = 1
	}
	if cfg.StartFrom < 1 {
		cfg.StartFrom = 1
	}
}

func draw(st *state) {
	fmt.Print("\033[H\033[2J")
	fmt.Println("  ◆ PEANUT BUTTER & JELLY")
	fmt.Println("  deez nuts × jellyfin")
	fmt.Println()
	fmt.Println("  Pick options, then Run. Progress prints after this screen.")
	fmt.Println()
	for i, key := range rows {
		marker := "  "
		if i == st.cursor {
			marker = "▸ "
		}
		val := value(st, key)
		if st.editing && i == st.cursor {
			val = st.editBuf + "█"
		}
		fmt.Printf("  %s%-18s %s\n", marker, labels[key], val)
	}
	fmt.Println()
	fmt.Println("  ↑↓ move   space/enter toggle or edit   r run   q quit")
}

func value(st *state, key string) string {
	switch key {
	case "apply":
		if st.cfg.Apply {
			return "APPLY"
		}
		return "DRY RUN"
	case "force":
		if st.cfg.Force {
			return "FORCE overwrite"
		}
		return "fill missing only"
	case "dir":
		return st.cfg.MusicDir
	case "workers":
		return strconv.Itoa(st.cfg.Workers)
	case "start_from":
		return strconv.Itoa(st.cfg.StartFrom)
	case "run":
		return "start tagging →"
	default:
		return "leave without running"
	}
}

func readKey() string {
	buf := make([]byte, 1)
	n, err := syscall.Read(int(os.Stdin.Fd()), buf)
	if err != nil || n == 0 {
		return ""
	}
	if buf[0] != 0x1b {
		if buf[0] == '\r' {
			return "\n"
		}
		return string(buf[:n])
	}
	rest := make([]byte, 2)
	n2, _ := syscall.Read(int(os.Stdin.Fd()), rest)
	if n2 > 0 {
		return "\x1b" + string(rest[:n2])
	}
	return "\x1b"
}
