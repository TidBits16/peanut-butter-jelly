"""LRCLIB timed-lyrics lookups (public API, disk-cached, paced)."""

from __future__ import annotations

import re
import time
from dataclasses import dataclass
from pathlib import Path

import requests
from requests.adapters import HTTPAdapter

from mb_tagger.constants import LRCLIB_BASE, LRCLIB_HEADERS
from mb_tagger.mb_cache import cache_get, cache_set
from mb_tagger.pacer import Pacer
from mb_tagger.titles import strip_mark

_http_requests = 0
_cache_hits = 0
_MIN_INTERVAL = 0.25
_MISS_TTL = 60 * 60 * 24 * 7
_pacer = Pacer(_MIN_INTERVAL)
_SYNC_RE = re.compile(r"\[\d{1,2}:\d{2}")
_PAREN_TAIL = re.compile(r"\s*\([^)]*\)\s*$")
_session: requests.Session | None = None


def lrc_stats() -> dict[str, int]:
    return {"lrclib_http": _http_requests, "lrclib_cache_hits": _cache_hits}


def reset_lrc_stats() -> None:
    global _http_requests, _cache_hits
    _http_requests = 0
    _cache_hits = 0


def _client() -> requests.Session:
    global _session
    if _session is None:
        _session = requests.Session()
        _session.headers.update(LRCLIB_HEADERS)
        adapter = HTTPAdapter(pool_connections=8, pool_maxsize=8)
        _session.mount("http://", adapter)
        _session.mount("https://", adapter)
    return _session


@dataclass
class LrcMatch:
    synced: str | None = None
    plain: str | None = None
    instrumental: bool = False
    source: str = "no-match"
    track_name: str = ""


def _clean(text: str | None) -> str | None:
    value = (text or "").replace("\r\n", "\n").replace("\r", "\n").strip()
    return value or None


def _is_synced(text: str | None) -> bool:
    return bool(text and _SYNC_RE.search(text))


def _from_payload(payload: dict, source: str) -> LrcMatch:
    synced = _clean(str(payload.get("syncedLyrics") or ""))
    plain = _clean(str(payload.get("plainLyrics") or ""))
    if synced and not _is_synced(synced):
        if not plain:
            plain = synced
        synced = None
    return LrcMatch(
        synced=synced,
        plain=plain,
        instrumental=bool(payload.get("instrumental")),
        source=source,
        track_name=str(payload.get("trackName") or payload.get("name") or ""),
    )


def _usable(match: LrcMatch) -> bool:
    return bool(match.instrumental or match.synced or match.plain)


def _get(path: str, params: dict[str, str]) -> dict | list | None:
    """GET with disk cache, pacing, and Retry-After on 429."""
    global _http_requests, _cache_hits
    cache_path = f"lrclib/{path.lstrip('/')}"
    cached = cache_get(cache_path, params, ttl=_MISS_TTL)
    if cached is not None:
        _cache_hits += 1
        if cached.get("_miss"):
            return None
        if "results" in cached:
            return cached["results"]
        return cached

    url = f"{LRCLIB_BASE}/{path.lstrip('/')}"
    last_err: Exception | None = None
    for attempt in range(5):
        _pacer.wait()
        try:
            res = _client().get(url, params=params or None, timeout=30)
            if res.status_code == 404:
                _http_requests += 1
                cache_set(cache_path, params, {"_miss": True})
                return None
            if res.status_code == 429:
                wait = res.headers.get("Retry-After")
                try:
                    delay = max(1.0, float(wait))
                except (TypeError, ValueError):
                    delay = 1.5 * (attempt + 1)
                time.sleep(delay)
                last_err = requests.HTTPError(f"429 {res.reason}", response=res)
                continue
            if res.status_code in {500, 502, 503, 504}:
                time.sleep(1.5 * (attempt + 1))
                last_err = requests.HTTPError(f"{res.status_code} {res.reason}", response=res)
                continue
            res.raise_for_status()
            data = res.json()
            _http_requests += 1
            if isinstance(data, list):
                cache_set(cache_path, params, {"results": data})
                return data
            if isinstance(data, dict):
                cache_set(cache_path, params, data)
                return data
            return None
        except (requests.RequestException, ValueError) as exc:
            last_err = exc
            time.sleep(1.5 * (attempt + 1))
    if last_err:
        raise last_err
    return None


def _duration_ok(got: object, want: int | None) -> bool:
    if want is None:
        return True
    try:
        value = float(got)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return False
    return abs(value - want) <= 2


