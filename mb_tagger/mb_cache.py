"""On-disk HTTP cache (survives reruns; shared across lookups)."""

from __future__ import annotations

import hashlib
import json
import threading
import time
from pathlib import Path
from typing import Any
from urllib.parse import urlencode

CACHE_DIR = Path(__file__).resolve().parent.parent / ".mb_cache"
DEFAULT_TTL_SECONDS = 60 * 60 * 24 * 30  # 30 days
_lock = threading.Lock()


def _key(path: str, params: dict[str, Any]) -> str:
    items = sorted((str(k), str(v)) for k, v in params.items())
    raw = path.lstrip("/") + "?" + urlencode(items)
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()


def cache_get(path: str, params: dict[str, Any], *, ttl: int = DEFAULT_TTL_SECONDS) -> dict | None:
    fp = CACHE_DIR / f"{_key(path, params)}.json"
    with _lock:
        if not fp.is_file():
            return None
        try:
            if time.time() - fp.stat().st_mtime > ttl:
                return None
            data = json.loads(fp.read_text(encoding="utf-8"))
            return data if isinstance(data, dict) else None
        except (OSError, json.JSONDecodeError, TypeError):
            return None


def cache_set(path: str, params: dict[str, Any], payload: dict) -> None:
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    fp = CACHE_DIR / f"{_key(path, params)}.json"
    tmp = fp.with_suffix(".tmp")
    payload_text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    with _lock:
        try:
            tmp.write_text(payload_text, encoding="utf-8")
            tmp.replace(fp)
        except OSError:
            pass
