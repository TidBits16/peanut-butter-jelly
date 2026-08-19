package titles

import (
	"strings"

	"github.com/TidBits16/peanut-butter-jelly/internal/config"
)

func StripMark(name string) string {
	if strings.HasSuffix(name, config.ExplicitMark) {
		return name[:len(name)-len(config.ExplicitMark)]
	}
	return name
}

func HasExplicitMark(name string) bool {
	return strings.HasSuffix(name, config.ExplicitMark) || strings.HasSuffix(name, "🅴")
}

func DesiredTitle(name string, explicit bool) string {
	base := StripMark(name)
	if base == "" {
		return ""
	}
	if explicit {
		return base + config.ExplicitMark
	}
	return base
}

func Norm(text string) string {
	s := strings.ToLower(StripMark(text))
	var b strings.Builder
	prevSpace := false
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '&' || r == ' ' {
			if r == ' ' {
				if prevSpace {
					continue
				}
				prevSpace = true
			} else {
				prevSpace = false
			}
			b.WriteRune(r)
			continue
		}
		if !prevSpace {
			b.WriteByte(' ')
			prevSpace = true
		}
	}
	return strings.TrimSpace(b.String())
}
