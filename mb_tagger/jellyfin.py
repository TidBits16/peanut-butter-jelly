"""Jellyfin library fetch and metadata PATCH helpers."""

from __future__ import annotations

import base64
import threading

import requests
from requests.adapters import HTTPAdapter

from mb_tagger.constants import UUID_RE, api_key, jf_headers, jf_url, user_id

__all__ = [
    "api_key",
    "fetch_jellyfin_albums",
    "fetch_jellyfin_artists",
    "fetch_jellyfin_tracks",
    "jf_headers",
    "jf_url",
    "resolve_user_id",
    "update_jellyfin_item",
    "upload_item_image",
    "upload_lyrics",
    "user_id",
]

_local = threading.local()


def _session() -> requests.Session:
    session = getattr(_local, "session", None)
    if session is None:
        session = requests.Session()
        session.headers.update(jf_headers)
        adapter = HTTPAdapter(pool_connections=16, pool_maxsize=16)
        session.mount("http://", adapter)
        session.mount("https://", adapter)
        _local.session = session
    return session


def resolve_user_id(user_ref: str) -> str:
    if UUID_RE.match(user_ref):
        return user_ref
    res = _session().get(f"{jf_url}/Users", timeout=30)
    res.raise_for_status()
    for u in res.json():
        if (u.get("Name") or "").lower() == user_ref.lower():
            return u["Id"]
    raise RuntimeError(f"Could not resolve user_id from '{user_ref}'")


def fetch_jellyfin_tracks(uid: str) -> list[dict]:
    res = _session().get(
        f"{jf_url}/Users/{uid}/Items",
        params={
            "IncludeItemTypes": "Audio",
            "Fields": "Path,Tags,Genres,Album,AlbumArtist,Artists,AlbumId,HasLyrics,RunTimeTicks",
            "Recursive": "true",
            "Limit": 100000,
        },
        timeout=180,
    )
    res.raise_for_status()
    return res.json().get("Items", [])


def fetch_jellyfin_albums(uid: str) -> dict[str, dict]:
    """Album metadata keyed by Id — used to plan genre/artist writes without extra GETs."""
    res = _session().get(
        f"{jf_url}/Users/{uid}/Items",
        params={
            "IncludeItemTypes": "MusicAlbum",
            "Fields": "Tags,Genres,AlbumArtist,Artists,AlbumArtists",
            "Recursive": "true",
            "Limit": 100000,
        },
        timeout=180,
    )
    res.raise_for_status()
    return {item["Id"]: item for item in res.json().get("Items", []) if item.get("Id")}


def fetch_jellyfin_artists() -> dict[str, dict]:
    """MusicArtist items keyed by lowercase name."""
    res = _session().get(
        f"{jf_url}/Artists",
        params={
            "Fields": "Overview,ImageTags,ProviderIds",
            "Recursive": "true",
            "Limit": 100000,
        },
        timeout=180,
    )
    res.raise_for_status()
    artists: dict[str, dict] = {}
    for item in res.json().get("Items", []) or []:
        name = str(item.get("Name") or "").strip()
        if name and item.get("Id"):
            artists.setdefault(name.lower(), item)
    return artists


def _names(values) -> list[str]:
    return [str(v).strip() for v in (values or []) if str(v).strip()]


def _album_artist_names(item: dict) -> list[str]:
    people = item.get("AlbumArtists") or []
    names = [str(p.get("Name") or "").strip() for p in people if isinstance(p, dict)]
    return [n for n in names if n]


def _tag_key(item: dict) -> tuple[str, ...]:
    return tuple(sorted(str(t).strip().lower() for t in (item.get("Tags") or []) if str(t).strip()))


def _snapshot(item: dict) -> tuple:
    return (
        _names(item.get("Genres")),
        _names(item.get("Artists")),
        (item.get("AlbumArtist") or "").strip(),
        tuple(_album_artist_names(item)),
        item.get("Name") or "",
        (item.get("Overview") or "").strip(),
        _tag_key(item),
    )


