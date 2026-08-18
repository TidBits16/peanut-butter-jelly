"""Shared glam console output for the tagging suite."""

from __future__ import annotations

from pathlib import Path

from rich import box
from rich.align import Align
from rich.columns import Columns
from rich.console import Console, Group
from rich.panel import Panel
from rich.progress import (
    BarColumn,
    MofNCompleteColumn,
    Progress,
    SpinnerColumn,
    TaskProgressColumn,
    TextColumn,
    TimeElapsedColumn,
    TimeRemainingColumn,
)
from rich.rule import Rule
from rich.table import Table
from rich.text import Text
from rich.theme import Theme

THEME = Theme(
    {
        "brand": "bold #f472b6",
        "peanut": "bold #f59e0b",
        "jelly": "bold #e879f9",
        "accent": "bold #22d3ee",
        "ok": "bold #4ade80",
        "warn": "bold #fbbf24",
        "bad": "bold #fb7185",
        "mute": "#64748b",
        "path": "#e2e8f0",
        "dimpath": "#94a3b8",
        "mp3": "bold black on #f97316",
        "corrupt": "bold white on #dc2626",
        "fake": "bold black on #eab308",
        "explicit": "bold white on #db2777",
        "dry": "bold #38bdf8",
        "apply": "bold black on #a3e635",
        "force": "bold black on #f472b6",
        "genre": "bold #fef3c7 on #78350f",
        "chip": "bold #e0f2fe on #0e7490",
        "chip_off": "bold #64748b on #1e293b",
        "chip_miss": "bold white on #be123c",
        "chip_photo": "bold black on #f59e0b",
        "chip_bio": "bold black on #e879f9",
        "chip_lyric": "bold black on #22d3ee",
        "meta": "bold #c4b5fd on #4c1d95",
        "renamed": "bold black on #2dd4bf",
        "cache": "bold #a7f3d0 on #064e3b",
    }
)

console = Console(theme=THEME, highlight=False)


def rel_path(path: Path, root: Path | None = None) -> str:
    try:
        if root is not None:
            return str(path.relative_to(root))
    except ValueError:
        pass
    return str(path)


def badge(label: str, style: str) -> Text:
    return Text(f" {label} ", style=style)


def chip(label: str, *, on: bool = True) -> Text:
    return badge(label, "chip" if on else "chip_off")


def genre_chips(genres: list[str]) -> Text:
    if not genres:
        return Text("—", style="mute")
    parts: list[Text] = []
    for i, name in enumerate(genres):
        if i:
            parts.append(Text(" "))
        parts.append(badge(name, "genre"))
    return Text.assemble(*parts)


def meta_chips(**pairs: str | int | None) -> Text:
    """Tiny metadata pills: year=2021 track=03 → badges."""
    parts: list[Text] = []
    for key, value in pairs.items():
        if value is None or value == "":
            continue
        if parts:
            parts.append(Text(" "))
        parts.append(badge(f"{key} {value}", "meta"))
    return Text.assemble(*parts) if parts else Text("")


def file_flags(path: Path, *, container: str | None = None, corrupt: bool = False) -> list[Text]:
    flags: list[Text] = []
    ext = path.suffix.lower()
    container_l = (container or "").lower()

    if corrupt:
        flags.append(badge("CORRUPT", "corrupt"))

    is_mp3_ext = ext == ".mp3"
    is_mp3_codec = any(x in container_l for x in ("mp3", "mpeg", "easymp3"))
    is_mp4_codec = any(x in container_l for x in ("mp4", "m4a", "aac", "alac"))

    if is_mp3_ext and is_mp4_codec:
        flags.append(badge("FAKE .MP3", "fake"))
        flags.append(badge("REALLY MP4/AAC", "warn"))
    elif is_mp3_ext or is_mp3_codec:
        flags.append(badge("MP3 🤢", "mp3"))

    return flags