def _pick_search(results: list, *, duration: int | None) -> LrcMatch | None:
    best: LrcMatch | None = None
    best_score = -1.0
    for raw in results:
        if not isinstance(raw, dict):
            continue
        if duration is not None and not _duration_ok(raw.get("duration"), duration):
            continue
        match = _from_payload(raw, source=f"search:{raw.get('id')}")
        if not _usable(match):
            continue
        score = 3.0 if match.synced else 1.0
        if duration is not None:
            try:
                score -= abs(float(raw.get("duration") or 0) - duration) / 10
            except (TypeError, ValueError):
                pass
        if score > best_score:
            best, best_score = match, score
    return best


def _title_variants(title: str) -> list[str]:
    variants: list[str] = []
    current = strip_mark(title or "").strip()
    while current:
        if current not in variants:
            variants.append(current)
        nxt = _PAREN_TAIL.sub("", current).strip()
        if nxt == current:
            break
        current = nxt
    return variants


def lookup_lyrics(
    *,
    title: str,
    artist: str,
    album: str = "",
    duration: int | None = None,
) -> LrcMatch:
    """Best-effort LRCLIB match. Prefers synced LRC, then unsynced plain text."""
    title = strip_mark(title or "").strip()
    artist = (artist or "").strip()
    album = strip_mark(album or "").strip()
    if not title or not artist:
        return LrcMatch(source="no-match")

    titles = _title_variants(title)
    fallback: LrcMatch | None = None

    def take(match: LrcMatch) -> LrcMatch | None:
        nonlocal fallback
        if match.synced:
            return match
        if match.instrumental and not match.plain and match.source.startswith("get"):
            return match
        if match.plain and fallback is None:
            fallback = match
        return None

    for name in titles:
        params: dict[str, str] = {
            "track_name": name,
            "artist_name": artist,
        }
        if album:
            params["album_name"] = album
        if duration is not None:
            params["duration"] = str(duration)
        payload = _get("api/get", params)
        if isinstance(payload, dict) and not payload.get("_miss"):
            done = take(_from_payload(payload, source=f"get:{payload.get('id')}"))
            if done is not None:
                return done

    if album:
        for name in titles:
            params = {"track_name": name, "artist_name": artist}
            if duration is not None:
                params["duration"] = str(duration)
            payload = _get("api/get", params)
            if isinstance(payload, dict) and not payload.get("_miss"):
                done = take(_from_payload(payload, source=f"get-noalbum:{payload.get('id')}"))
                if done is not None:
                    return done

    results = _get("api/search", {"track_name": titles[0], "artist_name": artist})
    if isinstance(results, list):
        picked = _pick_search(results, duration=duration)
        if picked is not None:
            done = take(picked)
            if done is not None:
                return done

    return fallback or LrcMatch(source="no-match")


def lookup_lyrics_for_artists(
    *,
    title: str,
    artists: list[str],
    album: str = "",
    duration: int | None = None,
) -> LrcMatch:
    """Try each artist until a synced (or usable) match appears."""
    if not artists:
        return LrcMatch(source="no-match")
    match = lookup_lyrics(title=title, artist=artists[0], album=album, duration=duration)
    for artist in artists[1:]:
        if match.synced or (match.instrumental and not match.plain):
            break
        candidate = lookup_lyrics(title=title, artist=artist, album=album, duration=duration)
        if candidate.synced:
            return candidate
        if match.source == "no-match" or (candidate.plain and not match.synced):
            match = candidate
    return match


_BRACKET_ARTIST = re.compile(r"^\[.*\]$")
_FANCY_HYPHEN = dict.fromkeys(map(ord, "\u2010\u2011\u2012\u2013\u2014"), "-")


def lyric_query_artists(item: dict, extra: list[str] | None = None) -> list[str]:
    """Artist names for LRCLIB: extras first (Deezer), then Jellyfin, skip [genre] tags."""
    names: list[str] = []
    seen: set[str] = set()

    def add(value: object) -> None:
        text = str(value or "").strip()
        if not text or _BRACKET_ARTIST.match(text):
            return
        for candidate in (text, text.translate(_FANCY_HYPHEN)):
            key = candidate.lower()
            if key in seen:
                continue
            seen.add(key)
            names.append(candidate)

    for name in extra or []:
        add(name)
    for name in item.get("Artists") or []:
        add(name)
    add(item.get("AlbumArtist"))
    return names


def track_duration_seconds(item: dict) -> int | None:
    ticks = item.get("RunTimeTicks")
    if not ticks:
        return None
    try:
        seconds = int(round(int(ticks) / 10_000_000))
    except (TypeError, ValueError):
        return None
    if 1 <= seconds <= 3600:
        return seconds
    return None


def lyrics_filename(jf_path: str, ext: str) -> str:
    name = Path(jf_path or "").name
    if name:
        return str(Path(name).with_suffix(ext))
    return f"lyrics{ext}"
