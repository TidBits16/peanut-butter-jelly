"""Deezer album genre + per-track explicit lookups (public API, disk-cached)."""

from __future__ import annotations

import re
import time
from dataclasses import dataclass, field
from difflib import SequenceMatcher
from urllib.parse import parse_qs, urlparse

import requests
from requests.adapters import HTTPAdapter

from mb_tagger.constants import DEEZER_BASE, DEEZER_HEADERS
from mb_tagger.genres import pretty_genres
from mb_tagger.mb_cache import cache_get, cache_set
from mb_tagger.pacer import Pacer
from mb_tagger.titles import strip_mark

_dz_http_requests = 0
_dz_cache_hits = 0
_MIN_INTERVAL = 0.12
_SKIP_GENRES = {"unclassified", "unknown", "other", "none"}
_PAREN_TAIL = re.compile(r"\s*\([^)]*\)\s*$")
_album_memo: dict[tuple[str, str], "DeezerAlbumMatch"] = {}
_album_id_memo: dict[int, "DeezerAlbumMatch"] = {}
_artist_search_memo: dict[str, "DeezerArtistInfo | None"] = {}
_dz_session: requests.Session | None = None
_pacer = Pacer(_MIN_INTERVAL)


def dz_stats() -> dict[str, int]:
    return {"deezer_http": _dz_http_requests, "deezer_cache_hits": _dz_cache_hits}


def reset_dz_stats() -> None:
    global _dz_http_requests, _dz_cache_hits
    _dz_http_requests = 0
    _dz_cache_hits = 0
    _album_memo.clear()
    _album_id_memo.clear()
    _artist_search_memo.clear()


def _session() -> requests.Session:
    global _dz_session
    if _dz_session is None:
        _dz_session = requests.Session()
        _dz_session.headers.update(DEEZER_HEADERS)
        adapter = HTTPAdapter(pool_connections=8, pool_maxsize=8)
        _dz_session.mount("http://", adapter)
        _dz_session.mount("https://", adapter)
    return _dz_session


@dataclass
class DeezerTrack:
    title: str
    explicit: bool | None = None
    artists: list[str] = field(default_factory=list)


@dataclass
class DeezerArtistInfo:
    name: str
    artist_id: int | None = None
    picture: str = ""


@dataclass
class DeezerAlbumMatch:
    genres: list[str] = field(default_factory=list)
    source: str = "no-match"
    album_id: int | None = None
    title: str = ""
    album_artist: str = ""
    artists: list[str] = field(default_factory=list)
    artist_infos: list[DeezerArtistInfo] = field(default_factory=list)
    tracks: list[DeezerTrack] = field(default_factory=list)
    explicit: bool | None = None


