package main

import (
	"flag"
	"fmt"
	"os"
	"strings"

	"github.com/TidBits16/peanut-butter-jelly/internal/audio"
	"github.com/TidBits16/peanut-butter-jelly/internal/config"
	"github.com/TidBits16/peanut-butter-jelly/internal/engine"
	"github.com/TidBits16/peanut-butter-jelly/internal/tui"
	"github.com/TidBits16/peanut-butter-jelly/internal/ui"
	"golang.org/x/term"
)

func wantGUI(cfg *config.Config, argv []string) bool {
	if cfg.CLI {
		return false
	}
	if cfg.GUI {
		return true
	}
	if !term.IsTerminal(int(os.Stdin.Fd())) || !term.IsTerminal(int(os.Stdout.Fd())) {
		return false
	}
	for _, a := range argv[1:] {
		if strings.HasPrefix(a, "-") && a != "-h" && a != "--help" {
			return false
		}
	}
	return true
}

func main() {
	cfg, err := config.Load()
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	cfg.MusicDir = audio.DefaultMusicDir()

	fs := flag.NewFlagSet("pbj", flag.ExitOnError)
	fs.BoolVar(&cfg.Apply, "apply", false, "Write changes to Jellyfin and audio files")
	fs.BoolVar(&cfg.Force, "force", false, "Overwrite existing photos, bios, genres, artists, and lyrics")
	fs.StringVar(&cfg.MusicDir, "dir", cfg.MusicDir, "Music library root for file writes")
	fs.IntVar(&cfg.StartFrom, "from", 1, "Resume at album N (1-based)")
	fs.IntVar(&cfg.Workers, "workers", 8, "Parallel lookups and Jellyfin writes")
	fs.BoolVar(&cfg.GUI, "gui", false, "Open the terminal GUI")
	fs.BoolVar(&cfg.GUI, "tui", false, "Open the terminal GUI")
	fs.BoolVar(&cfg.CLI, "cli", false, "Skip the GUI and run immediately")
	fs.Usage = func() {
		fmt.Fprintf(os.Stderr, "Peanut butter & jelly — Deezer genres/artists/explicit + LRCLIB lyrics onto Jellyfin\n\n")
		fmt.Fprintf(os.Stderr, "Dry-run by default. Add --apply to write. Add --force to overwrite existing values.\n\n")
		fs.PrintDefaults()
	}
	_ = fs.Parse(os.Args[1:])

	if wantGUI(cfg, os.Args) {
		picked, ok := tui.Launch(cfg)
		if !ok {
			os.Exit(0)
		}
		cfg = picked
	}

	if err := engine.Run(cfg); err != nil {
		ui.Callout(err.Error(), "bad")
		os.Exit(1)
	}
}