def print_banner(
    title: str,
    subtitle: str,
    *,
    apply_mode: bool,
    root: Path | None = None,
    features: list[tuple[str, bool]] | None = None,
    extras: dict[str, str] | None = None,
    suite: str = "peanut butter & jelly · deez nuts × jellyfin",
):
    mode = badge("APPLY", "apply") if apply_mode else badge("DRY RUN", "dry")
    rows: list[Text] = [
        Text.assemble(
            Text("peanut  ", style="peanut"),
            Text("×", style="mute"),
            Text("  jelly", style="jelly"),
            Text("   ", style="mute"),
            mode,
        ),
        Text(subtitle, style="mute"),
    ]
    if extras:
        extra_bits: list[Text] = []
        for key, value in extras.items():
            if extra_bits:
                extra_bits.append(Text("  ", style="mute"))
            extra_bits.append(badge(f"{key} {value}", "meta"))
        rows.append(Text.assemble(*extra_bits))
    if root is not None:
        rows.append(Text(str(root.resolve()), style="dimpath"))
    if features:
        rows.append(Text(""))
        row_bits: list[Text] = []
        for i, (label, on) in enumerate(features):
            if i and i % 6 == 0:
                rows.append(Text.assemble(*row_bits))
                row_bits = []
            elif row_bits:
                row_bits.append(Text(" "))
            row_bits.append(chip(label, on=on))
        if row_bits:
            rows.append(Text.assemble(*row_bits))

    console.print()
    console.print(
        Panel(
            Align.left(Group(*rows)),
            title=f"[brand]◆ {title}[/]",
            subtitle=f"[mute]{suite}[/]",
            border_style="#f472b6",
            box=box.DOUBLE_EDGE,
            padding=(1, 2),
        )
    )
    console.print()


def print_section(title: str, hint: str = ""):
    label = f"[accent]{title}[/]"
    if hint:
        label = f"[accent]{title}[/]  [mute]{hint}[/]"
    console.print()
    console.print(Rule(label, style="#334155", characters="═"))
    console.print()


def print_callout(message: str, *, kind: str = "mute"):
    styles = {
        "mute": ("#334155", "mute"),
        "warn": ("#fbbf24", "warn"),
        "ok": ("#4ade80", "ok"),
        "bad": ("#fb7185", "bad"),
        "info": ("#22d3ee", "accent"),
    }
    border, text_style = styles.get(kind, styles["mute"])
    console.print(
        Panel(
            Text(message, style=text_style),
            border_style=border,
            box=box.SIMPLE,
            padding=(0, 1),
        )
    )


def event_line(
    status: str,
    path: Path,
    detail: str = "",
    *,
    root: Path | None = None,
    container: str | None = None,
    corrupt: bool = False,
    explicit: bool | None = None,
    genres: list[str] | None = None,
    year: str | None = None,
    track: int | None = None,
    renamed_to: str | None = None,
):
    styles = {
        "updated": ("UPDATED", "ok"),
        "dry": ("DRY RUN", "dry"),
        "skip": ("SKIP", "mute"),
        "unchanged": ("OK", "mute"),
        "nomatch": ("NO MATCH", "warn"),
        "miss": ("MISS", "chip_miss"),
        "fail": ("FAIL", "bad"),
        "corrupt": ("CORRUPT", "corrupt"),
        "unmatched": ("UNMATCHED", "warn"),
        "debug": ("DEBUG", "accent"),
        "renamed": ("RENAMED", "renamed"),
        "stamped": ("STAMPED", "chip"),
    }
    label, style = styles.get(status, (status.upper(), "accent"))
    parts: list[Text] = [badge(label, style)]

    if explicit is True:
        parts.append(badge("🅴", "explicit"))
    elif explicit is False and status in {"updated", "dry"}:
        parts.append(badge("CLEAN", "chip_off"))

    parts.extend(file_flags(path, container=container, corrupt=False if status == "corrupt" else corrupt))
    parts.append(Text(" "))
    parts.append(Text(rel_path(path, root), style="path"))

    meta = meta_chips(year=year, track=f"{track:02d}" if isinstance(track, int) else track)
    if meta.plain:
        parts.append(Text("  "))
        parts.append(meta)

    if genres is not None:
        parts.append(Text("  "))
        parts.append(genre_chips(genres))

    if renamed_to:
        parts.append(Text("  "))
        parts.append(Text("→ ", style="mute"))
        parts.append(Text(renamed_to, style="accent"))

    if detail:
        parts.append(Text("  "))
        parts.append(Text(detail, style="mute"))

    console.print(Text.assemble(*parts))


def progress(description: str = "working") -> Progress:
    return Progress(
        SpinnerColumn(style="#f472b6"),
        TextColumn(f"[brand]✦[/] [accent]{description}[/]"),
        BarColumn(
            bar_width=32,
            complete_style="#f472b6",
            finished_style="#4ade80",
            pulse_style="#22d3ee",
        ),
        TaskProgressColumn(),
        MofNCompleteColumn(),
        TimeElapsedColumn(),
        TimeRemainingColumn(),
        console=console,
        transient=True,
        expand=False,
    )