def update_jellyfin_item(
    uid: str,
    item_id: str,
    *,
    genres: list[str] | None = None,
    explicit: bool | None = None,
    name: str | None = None,
    add_tags: list[str] | None = None,
    remove_tags: list[str] | None = None,
    artists: list[str] | None = None,
    album_artist: str | None = None,
    overview: str | None = None,
) -> bool:
    """GET, patch fields, POST only if something actually changed. Returns True if written."""
    session = _session()
    res = session.get(f"{jf_url}/Users/{uid}/Items/{item_id}", timeout=30)
    res.raise_for_status()
    item = res.json()
    before = _snapshot(item)

    if genres is not None:
        item["Genres"] = list(genres)
    if artists is not None:
        item["Artists"] = list(artists)
        item.pop("ArtistItems", None)
        item["AlbumArtists"] = [{"Name": n} for n in artists]
    if album_artist is not None:
        item["AlbumArtist"] = album_artist
        if artists is None:
            item["AlbumArtists"] = [{"Name": album_artist}]
    tags = list(item.get("Tags") or [])
    if explicit is not None:
        tags = [t for t in tags if str(t).lower() != "explicit"]
        if explicit:
            tags.append("Explicit")
    if remove_tags:
        drop = {str(t).lower() for t in remove_tags}
        tags = [t for t in tags if str(t).lower() not in drop]
    if add_tags:
        have = {str(t).lower() for t in tags}
        for tag in add_tags:
            if str(tag).lower() not in have:
                tags.append(tag)
                have.add(str(tag).lower())
    if explicit is not None or add_tags or remove_tags:
        item["Tags"] = tags
    if name is not None:
        item["Name"] = name
    if overview is not None:
        item["Overview"] = overview

    if _snapshot(item) == before:
        return False

    item.setdefault("Tags", item.get("Tags") or [])
    item.setdefault("Genres", item.get("Genres") or [])
    item.setdefault("ProviderIds", item.get("ProviderIds") or {})
    item.pop("UserData", None)
    res = session.post(f"{jf_url}/Items/{item_id}", json=item, timeout=30)
    if not res.ok:
        body = (res.text or "").strip()
        raise requests.HTTPError(
            f"{res.status_code} {res.reason} for {res.url} {body[:300]}",
            response=res,
        )
    return True


def upload_item_image(item_id: str, data: bytes, content_type: str = "image/jpeg") -> bool:
    """POST a Primary image. Jellyfin decodes the body as base64."""
    if not data:
        return False
    mime = (content_type or "image/jpeg").split(";")[0].strip().lower()
    if mime in {"image/jpg", "jpg", "jpeg"}:
        mime = "image/jpeg"
    elif mime in {"png", "image/x-png"}:
        mime = "image/png"
    elif mime in {"webp", "image/webp"}:
        mime = "image/webp"
    elif not mime.startswith("image/"):
        mime = "image/jpeg"
    session = _session()
    res = session.post(
        f"{jf_url}/Items/{item_id}/Images/Primary",
        data=base64.b64encode(data),
        headers={"Content-Type": mime},
        timeout=60,
    )
    if not res.ok:
        body = (res.text or "").strip()
        raise requests.HTTPError(
            f"{res.status_code} {res.reason} for {res.url} {body[:300]}",
            response=res,
        )
    return True


def upload_lyrics(item_id: str, file_name: str, text: str) -> bool:
    """POST an LRC/TXT sidecar through Jellyfin so it stores and serves the lyrics."""
    if not text.strip():
        return False
    session = _session()
    res = session.post(
        f"{jf_url}/Audio/{item_id}/Lyrics",
        params={"fileName": file_name},
        data=text.encode("utf-8"),
        headers={"Content-Type": "text/plain; charset=utf-8"},
        timeout=30,
    )
    if not res.ok:
        body = (res.text or "").strip()
        raise requests.HTTPError(
            f"{res.status_code} {res.reason} for {res.url} {body[:300]}",
            response=res,
        )
    return True
