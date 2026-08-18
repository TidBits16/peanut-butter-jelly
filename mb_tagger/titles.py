"""Jellyfin title helpers for the 🅴 explicit mark."""

from __future__ import annotations

from mb_tagger.constants import EXPLICIT_MARK


def strip_mark(name: str) -> str:
    if name.endswith(EXPLICIT_MARK):
        return name[: -len(EXPLICIT_MARK)]
    return name


def has_explicit_mark(name: str) -> bool:
    n = name or ""
    return n.endswith(EXPLICIT_MARK) or n.endswith("🅴")


def desired_title(name: str, explicit: bool) -> str:
    base = strip_mark(name or "")
    if not base:
        return ""
    return f"{base}{EXPLICIT_MARK}" if explicit else base
