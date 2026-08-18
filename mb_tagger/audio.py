"""Read/write title, genre, and iTunes advisory tags on audio files (mutagen)."""

from __future__ import annotations

import os
from collections.abc import Sequence
from collections import defaultdict
from pathlib import Path

import mutagen
from mutagen.easyid3 import EasyID3
from mutagen.mp4 import MP4, MP4FreeForm

from mb_tagger.genres import pretty_genres

AUDIO_EXTS = {".mp3", ".flac", ".m4a", ".mp4", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".wv"}
ADVISORY_EASY_KEY = "itunesadvisory"
ADVISORY_MP4_KEY = "----:com.apple.iTunes:ITUNESADVISORY"
ADVISORY_EXPLICIT = "1"

__all__ = [
    "ADVISORY_EXPLICIT",
    "AUDIO_EXTS",
    "build_name_index",
    "default_music_dir",
    "read_file_tags",
    "resolve_local_path",
    "write_file_tags",
]

EasyID3.RegisterTXXXKey(ADVISORY_EASY_KEY, "ITUNESADVISORY")


def default_music_dir() -> Path:
    for candidate in (Path("/media/music"), Path("music")):
        if candidate.is_dir():
            return candidate
    return Path("music")


def build_name_index(root: Path) -> dict[str, list[Path]]:
    index: dict[str, list[Path]] = defaultdict(list)
    if not root.is_dir():
        return index
    for path in root.rglob("*"):
        if path.is_file() and path.suffix.lower() in AUDIO_EXTS:
            index[path.name.lower()].append(path)
    return index


def resolve_local_path(
    jf_path: str,
    root: Path,
    name_index: dict[str, list[Path]] | None = None,
) -> Path | None:
    """Map a Jellyfin Path onto this machine (as-is, under --dir, or unique basename)."""
    raw = (jf_path or "").strip()
    if not raw:
        return None
    path = Path(raw)
    if path.is_file():
        return path
    parts = path.parts[1:] if path.parts and path.parts[0] == "/" else path.parts
    if root.is_dir():
        for i in range(len(parts)):
            candidate = root.joinpath(*parts[i:])
            if candidate.is_file():
                return candidate
    hits = list((name_index or {}).get(path.name.lower()) or [])
    if len(hits) == 1:
        return hits[0]
    if len(hits) > 1:
        want = [p.lower() for p in parts]
        best: Path | None = None
        best_score = -1
        for hit in hits:
            got = [p.lower() for p in hit.parts]
            score = 0
            for a, b in zip(reversed(want), reversed(got)):
                if a != b:
                    break
                score += 1
            if score > best_score:
                best, best_score = hit, score
        return best
    return None


def _first(values) -> str:
    if not values:
        return ""
    return str(values[0]).strip()


def _list(values) -> list[str]:
    return [str(v).strip() for v in (values or []) if str(v).strip()]


def _first_text(value) -> str:
    if value is None:
        return ""
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes, bytearray)):
        if not value:
            return ""
        value = value[0]
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", errors="ignore").strip()
    return str(value).strip()


def _easy_advisory(audio) -> str:
    for key in (ADVISORY_EASY_KEY, "ITUNESADVISORY", "itunesadvisory"):
        try:
            value = _first_text(audio.get(key))
        except ValueError:
            continue
        if value:
            return value
    return ""


def _set_easy_advisory(audio, value: str) -> None:
    try:
        audio[ADVISORY_EASY_KEY] = [value]
        return
    except ValueError:
        pass
    audio["ITUNESADVISORY"] = [value]


def read_file_tags(path: Path) -> tuple[str, list[str], str] | None:
    """Return (title, genres, ITUNESADVISORY) or None if the file can't be read."""
    try:
        raw = mutagen.File(path)
    except Exception:
        return None
    if raw is None:
        return None
    try:
        if isinstance(raw, MP4):
            title = _first_text(raw.get("\xa9nam"))
            genres = pretty_genres(_list(raw.get("\xa9gen")))
            advisory = _first_text(raw.get(ADVISORY_MP4_KEY))
            return title, genres, advisory
        audio = mutagen.File(path, easy=True)
        if audio is None:
            return None
        title = _first(audio.get("title"))
        genres = pretty_genres(_list(audio.get("genre")))
        return title, genres, _easy_advisory(audio)
    except Exception:
        return None


def write_file_tags(
    path: Path,
    *,
    title: str | None = None,
    genres: list[str] | None = None,
    advisory: str | None = None,
) -> bool:
    """Write title, genre, and/or iTunes advisory. Returns True if the file was saved."""
    if not path.is_file():
        raise FileNotFoundError(str(path))
    if not os.access(path, os.W_OK):
        raise PermissionError(f"not writable: {path}")
    if title is None and genres is None and advisory is None:
        return False
    raw = mutagen.File(path)
    if raw is None:
        raise RuntimeError(f"unsupported audio format: {path}")
    if isinstance(raw, MP4):
        changed = False
        if title is not None and _first_text(raw.get("\xa9nam")) != title:
            raw["\xa9nam"] = [title]
            changed = True
        if genres is not None and pretty_genres(_list(raw.get("\xa9gen"))) != list(genres):
            raw["\xa9gen"] = list(genres)
            changed = True
        if advisory is not None and _first_text(raw.get(ADVISORY_MP4_KEY)) != advisory:
            raw[ADVISORY_MP4_KEY] = [MP4FreeForm(advisory.encode("utf-8"))]
            changed = True
        if not changed:
            return False
        raw.save()
        return True
    audio = mutagen.File(path, easy=True)
    if audio is None:
        raise RuntimeError(f"unsupported audio format: {path}")
    if audio.tags is None:
        audio.add_tags()
    changed = False
    if title is not None and _first(audio.get("title")) != title:
        audio["title"] = [title]
        changed = True
    if genres is not None and pretty_genres(_list(audio.get("genre"))) != list(genres):
        audio["genre"] = list(genres)
        changed = True
    if advisory is not None and _easy_advisory(audio) != advisory:
        _set_easy_advisory(audio, advisory)
        changed = True
    if not changed:
        return False
    audio.save()
    return True