def print_summary(
    title: str,
    stats: dict[str, int],
    *,
    warnings: list[str] | None = None,
    footer: str = "",
):
    # Group related metrics into visual columns.
    groups = {
        "run": ["scanned", "pending", "updated", "skipped"],
        "deezer": ["deezer_http", "deezer_cache_hits", "albums", "albums_matched", "unchanged", "nomatch", "nomatch_tracks"],
        "lyrics": [
            "already_has",
            "lyrics_synced",
            "lyrics_plain",
            "lyrics_instrumental",
            "lyrics_nomatch",
            "lyrics_skipped",
            "lyrics_uploaded",
            "lrclib_http",
            "lrclib_cache_hits",
        ],
        "explicit": ["explicit_yes", "explicit_no", "explicit_unknown", "album_explicit", "tag_mark_sync", "nomatch_tagged", "nomatch_cleared"],
        "artists": [
            "artist_photos",
            "artist_photos_skip",
            "artist_photos_nomatch",
            "artist_photos_uploaded",
            "artist_bios",
            "artist_bios_skip",
            "artist_bios_nomatch",
            "artist_bios_uploaded",
            "bio_http",
            "bio_cache_hits",
        ],
        "files": ["files_updated", "files_missing", "files_unwritable"],
        "health": ["failed"],
    }

    accent_keys = {
        "updated",
        "pending",
        "isrc_writes",
        "renamed",
        "tags_rewritten",
        "jellyfin_updates",
        "mb_cache_hits",
        "deezer_cache_hits",
        "explicit_yes",
        "album_explicit",
        "files_updated",
        "nomatch_tagged",
        "lyrics_synced",
        "lyrics_uploaded",
        "lrclib_cache_hits",
        "artist_photos",
        "artist_photos_uploaded",
        "artist_bios",
        "artist_bios_uploaded",
        "bio_cache_hits",
    }
    bad_keys = {"failed", "corrupt", "mp3", "fake_mp3", "unwritable", "files_unwritable"}
    warn_keys = {
        "unmatched",
        "no_genre",
        "nomatch",
        "nomatch_tracks",
        "explicit_unknown",
        "skipped",
        "files_missing",
        "lyrics_nomatch",
        "already_has",
        "artist_photos_skip",
        "artist_photos_nomatch",
        "artist_bios_skip",
        "artist_bios_nomatch",
    }

    def style_for(key: str, value: int) -> str:
        if key in bad_keys and value:
            return "bad"
        if key in warn_keys and value:
            return "warn"
        if key in accent_keys and value:
            return "accent"
        if key == "mb_http" and value:
            return "brand"
        if value == 0:
            return "mute"
        return "ok"

    tables: list[Table] = []
    used: set[str] = set()
    for group_name, keys in groups.items():
        rows = [(k, stats[k]) for k in keys if k in stats]
        if not rows:
            continue
        used.update(k for k, _ in rows)
        table = Table(
            title=f"[mute]{group_name}[/]",
            show_header=False,
            box=box.SIMPLE_HEAVY,
            border_style="#1e293b",
            pad_edge=False,
            expand=True,
            title_style="mute",
        )
        table.add_column("key", style="mute", no_wrap=True)
        table.add_column("val", justify="right", no_wrap=True)
        for key, value in rows:
            if value == 0 and key not in {"failed", "pending", "updated"}:
                continue
            table.add_row(key.replace("_", " "), Text(str(value), style=style_for(key, value)))
        if table.row_count:
            tables.append(table)

    leftover = [(k, v) for k, v in stats.items() if k not in used and not (v == 0 and k not in {"failed", "pending", "updated"})]
    if leftover:
        table = Table(
            title="[mute]other[/]",
            show_header=False,
            box=box.SIMPLE_HEAVY,
            border_style="#1e293b",
            pad_edge=False,
            expand=True,
        )
        table.add_column("key", style="mute")
        table.add_column("val", justify="right")
        for key, value in leftover:
            table.add_row(key.replace("_", " "), Text(str(value), style=style_for(key, value)))
        tables.append(table)

    body_parts: list = []
    if tables:
        body_parts.append(Columns(tables, equal=True, expand=True))
    if warnings:
        body_parts.append(Text(""))
        body_parts.append(Text("\n".join(f"⚠  {w}" for w in warnings), style="warn"))
    if footer:
        body_parts.append(Text(""))
        body_parts.append(Text(footer, style="mute"))

    console.print()
    console.print(
        Panel(
            Group(*body_parts),
            title=f"[brand]◈ {title}[/]",
            subtitle="[mute]session totals[/]",
            border_style="#22d3ee",
            box=box.ROUNDED,
            padding=(1, 2),
        )
    )
    console.print()


