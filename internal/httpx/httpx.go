package httpx

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"sync"
	"time"

	"github.com/TidBits16/peanut-butter-jelly/internal/cache"
)

type Client struct {
	HTTP     *http.Client
	Cache    *cache.Store
	Headers  map[string]string
	Interval time.Duration
	Hits     int
	HTTPN    int
	mu       sync.Mutex
	last     time.Time
}

func New(store *cache.Store, headers map[string]string, interval time.Duration) *Client {
	return &Client{
		HTTP:     &http.Client{Timeout: 45 * time.Second},
		Cache:    store,
		Headers:  headers,
		Interval: interval,
	}
}

func (c *Client) pace() {
	c.mu.Lock()
	defer c.mu.Unlock()
	wait := c.Interval - time.Since(c.last)
	if wait > 0 {
		time.Sleep(wait)
	}
	c.last = time.Now()
}

func (c *Client) GetJSON(cachePath, rawURL string, params map[string]string, ttl time.Duration) (map[string]any, error) {
	var cached map[string]any
	if c.Cache != nil && c.Cache.Get(cachePath, params, &cached, ttl) {
		c.mu.Lock()
		c.Hits++
		c.mu.Unlock()
		return cached, nil
	}
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, err
	}
	q := u.Query()
	for k, v := range params {
		if v != "" {
			q.Set(k, v)
		}
	}
	u.RawQuery = q.Encode()
	var last error
	for attempt := 0; attempt < 5; attempt++ {
		c.pace()
		req, err := http.NewRequest(http.MethodGet, u.String(), nil)
		if err != nil {
			return nil, err
		}
		for k, v := range c.Headers {
			req.Header.Set(k, v)
		}
		res, err := c.HTTP.Do(req)
		if err != nil {
			last = err
			time.Sleep(time.Duration(attempt+1) * 1500 * time.Millisecond)
			continue
		}
		body, _ := io.ReadAll(res.Body)
		res.Body.Close()
		if res.StatusCode == 404 {
			c.mu.Lock()
			c.HTTPN++
			c.mu.Unlock()
			miss := map[string]any{"_miss": true}
			if c.Cache != nil {
				c.Cache.Set(cachePath, params, miss)
			}
			return miss, nil
		}
		if res.StatusCode == 429 {
			delay := time.Duration(attempt+1) * 1500 * time.Millisecond
			if ra := res.Header.Get("Retry-After"); ra != "" {
				if n, err := strconv.Atoi(ra); err == nil {
					delay = time.Duration(n) * time.Second
				}
			}
			time.Sleep(delay)
			last = fmt.Errorf("429 %s", res.Status)
			continue
		}
		if res.StatusCode >= 500 {
			time.Sleep(time.Duration(attempt+1) * 1500 * time.Millisecond)
			last = fmt.Errorf("%s", res.Status)
			continue
		}
		if res.StatusCode >= 400 {
			return nil, fmt.Errorf("%s %s", res.Status, truncate(string(body), 300))
		}
		var data any
		if err := json.Unmarshal(body, &data); err != nil {
			last = err
			time.Sleep(time.Duration(attempt+1) * 1500 * time.Millisecond)
			continue
		}
		c.mu.Lock()
		c.HTTPN++
		c.mu.Unlock()
		switch v := data.(type) {
		case map[string]any:
			if c.Cache != nil {
				c.Cache.Set(cachePath, params, v)
			}
			return v, nil
		case []any:
			wrapped := map[string]any{"results": v}
			if c.Cache != nil {
				c.Cache.Set(cachePath, params, wrapped)
			}
			return wrapped, nil
		default:
			return map[string]any{}, nil
		}
	}
	if last != nil {
		return nil, last
	}
	return map[string]any{}, nil
}

func (c *Client) GetBytes(rawURL string) ([]byte, string, error) {
	c.pace()
	req, err := http.NewRequest(http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, "", err
	}
	for k, v := range c.Headers {
		req.Header.Set(k, v)
	}
	res, err := c.HTTP.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer res.Body.Close()
	if res.StatusCode >= 400 {
		return nil, "", fmt.Errorf("%s", res.Status)
	}
	b, err := io.ReadAll(res.Body)
	mime := res.Header.Get("Content-Type")
	return b, mime, err
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n]
}

func AsString(v any) string {
	if v == nil {
		return ""
	}
	switch t := v.(type) {
	case string:
		return t
	case fmt.Stringer:
		return t.String()
	case float64:
		if t == float64(int(t)) {
			return strconv.Itoa(int(t))
		}
		return strconv.FormatFloat(t, 'f', -1, 64)
	case json.Number:
		return t.String()
	default:
		return fmt.Sprint(v)
	}
}

func AsBoolPtr(v any) *bool {
	switch t := v.(type) {
	case bool:
		return &t
	case float64:
		b := t != 0
		return &b
	default:
		return nil
	}
}

func AsList(v any) []any {
	if s, ok := v.([]any); ok {
		return s
	}
	return nil
}

func AsMap(v any) map[string]any {
	if m, ok := v.(map[string]any); ok {
		return m
	}
	return map[string]any{}
}
