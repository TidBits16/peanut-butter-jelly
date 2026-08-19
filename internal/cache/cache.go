package cache

import (
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"net/url"
	"os"
	"path/filepath"
	"sync"
	"time"
)

const DefaultTTL = 30 * 24 * time.Hour

type Store struct {
	dir string
	mu  sync.Mutex
}

func New(root string) *Store {
	return &Store{dir: filepath.Join(root, ".mb_cache")}
}

func (s *Store) key(path string, params map[string]string) string {
	q := url.Values{}
	for k, v := range params {
		q.Set(k, v)
	}
	raw := stringsTrim(path) + "?" + q.Encode()
	sum := sha1.Sum([]byte(raw))
	return hex.EncodeToString(sum[:])
}

func stringsTrim(path string) string {
	for len(path) > 0 && path[0] == '/' {
		path = path[1:]
	}
	return path
}

func (s *Store) Get(path string, params map[string]string, dest any, ttl time.Duration) bool {
	if ttl == 0 {
		ttl = DefaultTTL
	}
	fp := filepath.Join(s.dir, s.key(path, params)+".json")
	s.mu.Lock()
	defer s.mu.Unlock()
	st, err := os.Stat(fp)
	if err != nil {
		return false
	}
	if time.Since(st.ModTime()) > ttl {
		return false
	}
	b, err := os.ReadFile(fp)
	if err != nil {
		return false
	}
	return json.Unmarshal(b, dest) == nil
}

func (s *Store) Set(path string, params map[string]string, payload any) {
	_ = os.MkdirAll(s.dir, 0o755)
	fp := filepath.Join(s.dir, s.key(path, params)+".json")
	b, err := json.Marshal(payload)
	if err != nil {
		return
	}
	tmp := fp + ".tmp"
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return
	}
	_ = os.Rename(tmp, fp)
}
