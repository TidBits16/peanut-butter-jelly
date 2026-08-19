package config

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

const (
	Version      = "4.0"
	ExplicitMark = " 🅴"
	NoMatchTag   = "DeezerNoMatch"
	DeezerBase   = "https://api.deezer.com"
	LRCLibBase   = "https://lrclib.net"
	AudioDBBase  = "https://www.theaudiodb.com/api/v1/json/2"
	WikiAPI      = "https://en.wikipedia.org/w/api.php"
	WikiSummary  = "https://en.wikipedia.org/api/rest_v1/page/summary"
	WikidataAPI  = "https://www.wikidata.org/w/api.php"
)

type Config struct {
	Root      string
	Jellyfin  string
	APIKey    string
	User      string
	UserAgent string
	Apply     bool
	Force     bool
	MusicDir  string
	StartFrom int
	Workers   int
	GUI       bool
	CLI       bool
}

func FindRoot() string {
	if wd, err := os.Getwd(); err == nil {
		if _, err := os.Stat(filepath.Join(wd, ".env")); err == nil {
			return wd
		}
		if _, err := os.Stat(filepath.Join(wd, "go.mod")); err == nil {
			return wd
		}
	}
	exe, err := os.Executable()
	if err == nil {
		return filepath.Dir(exe)
	}
	wd, _ := os.Getwd()
	return wd
}

func LoadEnv(root string) error {
	f, err := os.Open(filepath.Join(root, ".env"))
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") || !strings.Contains(line, "=") {
			continue
		}
		k, v, _ := strings.Cut(line, "=")
		k = strings.TrimSpace(k)
		v = strings.Trim(strings.TrimSpace(v), `"'`)
		if os.Getenv(k) == "" {
			_ = os.Setenv(k, v)
		}
	}
	return sc.Err()
}

func Load() (*Config, error) {
	root := FindRoot()
	if err := LoadEnv(root); err != nil {
		return nil, err
	}
	cfg := &Config{
		Root:      root,
		Jellyfin:  strings.TrimRight(os.Getenv("JELLYFIN_URL"), "/"),
		APIKey:    os.Getenv("JELLYFIN_API_KEY"),
		User:      os.Getenv("JELLYFIN_USER"),
		StartFrom: 1,
		Workers:   8,
	}
	if cfg.User == "" {
		cfg.User = "admin"
	}
	if cfg.Jellyfin == "" || cfg.APIKey == "" {
		return nil, fmt.Errorf("set JELLYFIN_URL and JELLYFIN_API_KEY in a .env file (see .env.example)")
	}
	cfg.UserAgent = fmt.Sprintf("peanut-butter-jelly/%s (%s)", Version, cfg.Jellyfin)
	return cfg, nil
}