def print_library_strip(*, user: str, tracks: int, albums: int, artists: int, resume: int | None = None):
    bits: list[Text] = [
        badge("LIBRARY", "chip"),
        Text(f"  {user}  ", style="mute"),
        badge(f"{tracks} tracks", "meta"),
        Text(" "),
        badge(f"{albums} albums", "meta"),
        Text(" "),
        badge(f"{artists} artists", "meta"),
    ]
    if resume:
        bits.append(Text(" "))
        bits.append(badge(f"from {resume}", "warn"))
    console.print(Text.assemble(*bits))


def _gap_kind_chips(kinds: list[str]) -> Text:
    styles = {
        "photo": "chip_photo",
        "bio": "chip_bio",
        "lyrics": "chip_lyric",
        "deezer": "chip_miss",
        "fail": "bad",
    }
    parts: list[Text] = []
    for kind in kinds:
        if parts:
            parts.append(Text(" "))
        parts.append(badge(kind.upper(), styles.get(kind, "chip_miss")))
    return Text.assemble(*parts) if parts else Text("—", style="mute")


def print_gap_report(
    *,
    albums: list[tuple[str, str, int]],
    artists: list[tuple[str, list[str]]],
    lyrics: list[tuple[str, list[str]]],
    fails: list[tuple[str, str]],
):
    """Manual-attention list: sources came up empty."""
    album_n = len(albums)
    artist_n = len(artists)
    lyric_n = sum(len(tracks) for _, tracks in lyrics)
    fail_n = len(fails)
    total = album_n + artist_n + lyric_n + fail_n

    print_section(
        "needs you",
        hint="sources came up empty · go in by hand" if total else "nothing left to chase",
    )

    if not total:
        console.print(
            Panel(
                Text.assemble(
                    badge("CLEAN", "apply"),
                    Text("  every album, artist, and lyric request found a source", style="ok"),
                ),
                border_style="#4ade80",
                box=box.ROUNDED,
                padding=(0, 1),
            )
        )
        return

    header = Text.assemble(
        badge("NEEDS YOU", "chip_miss"),
        Text("  ", style="mute"),
        badge(f"{album_n} albums", "chip_miss" if album_n else "chip_off"),
        Text(" "),
        badge(f"{artist_n} artists", "chip_miss" if artist_n else "chip_off"),
        Text(" "),
        badge(f"{lyric_n} lyrics", "chip_lyric" if lyric_n else "chip_off"),
        Text(" "),
        badge(f"{fail_n} fails", "bad" if fail_n else "chip_off"),
    )
    console.print(header)
    console.print()

    if albums:
        table = Table(
            title="[chip_miss] albums[/]  [mute]no Deezer match[/]",
            box=box.SIMPLE_HEAVY,
            border_style="#9f1239",
            pad_edge=False,
            expand=True,
            show_header=True,
            header_style="mute",
        )
        table.add_column("album", style="path", overflow="fold")
        table.add_column("why", style="mute")
        table.add_column("tracks", justify="right", style="warn", width=7)
        for label, reason, tracks in albums:
            table.add_row(label, reason, str(tracks))
        console.print(table)
        console.print()

    if artists:
        table = Table(
            title="[chip_photo] artists[/]  [mute]photo / bio missing[/]",
            box=box.SIMPLE_HEAVY,
            border_style="#b45309",
            pad_edge=False,
            expand=True,
            show_header=True,
            header_style="mute",
        )
        table.add_column("artist", style="path", overflow="fold")
        table.add_column("missing")
        for name, kinds in artists:
            table.add_row(name, _gap_kind_chips(kinds))
        console.print(table)
        console.print()

    if lyrics:
        table = Table(
            title="[chip_lyric] lyrics[/]  [mute]LRCLIB had nothing[/]",
            box=box.SIMPLE_HEAVY,
            border_style="#0e7490",
            pad_edge=False,
            expand=True,
            show_header=True,
            header_style="mute",
        )
        table.add_column("album", style="path", overflow="fold")
        table.add_column("n", justify="right", width=4, style="accent")
        table.add_column("tracks", style="mute", overflow="fold")
        for album, tracks in lyrics:
            shown = tracks[:6]
            extra = len(tracks) - len(shown)
            detail = ", ".join(shown)
            if extra > 0:
                detail = f"{detail}  +{extra} more"
            table.add_row(album, str(len(tracks)), detail)
        console.print(table)
        console.print()

    if fails:
        table = Table(
            title="[bad] fails[/]  [mute]lookup / write errors[/]",
            box=box.SIMPLE_HEAVY,
            border_style="#fb7185",
            pad_edge=False,
            expand=True,
            show_header=True,
            header_style="mute",
        )
        table.add_column("item", style="path", overflow="fold")
        table.add_column("error", style="bad", overflow="fold")
        for item, error in fails:
            table.add_row(item, error)
        console.print(table)
        console.print()
