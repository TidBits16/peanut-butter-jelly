"""Jellyfin + Deezer endpoints and the title 🅴 mark."""

from __future__ import annotations

import os
import re
from pathlib import Path

_ROOT = Path(__file__).resolve().parent.parent


def _load_env(path: Path) -> None:
    if not path.is_file():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip().strip("'").strip('"')
        os.environ.setdefault(key, value)


_load_env(_ROOT / ".env")

jf_url = os.environ.get("JELLYFIN_URL", "").rstrip("/")
api_key = os.environ.get("JELLYFIN_API_KEY", "")
user_id = os.environ.get("JELLYFIN_USER", "admin")
if not jf_url or not api_key:
    raise RuntimeError(
        "Set JELLYFIN_URL and JELLYFIN_API_KEY in a .env file (see .env.example)."
    )

jf_headers = {"X-Emby-Token": api_key, "Content-Type": "application/json"}
UUID_RE = re.compile(r"^[0-9a-fA-F-]{32,36}$")

DEEZER_BASE = "https://api.deezer.com"
DEEZER_HEADERS = {
    "User-Agent": "peanut-butter-jelly/3.0 (deezer; jellyfin)",
    "Accept": "application/json",
}
LRCLIB_BASE = "https://lrclib.net"
LRCLIB_HEADERS = {
    "User-Agent": f"peanut-butter-jelly/3.0 ({jf_url})",
    "Accept": "application/json",
}
AUDIODB_BASE = "https://www.theaudiodb.com/api/v1/json/2"
WIKI_API = "https://en.wikipedia.org/w/api.php"
WIKI_SUMMARY = "https://en.wikipedia.org/api/rest_v1/page/summary"
WIKIDATA_API = "https://www.wikidata.org/w/api.php"
BIO_HEADERS = {
    "User-Agent": f"peanut-butter-jelly/3.0 ({jf_url})",
    "Accept": "application/json",
}
EXPLICIT_MARK = " 🅴"
NOMATCH_TAG = "DeezerNoMatch"