def _norm(text: str) -> str:
    s = strip_mark(text or "").lower()
    s = re.sub(r"[^\w\s&]+", " ", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def _album_variants(album: str) -> list[str]:
    variants: list[str] = []
    current = (album or "").strip()
    while current:
        if current not in variants:
            variants.append(current)
        nxt = _PAREN_TAIL.sub("", current).strip()
        if nxt == current:
            break
        current = nxt
    return variants


def _quote(text: str) -> str:
    return (text or "").replace('"', "").strip()


def dz_get(path: str, params: dict | None = None) -> dict:
    """Deezer GET with disk cache, pacing, and retries on 429/5xx."""
    global _dz_http_requests, _dz_cache_hits
    params = dict(params or {})
    cache_path = f"deezer/{path.lstrip('/')}"
    cached = cache_get(cache_path, params)
    if cached is not None:
        _dz_cache_hits += 1
        return cached

    url = f"{DEEZER_BASE}/{path.lstrip('/')}"
    last_err: Exception | None = None
    for attempt in range(5):
        _pacer.wait()
        try:
            res = _session().get(url, params=params or None, timeout=30)
            if res.status_code in {429, 500, 502, 503, 504}:
                time.sleep(1.5 * (attempt + 1))
                last_err = requests.HTTPError(f"{res.status_code} {res.reason}", response=res)
                continue
            res.raise_for_status()
            data = res.json()
            if not isinstance(data, dict):
                return {}
            if data.get("error"):
                return data
            _dz_http_requests += 1
            cache_set(cache_path, params, data)
            return data
        except (requests.RequestException, ValueError) as exc:
            last_err = exc
            time.sleep(1.5 * (attempt + 1))
    if last_err:
        raise last_err
    return {}


def download_image(url: str) -> tuple[bytes, str]:
    """Fetch a Deezer CDN image. Returns (bytes, content-type)."""
    res = _session().get(url, timeout=30)
    res.raise_for_status()
    mime = (res.headers.get("Content-Type") or "image/jpeg").split(";")[0].strip().lower()
    if mime in {"image/jpg", "jpg", "jpeg"} or not mime.startswith("image/"):
        mime = "image/jpeg"
    return res.content, mime


def _picture_url(payload: dict) -> str:
    return str(
        payload.get("picture_xl")
        or payload.get("picture_big")
        or payload.get("picture")
        or ""
    ).strip()


def _artist_infos(payload: dict) -> list[DeezerArtistInfo]:
    infos: list[DeezerArtistInfo] = []
    seen: set[str] = set()

    def add(raw: object) -> None:
        if not isinstance(raw, dict):
            return
        name = str(raw.get("name") or "").strip()
        if not name:
            return
        key = name.lower()
        if key in seen:
            return
        seen.add(key)
        artist_id = raw.get("id")
        picture = _picture_url(raw)
        infos.append(
            DeezerArtistInfo(
                name=name,
                artist_id=int(artist_id) if artist_id else None,
                picture=picture,
            )
        )

    add(payload.get("artist"))
    for person in payload.get("contributors") or []:
        add(person)
    return infos


def _genres_from_album_payload(payload: dict) -> list[str]:
    names: list[str] = []
    for g in ((payload.get("genres") or {}).get("data") or []):
        name = str(g.get("name") or "").strip()
        if not name or _norm(name) in _SKIP_GENRES:
            continue
        names.append(name)
    return pretty_genres(names, max_genres=3)


def _explicit_from_payload(payload: dict, *, album: bool = False) -> bool | None:
    """Deezer explicit_content_lyrics: 0 clean, 1 explicit, 2 unknown, 3 edited, 4 partial (album)."""
    code = payload.get("explicit_content_lyrics")
    if code == 1:
        return True
    if album and code == 4:
        return True
    if code in (0, 3):
        return False
    if code == 2:
        return None
    flag = payload.get("explicit_lyrics")
    if flag is True:
        return True
    if flag is False:
        return False
    return None


def _explicit_from_track(payload: dict) -> bool | None:
    """Track-level only. Album code 4 (partially explicit) is not inherited onto tracks."""
    return _explicit_from_payload(payload, album=False)


def _explicit_rank(payload: dict | DeezerTrack, *, album: bool = False) -> int:
    """Higher is better: explicit original > unknown > clean/edited."""
    if isinstance(payload, DeezerTrack):
        flag = payload.explicit
    else:
        flag = _explicit_from_payload(payload, album=album)
    if flag is True:
        return 2
    if flag is False:
        return 0
    return 1


def _artist_ok(got: str, want: str) -> bool:
    if not want:
        return True
    if not got:
        return False
    return got == want or want in got or got in want


def _pick_album(results: list[dict], artist: str, album: str) -> dict | None:
    want_artist = _norm(artist)
    want_album = _norm(album)
    best: dict | None = None
    best_key = (-1.0, -1)
    for item in results:
        got_artist = _norm((item.get("artist") or {}).get("name") or "")
        if not _artist_ok(got_artist, want_artist):
            continue
        got_title = _norm(item.get("title") or "")
        if not got_title:
            continue
        if got_title == want_album:
            score = 1.0
        else:
            score = SequenceMatcher(None, got_title, want_album).ratio()
            if want_album in got_title or got_title in want_album:
                score = max(score, 0.82)
        key = (score, _explicit_rank(item, album=True))
        if key > best_key:
            best, best_key = item, key
    if best is not None and best_key[0] >= 0.72:
        return best
    return None


def _album_tracks(album_id: int) -> list[DeezerTrack]:
    items: list[DeezerTrack] = []
    path = f"album/{album_id}/tracks"
    params: dict[str, str] | None = {"limit": "100"}
    while path:
        payload = dz_get(path, params)
        for raw in payload.get("data") or []:
            title = str(raw.get("title") or "").strip()
            if not title:
                continue
            items.append(
                DeezerTrack(
                    title=title,
                    explicit=_explicit_from_track(raw),
                    artists=[info.name for info in _artist_infos(raw)],
                )
            )
        nxt = payload.get("next")
        if not nxt:
            break
        parsed = urlparse(str(nxt))
        path = parsed.path.lstrip("/")
        if path.startswith("2.0/"):
            path = path[4:]
        params = {k: v[0] for k, v in parse_qs(parsed.query).items()}
    return items


def _album_by_id(album_id: int) -> DeezerAlbumMatch:
    cached = _album_id_memo.get(album_id)
    if cached is not None:
        return cached
    payload = dz_get(f"album/{album_id}")
    if payload.get("error") or not payload.get("id"):
        match = DeezerAlbumMatch(source="no-match")
        _album_id_memo[album_id] = match
        return match
    genres = _genres_from_album_payload(payload)
    artist_infos = _artist_infos(payload)
    artists = [info.name for info in artist_infos]
    tracks = _album_tracks(album_id)
    source = f"album:{album_id}"
    if not genres and not tracks:
        source = "no-genre"
    match = DeezerAlbumMatch(
        genres=genres,
        source=source,
        album_id=int(payload.get("id") or album_id),
        title=str(payload.get("title") or ""),
        album_artist=artists[0] if artists else "",
        artists=artists,
        artist_infos=artist_infos,
        tracks=tracks,
        explicit=_explicit_from_payload(payload, album=True),
    )
    _album_id_memo[album_id] = match
    return match


def _search_album(artist: str, album: str) -> DeezerAlbumMatch:
    q = f'artist:"{_quote(artist)}" album:"{_quote(album)}"'
    payload = dz_get("search/album", {"q": q, "limit": 25})
    hit = _pick_album(payload.get("data") or [], artist, album)
    if not hit:
        return DeezerAlbumMatch(source="no-match")
    return _album_by_id(int(hit["id"]))


def _search_track_album(artist: str, title: str) -> DeezerAlbumMatch:
    q = f'artist:"{_quote(artist)}" track:"{_quote(strip_mark(title))}"'
    payload = dz_get("search/track", {"q": q, "limit": 15})
    want_artist = _norm(artist)
    want_title = _norm(title)
    best: dict | None = None
    best_key = (-1.0, -1)
    for item in payload.get("data") or []:
        got_artist = _norm((item.get("artist") or {}).get("name") or "")
        got_title = _norm(item.get("title") or "")
        if not _artist_ok(got_artist, want_artist):
            continue
        if not got_title:
            continue
        if got_title == want_title:
            score = 1.0
        else:
            score = SequenceMatcher(None, got_title, want_title).ratio()
            if want_title in got_title or got_title in want_title:
                score = max(score, 0.84)
        if score < 0.72:
            continue
        key = (score, _explicit_rank(item, album=False))
        if key > best_key:
            best, best_key = item, key
    album_id = (best.get("album") or {}).get("id") if best else None
    if album_id:
        return _album_by_id(int(album_id))
    return DeezerAlbumMatch(source="no-match")


def match_deezer_track(title: str, tracks: list[DeezerTrack]) -> DeezerTrack | None:
    want = _norm(title)
    if not want or not tracks:
        return None
    best: DeezerTrack | None = None
    best_key = (-1.0, -1)
    for track in tracks:
        got = _norm(track.title)
        if not got:
            continue
        if got == want:
            score = 1.0
        else:
            score = SequenceMatcher(None, got, want).ratio()
            if want in got or got in want:
                score = max(score, 0.84)
        if score < 0.72:
            continue
        key = (score, _explicit_rank(track))
        if key > best_key:
            best, best_key = track, key
    return best


def search_artist(name: str) -> DeezerArtistInfo | None:
    """Best-name Deezer artist hit, used to fill profile photos."""
    want = _norm(name)
    if not want:
        return None
    if want in _artist_search_memo:
        return _artist_search_memo[want]
    payload = dz_get("search/artist", {"q": name, "limit": 8})
    best: DeezerArtistInfo | None = None
    best_score = 0.0
    for raw in payload.get("data") or []:
        if not isinstance(raw, dict):
            continue
        got = str(raw.get("name") or "").strip()
        if not got:
            continue
        got_n = _norm(got)
        score = 1.0 if got_n == want else SequenceMatcher(None, got_n, want).ratio()
        if score < 0.86:
            continue
        if score > best_score:
            best_score = score
            artist_id = raw.get("id")
            best = DeezerArtistInfo(
                name=got,
                artist_id=int(artist_id) if artist_id else None,
                picture=_picture_url(raw),
            )
    if best and not best.picture and best.artist_id:
        detail = dz_get(f"artist/{best.artist_id}")
        if not detail.get("error"):
            best.picture = _picture_url(detail)
    _artist_search_memo[want] = best
    return best


def lookup_album(
    artist: str,
    album: str,
    *,
    sample_title: str = "",
) -> DeezerAlbumMatch:
    """Resolve Deezer album genres + track list. Memoized per (artist, album)."""
    key = (_norm(artist), _norm(album))
    if key in _album_memo:
        return _album_memo[key]

    match = DeezerAlbumMatch(source="no-match")
    for variant in _album_variants(album):
        match = _search_album(artist, variant)
        if match.album_id:
            break

    if not match.album_id and sample_title:
        track_match = _search_track_album(artist, sample_title)
        if track_match.album_id:
            match = track_match

    _album_memo[key] = match
    return match
