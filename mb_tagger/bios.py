"""Artist overviews: TheAudioDB first, Wikipedia if AudioDB has no bio."""

from __future__ import annotations

import html
import re
import time
from dataclasses import dataclass
from difflib import SequenceMatcher
from urllib.parse import quote

import requests
from requests.adapters import HTTPAdapter

from mb_tagger.constants import AUDIODB_BASE, BIO_HEADERS, WIKI_API, WIKI_SUMMARY, WIKIDATA_API
from mb_tagger.mb_cache import cache_get, cache_set
from mb_tagger.pacer import Pacer
from mb_tagger.titles import strip_mark

_http_requests = 0
_cache_hits = 0
_MIN_INTERVAL = 0.2
_MISS_TTL = 60 * 60 * 24 * 7
_pacer = Pacer(_MIN_INTERVAL)
_PAREN = re.compile(r"\s*\([^)]*\)\s*")
_HTML = re.compile(r"<[^>]+>")
_MUSIC_RE = re.compile(
    r"\b("
    r"rapper|singer|songwriter|musician|vocalist|band|dj|composer|"
    r"record producer|music producer|hip[\s-]?hop|folk[\s-]?punk|"
    r"indie pop|folk-pop|pop (?:band|group|duo|trio|artist)|"
    r"rock (?:band|group)|multi-instrumentalist|"
    r"musical (?:artist|group|duo|trio)|recording artist|"
    r"guitarist|drummer|bassist|pianist|youtuber|"
    r"ensemble|orchestra|choir|mc"
    r")\b",
    re.I,
)
_SKIP_TITLE_RE = re.compile(
    r"\b(discography|filmography|list of|politician)\b|\((album|song|ep|single|soundtrack)\)",
    re.I,
)
_session: requests.Session | None = None


def bio_stats() -> dict[str, int]:
    return {"bio_http": _http_requests, "bio_cache_hits": _cache_hits}


def reset_bio_stats() -> None:
    global _http_requests, _cache_hits
    _http_requests = 0
    _cache_hits = 0


def _client() -> requests.Session:
    global _session
    if _session is None:
        _session = requests.Session()
        _session.headers.update(BIO_HEADERS)
        adapter = HTTPAdapter(pool_connections=8, pool_maxsize=8)
        _session.mount("http://", adapter)
        _session.mount("https://", adapter)
    return _session


@dataclass
class BioMatch:
    overview: str = ""
    source: str = "no-match"


def _norm(text: str) -> str:
    s = strip_mark(text or "").lower()
    s = re.sub(r"[^\w\s&]+", " ", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def _compact(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", _norm(text))


def _title_core(title: str) -> str:
    return _PAREN.sub(" ", title or "").strip()


def _clean_bio(text: str | None, *, limit: int = 4000) -> str:
    value = _HTML.sub(" ", html.unescape(text or ""))
    value = value.replace("\r\n", "\n").replace("\r", "\n")
    value = re.sub(r"[ \t]+\n", "\n", value)
    value = re.sub(r"\n{3,}", "\n\n", value)
    value = re.sub(r"[ \t]{2,}", " ", value).strip()
    if len(value) > limit:
        cut = value[:limit].rsplit("\n", 1)[0].strip()
        value = cut or value[:limit].rstrip()
    return value


def _name_score(want: str, got: str) -> float:
    a, b = _norm(want), _norm(_title_core(got))
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    score = SequenceMatcher(None, a, b).ratio()
    if _compact(a) == _compact(b):
        return max(score, 0.98)
    a_parts, b_parts = a.split(), b.split()
    if a_parts and b_parts and a_parts[-1] == b_parts[-1]:
        first_a, first_b = a_parts[0], b_parts[0]
        if first_a == first_b or first_a.startswith(first_b) or first_b.startswith(first_a):
            score = max(score, 0.9)
    ca, cb = _compact(a), _compact(b)
    shorter, longer = (ca, cb) if len(ca) <= len(cb) else (cb, ca)
    if len(shorter) >= 4 and (longer.startswith(shorter) or longer.endswith(shorter)):
        score = max(score, 0.86)
    return score


def _is_music(text: str) -> bool:
    return bool(_MUSIC_RE.search(text or ""))


def _get(url: str, params: dict[str, str] | None, cache_path: str) -> dict | None:
    global _http_requests, _cache_hits
    params = dict(params or {})
    cached = cache_get(cache_path, params, ttl=_MISS_TTL)
    if cached is not None:
        _cache_hits += 1
        if cached.get("_miss"):
            return None
        return cached

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


def _pick_bio(name: str, payload: dict | None) -> BioMatch | None:
    artists = (payload or {}).get("artists") or []
    best: BioMatch | None = None
    best_score = 0.0
    for raw in artists:
        if not isinstance(raw, dict):
            continue
        got = str(raw.get("strArtist") or "").strip()
        score = _name_score(name, got)
        if score < 0.72 and _compact(name) not in _compact(got):
            continue
        bio = _clean_bio(
            raw.get("strBiography")
            or raw.get("strBiographyEN")
            or raw.get("strBiographyDE")
            or raw.get("strBiographyFR")
        )
        if not bio:
            continue
        if score > best_score:
            best_score = score
            best = BioMatch(overview=bio, source=f"audiodb:{got or name}")
    return best


def _wiki_search(query: str) -> list[str]:
    payload = _get(WIKI_API, {
        "action": "query",
        "list": "search",
        "srsearch": query,
        "srlimit": "8",
        "srprop": "",
        "format": "json",
    }, "wikipedia/search")
    titles: list[str] = []
    for hit in ((payload or {}).get("query") or {}).get("search") or []:
        title = str(hit.get("title") or "").strip()
        if title:
            titles.append(title)
    return titles


def _wiki_summary(title: str) -> dict | None:
    url = f"{WIKI_SUMMARY}/{quote(title.replace(' ', '_'), safe='')}"
    return _get(url, None, f"wikipedia/summary/{title}")


def _from_wikipedia_titles(name: str, titles: list[str]) -> BioMatch | None:
    best: BioMatch | None = None
    best_key = (-1.0, -1)
    for title in titles:
        if _SKIP_TITLE_RE.search(title):
            continue
        name_score = _name_score(name, title)
        if name_score < 0.72:
            continue
        payload = _wiki_summary(title)
        if not payload or payload.get("type") == "disambiguation":
            continue
        extract = _clean_bio(str(payload.get("extract") or ""))
        description = str(payload.get("description") or "")
        if not extract:
            continue
        if not _is_music(f"{description} {extract[:500]}"):
            continue
        key = (name_score, 1 if _is_music(description) else 0)
        if key > best_key:
            best_key = key
            best = BioMatch(overview=extract, source=f"wikipedia:{title}")
            if name_score >= 0.98 and _is_music(description):
                break
    return best


def _from_wikipedia(name: str) -> BioMatch | None:
    seen: set[str] = set()
    queries = [
        name,
        f'{name} (musician OR singer OR band OR rapper)',
    ]
    for query in queries:
        titles = [t for t in _wiki_search(query) if t not in seen]
        seen.update(titles)
        match = _from_wikipedia_titles(name, titles)
        if match:
            return match
    return None


def _from_wikidata(name: str) -> BioMatch | None:
    """Find the music entity, then its English Wikipedia page (or a short description)."""
    payload = _get(WIKIDATA_API, {
        "action": "wbsearchentities",
        "search": name,
        "language": "en",
        "type": "item",
        "limit": "5",
        "format": "json",
    }, "wikidata/search")
    hits = (payload or {}).get("search") or []
    for hit in hits:
        if not isinstance(hit, dict):
            continue
        label = str(hit.get("label") or "")
        description = str(hit.get("description") or "")
        if _name_score(name, label) < 0.72 and _compact(name) != _compact(label):
            continue
        if description and not _is_music(description):
            continue
        qid = str(hit.get("id") or "")
        if not qid:
            continue
        entity = _get(WIKIDATA_API, {
            "action": "wbgetentities",
            "ids": qid,
            "props": "sitelinks|descriptions",
            "languages": "en",
            "sitefilter": "enwiki",
            "format": "json",
        }, f"wikidata/entity/{qid}")
        ent = ((entity or {}).get("entities") or {}).get(qid) or {}
        title = str(((ent.get("sitelinks") or {}).get("enwiki") or {}).get("title") or "").strip()
        if title:
            match = _from_wikipedia_titles(name, [title])
            if match:
                return match
        desc = str(((ent.get("descriptions") or {}).get("en") or {}).get("value") or description).strip()
        if desc and _is_music(desc):
            return BioMatch(overview=desc[0].upper() + desc[1:] + ".", source=f"wikidata:{qid}")
    return None


def lookup_bio(name: str) -> BioMatch:
    global _cache_hits
    want = (name or "").strip()
    if not want:
        return BioMatch()
    cached = cache_get("bio/v3", {"name": want.lower()}, ttl=_MISS_TTL)
    if cached is not None:
        _cache_hits += 1
        if cached.get("_miss"):
            return BioMatch()
        return BioMatch(
            overview=str(cached.get("overview") or ""),
            source=str(cached.get("source") or "cache"),
        )

    queries = [want]
    stripped = _norm(want)
    if stripped and stripped.lower() != want.lower():
        queries.append(stripped)
    match: BioMatch | None = None
    for query in queries:
        payload = _get(f"{AUDIODB_BASE}/search.php", {"s": query}, "audiodb/search")
        match = _pick_bio(want, payload)
        if match:
            break
    if not match:
        match = _from_wikipedia(want) or _from_wikidata(want)

    if match and match.overview:
        cache_set("bio/v3", {"name": want.lower()}, {
            "overview": match.overview,
            "source": match.source,
        })
        return match
    cache_set("bio/v3", {"name": want.lower()}, {"_miss": True})
    return BioMatch()
