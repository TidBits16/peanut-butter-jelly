#!/usr/bin/env python3
"""Peanut butter & jelly: Deezer (deez nuts) × Jellyfin (jelly).

Album genres/artists from Deezer, album + per-track Explicit tag + title 🅴 from Deezer.
Artist profile photos from Deezer. Artist descriptions from TheAudioDB, Wikipedia if AudioDB has none.
Jellyfin Explicit tags also get iTunes ITUNESADVISORY + title 🅴 on files.
Synced lyrics from LRCLIB (plain text if no timestamps). Genre + title 🅴 + iTunes
also written to files.

Default writes fill missing fields only. Pass --force to replace existing values
from sources (photos, bios, genres, artists, lyrics).

  .venv/bin/python peanut-butter-jelly.py
  .venv/bin/python peanut-butter-jelly.py --apply
  .venv/bin/python peanut-butter-jelly.py --apply --force
  .venv/bin/python peanut-butter-jelly.py --apply --dir /media/music
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from pathlib import Path

from rich.text import Text

import console_ui as ui
from mb_tagger.audio import (
    ADVISORY_EXPLICIT,
    build_name_index,
    default_music_dir,
    read_file_tags,
    resolve_local_path,
    write_file_tags,
)
from mb_tagger.bios import BioMatch, bio_stats, lookup_bio, reset_bio_stats
from mb_tagger.constants import NOMATCH_TAG
from mb_tagger.deezer import download_image, dz_stats, lookup_album, match_deezer_track, reset_dz_stats, search_artist
from mb_tagger.genres import pretty_genres
from mb_tagger.jellyfin import (
    fetch_jellyfin_albums,
    fetch_jellyfin_artists,
    fetch_jellyfin_tracks,
    resolve_user_id,
    update_jellyfin_item,
    upload_item_image,
    upload_lyrics,
    user_id,
)
from mb_tagger.lrclib import (
    lrc_stats,
    lookup_lyrics_for_artists,
    lyric_query_artists,
    lyrics_filename,
    reset_lrc_stats,
    track_duration_seconds,
)
from mb_tagger.titles import desired_title, has_explicit_mark, strip_mark

parser = argparse.ArgumentParser(
    description="Peanut butter & jelly — Deezer genres/artists/explicit + LRCLIB lyrics onto Jellyfin",
    epilog="Dry-run by default. Add --apply to write missing fields. Add --force to overwrite existing values. Interrupted? Resume with --from N.",
    formatter_class=argparse.RawDescriptionHelpFormatter,
)
parser.add_argument("--apply", action="store_true", help="Write changes to Jellyfin and audio files")
parser.add_argument(
    "--force",
    action="store_true",
    help="Overwrite existing photos, bios, genres, artists, and lyrics from sources (default: fill missing only)",
)
parser.add_argument(
    "--dir",
    type=Path,
    default=default_music_dir(),
    metavar="PATH",
    help="Music library root for file writes (default: /media/music if present, else ./music)",
)
parser.add_argument(
    "--from",
    dest="start_from",
    type=int,
    default=1,
    metavar="N",
    help="Resume at album N (1-based, sorted artist/album order)",
)
parser.add_argument(
    "--workers",
    type=int,
    default=4,
    metavar="N",
    help="Parallel Jellyfin writes on --apply (default 4)",
)
args = parser.parse_args()


@dataclass
class Patch:
    item_id: str
    path: Path
    genres: list[str] | None = None
    artists: list[str] | None = None
    album_artist: str | None = None
    explicit: bool | None = None
    name: str | None = None
    add_tags: list[str] = field(default_factory=list)
    remove_tags: list[str] = field(default_factory=list)
    file_path: Path | None = None
    file_title: str | None = None
    file_genres: list[str] | None = None
    file_advisory: str | None = None
    detail: str = ""
    show_explicit: bool | None = None
    show_genres: list[str] | None = None
    lyrics_text: str | None = None
    lyrics_name: str | None = None
    image_url: str | None = None
    overview: str | None = None

    def metadata_empty(self) -> bool:
        return not (
            self.genres is not None
            or self.artists is not None
            or self.album_artist is not None
            or self.explicit is not None
            or self.name is not None
            or self.add_tags
            or self.remove_tags
            or self.overview is not None
        )

    def jellyfin_empty(self) -> bool:
        return self.metadata_empty() and not self.image_url

    def empty(self) -> bool:
        return (
            self.jellyfin_empty()
            and self.file_title is None
            and self.file_genres is None
            and self.file_advisory is None
            and self.lyrics_text is None
        )


def album_artist(item: dict) -> str:
    artist = item.get("AlbumArtist") or ""
    if artist:
        return str(artist).strip()
    artists = item.get("Artists") or []
    if isinstance(artists, list) and artists:
        return str(artists[0]).strip()
    return ""


def album_label(item: dict) -> str:
    bits = [p for p in (album_artist(item), item.get("Album") or "") if p]
    return " — ".join(bits) or "unknown album"


def current_artists(item: dict) -> list[str]:
    return [str(a).strip() for a in (item.get("Artists") or []) if str(a).strip()]


def current_genres(item: dict) -> list[str]:
    return pretty_genres([str(g) for g in (item.get("Genres") or []) if str(g).strip()])


def has_tag(item: dict, name: str) -> bool:
    want = name.lower()
    return any(str(t).strip().lower() == want for t in (item.get("Tags") or []) if str(t).strip())


_SKIP_ARTIST_META = {"various artists", "various", "unknown artist", "unknown"}


def has_primary_image(item: dict) -> bool:
    return bool((item.get("ImageTags") or {}).get("Primary"))


def has_overview(item: dict) -> bool:
    return bool(str(item.get("Overview") or "").strip())


def skip_artist_meta(name: str) -> bool:
    key = name.strip().lower()
    if not key or key in _SKIP_ARTIST_META:
        return True
    return key.startswith("[") and key.endswith("]")


def should_write(*, present: bool, differs: bool, force: bool) -> bool:
    """Force replaces differing values; otherwise only fill empties."""
    return (force and differs) or (not force and not present)


@dataclass
class ApplyResult:
    jellyfin: bool = False
    file: bool = False
    lyrics: bool = False
    image: bool = False
    file_error: Exception | None = None
    lyrics_error: Exception | None = None
    image_error: Exception | None = None


def apply_patch(uid: str, patch: Patch) -> ApplyResult:
    result = ApplyResult()
    if not patch.metadata_empty():
        result.jellyfin = update_jellyfin_item(
            uid,
            patch.item_id,
            genres=patch.genres,
            artists=patch.artists,
            album_artist=patch.album_artist,
            explicit=patch.explicit,
            name=patch.name,
            add_tags=patch.add_tags or None,
            remove_tags=patch.remove_tags or None,
            overview=patch.overview,
        )
    if patch.file_path is not None and (
        patch.file_title is not None or patch.file_genres is not None or patch.file_advisory is not None
    ):
        try:
            result.file = write_file_tags(
                patch.file_path,
                title=patch.file_title,
                genres=patch.file_genres,
                advisory=patch.file_advisory,
            )
        except Exception as exc:
            result.file_error = exc
            if not result.jellyfin:
                raise
    if patch.lyrics_text and patch.lyrics_name:
        try:
            result.lyrics = upload_lyrics(patch.item_id, patch.lyrics_name, patch.lyrics_text)
        except Exception as exc:
            result.lyrics_error = exc
            if not result.jellyfin and not result.file:
                raise
    if patch.image_url:
        try:
            data, mime = download_image(patch.image_url)
            result.image = upload_item_image(patch.item_id, data, mime)
        except Exception as exc:
            result.image_error = exc
            if not result.jellyfin and not result.file and not result.lyrics:
                raise
    return result


def _artist_lookup_job(
    item_id: str,
    name: str,
    need_photo: bool,
    need_bio: bool,
    picture: str,
) -> tuple[str, str, bool, bool, str, BioMatch, Exception | None]:
    err: Exception | None = None
    if need_photo and not picture:
        try:
            dz_artist = search_artist(name)
            if dz_artist and dz_artist.picture:
                picture = dz_artist.picture
        except Exception as exc:
            err = exc
    bio = BioMatch()
    if need_bio:
        try:
            bio = lookup_bio(name)
        except Exception as exc:
            err = err or exc
    return item_id, name, need_photo, need_bio, picture, bio, err


def _lyrics_lookup_job(
    item_id: str,
    path: Path,
    title: str,
    artists: list[str],
    album: str,
    duration: int | None,
    jf_path: str,
):
    try:
        match = lookup_lyrics_for_artists(
            title=title,
            artists=artists,
            album=album,
            duration=duration,
        )
        return item_id, path, jf_path, match, None
    except Exception as exc:
        return item_id, path, jf_path, None, exc


def run() -> None:
    force = args.force
    start_from = max(1, args.start_from)
    workers = max(1, args.workers)
    music_root = args.dir.expanduser().resolve()
    ui.print_banner(
        "PEANUT BUTTER & JELLY",
        "misses get a list at the end · fills stay quiet unless they write",
        apply_mode=args.apply,
        root=music_root,
        features=[
            ("JELLYFIN", True),
            ("FILES", True),
            ("ALBUM GENRES", True),
            ("ARTISTS", True),
            ("ARTIST PHOTOS", True),
            ("ARTIST BIOS", True),
            ("ALBUM EXPLICIT", True),
            ("TRACK EXPLICIT", True),
            ("ITUNES 🅴", True),
            ("LYRICS", True),
            ("PLAIN FALLBACK", True),
            ("NO MATCH TAG", True),
            ("FORCE", force),
        ],
        extras={
            "write": "FORCE overwrite" if force else "fill missing",
            "workers": str(workers),
            **({"resume": f"album {start_from}"} if start_from > 1 else {}),
        },
    )

    with ui.console.status("[accent]loading Jellyfin library…[/]", spinner="dots"):
        uid = resolve_user_id(user_id)
        jf_items = [item for item in fetch_jellyfin_tracks(uid) if item.get("Id")]
        jf_albums = fetch_jellyfin_albums(uid)
        jf_artists = fetch_jellyfin_artists()
        name_index = build_name_index(music_root) if music_root.is_dir() else {}

    if not music_root.is_dir():
        ui.print_callout(
            f"No music folder at {music_root}. Jellyfin will still update. "
            f"Pass --dir to the library (on the NAS that is /media/music).",
            kind="warn",
        )

    grouped: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for item in jf_items:
        grouped[(album_artist(item), strip_mark((item.get("Album") or "").strip()))].append(item)
    albums = sorted(grouped.items(), key=lambda kv: (kv[0][0].lower(), kv[0][1].lower()))
    total_albums = len(albums)
    if start_from > 1:
        albums = albums[start_from - 1 :]

    ui.print_library_strip(
        user=user_id,
        tracks=len(jf_items),
        albums=total_albums,
        artists=len(jf_artists),
        resume=start_from if start_from > 1 else None,
    )
    ui.console.print()
    ui.print_section(
        "plan",
        hint="misses print live · full manual list waits at the end",
    )
    if start_from > 1:
        ui.print_callout(f"RESUMING at album {start_from} / {total_albums}", kind="info")

    reset_dz_stats()
    reset_lrc_stats()
    reset_bio_stats()
    patches: dict[str, Patch] = {}
    lyrics_jobs: dict[str, tuple[Path, str, list[str], str, int | None, str]] = {}
    artist_photos: dict[str, tuple[str, str]] = {}
    failed = unchanged = nomatch_albums = albums_matched = 0
    explicit_yes = explicit_no = explicit_unknown = album_explicit = 0
    nomatch_tracks = files_missing = 0
    lyrics_skipped = 0
    lyrics_already = 0
    lyrics_synced = lyrics_plain = lyrics_instrumental = lyrics_nomatch = 0
    artist_photos_queued = artist_photos_nomatch = artist_photos_skip = 0
    artist_bios_queued = artist_bios_nomatch = artist_bios_skip = 0
    current_album = start_from
    gap_albums: list[tuple[str, str, int]] = []
    gap_artists: dict[str, list[str]] = {}
    gap_lyrics: dict[str, list[str]] = defaultdict(list)
    gap_fails: list[tuple[str, str]] = []

    def queue(patch: Patch) -> None:
        if patch.empty():
            return
        existing = patches.get(patch.item_id)
        if existing is None:
            patches[patch.item_id] = patch
            return
        if patch.genres is not None:
            existing.genres = patch.genres
            existing.show_genres = patch.show_genres
        if patch.artists is not None:
            existing.artists = patch.artists
        if patch.album_artist is not None:
            existing.album_artist = patch.album_artist
        if patch.explicit is not None:
            existing.explicit = patch.explicit
            existing.show_explicit = True
        if patch.name is not None:
            existing.name = patch.name
        if patch.file_path is not None:
            existing.file_path = patch.file_path
        if patch.file_title is not None:
            existing.file_title = patch.file_title
        if patch.file_genres is not None:
            existing.file_genres = patch.file_genres
        if patch.file_advisory is not None:
            existing.file_advisory = patch.file_advisory
        if patch.lyrics_text is not None:
            existing.lyrics_text = patch.lyrics_text
            existing.lyrics_name = patch.lyrics_name
        if patch.image_url is not None:
            existing.image_url = patch.image_url
        if patch.overview is not None:
            existing.overview = patch.overview
        existing.add_tags = list(dict.fromkeys([*existing.add_tags, *patch.add_tags]))
        existing.remove_tags = list(dict.fromkeys([*existing.remove_tags, *patch.remove_tags]))
        if patch.detail:
            existing.detail = f"{existing.detail} · {patch.detail}" if existing.detail else patch.detail

    def remember_lyrics(
        item: dict,
        *,
        path: Path,
        extra_artists: list[str] | None = None,
        album: str = "",
    ) -> None:
        nonlocal lyrics_skipped, lyrics_already
        item_id = item.get("Id")
        if not item_id or item_id in lyrics_jobs:
            return
        if not force and item.get("HasLyrics"):
            lyrics_already += 1
            return
        title = strip_mark(item.get("Name") or "")
        artists = lyric_query_artists(item, extra_artists)
        if not title or not artists:
            lyrics_skipped += 1
            gap_lyrics[strip_mark(album) or "unknown album"].append(title or "(no title)")
            return
        lyrics_jobs[item_id] = (
            path,
            title,
            artists,
            strip_mark(album or (item.get("Album") or "")),
            track_duration_seconds(item),
            str(item.get("Path") or ""),
        )

    def stamp_jellyfin_explicit(item: dict, fake_path: Path) -> None:
        """Jellyfin Explicit tag → title 🅴 + iTunes advisory, even without a Deezer hit."""
        nonlocal files_missing
        name = item.get("Name") or ""
        if not has_tag(item, "Explicit"):
            return
        marked = has_explicit_mark(name)
        file_path = resolve_local_path(item.get("Path") or "", music_root, name_index)
        file_title = None
        file_advisory = None
        if file_path is None:
            files_missing += 1
        else:
            tags = read_file_tags(file_path)
            if tags is None:
                files_missing += 1
            else:
                cur_title, _cur_genres, cur_advisory = tags
                want_title = desired_title(name, True)
                if cur_title != want_title:
                    file_title = want_title
                if cur_advisory != ADVISORY_EXPLICIT:
                    file_advisory = ADVISORY_EXPLICIT
        need_e = not marked
        if not need_e and file_title is None and file_advisory is None:
            return
        details: list[str] = []
        if need_e:
            details.append("Explicit + 🅴")
        bits: list[str] = []
        if file_title is not None:
            bits.append("title 🅴")
        if file_advisory is not None:
            bits.append("itunes")
        if bits:
            details.append("file " + "+".join(bits))
        queue(
            Patch(
                item["Id"],
                file_path or (fake_path / (strip_mark(name) or "track")),
                explicit=True if need_e else None,
                name=desired_title(name, True) if need_e else None,
                file_path=file_path,
                file_title=file_title,
                file_advisory=file_advisory,
                detail=" · ".join(details),
                show_explicit=True,
            )
        )

    def tag_nomatch(tracks: list[dict], fake_path: Path) -> None:
        nonlocal nomatch_tracks
        nomatch_tracks += len(tracks)
        for item in tracks:
            remember_lyrics(
                item,
                path=fake_path / strip_mark(item.get("Name") or "track"),
                album=item.get("Album") or "",
            )
            stamp_jellyfin_explicit(item, fake_path)
            if not has_tag(item, NOMATCH_TAG):
                queue(
                    Patch(
                        item["Id"],
                        fake_path / strip_mark(item.get("Name") or "track"),
                        add_tags=[NOMATCH_TAG],
                        detail=NOMATCH_TAG,
                    )
                )

    try:
        with ui.progress("deezer") as progress:
            task = progress.add_task("matching", total=len(albums))
            for album_n, ((_artist, _album), tracks) in enumerate(albums, start=start_from):
                current_album = album_n
                progress.advance(task)
                label = album_label(tracks[0])
                fake_path = Path(label)
                try:
                    match = lookup_album(_artist, _album, sample_title=strip_mark(tracks[0].get("Name") or ""))
                except Exception as e:
                    failed += 1
                    tag_nomatch(tracks, fake_path)
                    gap_fails.append((label, str(e)))
                    gap_albums.append((label, "Deezer error", len(tracks)))
                    ui.event_line("fail", fake_path, str(e))
                    continue

                if not match.album_id:
                    nomatch_albums += 1
                    tag_nomatch(tracks, fake_path)
                    gap_albums.append((label, "Deezer no match", len(tracks)))
                    ui.event_line("miss", fake_path, f"{len(tracks)} tracks")
                    continue

                albums_matched += 1
                for info in match.artist_infos:
                    if info.picture and not skip_artist_meta(info.name):
                        artist_photos.setdefault(info.name.lower(), (info.name, info.picture))
                album_ids = {t.get("AlbumId") for t in tracks if t.get("AlbumId")}
                album_bits: list[str] = []
                dz_album_artist = match.album_artist
                want_album_artists = match.artists or ([dz_album_artist] if dz_album_artist else [])
                need_album_genres = False
                need_album_artists = False
                need_album_e = False
                album_names: dict[str, str] = {}
                for album_id in album_ids:
                    album_item = jf_albums.get(album_id)
                    album_name = (
                        (album_item.get("Name") if album_item else "")
                        or _album
                        or (tracks[0].get("Album") or "")
                    )
                    album_names[album_id] = str(album_name)
                    if album_item is None:
                        need_album_genres = need_album_genres or bool(match.genres)
                        need_album_artists = need_album_artists or bool(want_album_artists)
                        need_album_e = need_album_e or (match.explicit is True)
                        continue
                    got_genres = current_genres(album_item)
                    if match.genres and should_write(
                        present=bool(got_genres),
                        differs=got_genres != match.genres,
                        force=force,
                    ):
                        need_album_genres = True
                    got_album_artists = [
                        str(p.get("Name") or "").strip()
                        for p in (album_item.get("AlbumArtists") or [])
                        if isinstance(p, dict) and str(p.get("Name") or "").strip()
                    ]
                    got_artists = current_artists(album_item)
                    got_album_artist = (album_item.get("AlbumArtist") or "").strip()
                    if want_album_artists and should_write(
                        present=bool(got_artists or got_album_artists or got_album_artist),
                        differs=(
                            got_artists != want_album_artists
                            or got_album_artist != dz_album_artist
                            or got_album_artists != want_album_artists
                        ),
                        force=force,
                    ):
                        need_album_artists = True
                    if match.explicit is True and (
                        not has_tag(album_item, "Explicit") or not has_explicit_mark(album_names[album_id])
                    ):
                        need_album_e = True
                if need_album_genres:
                    album_bits.append(f"genres → {', '.join(match.genres)}")
                if need_album_artists:
                    album_bits.append(f"artists → {dz_album_artist or ', '.join(want_album_artists)}")
                if need_album_e:
                    album_bits.append("Explicit + 🅴")
                    album_explicit += 1
                if album_bits:
                    for album_id in album_ids:
                        queue(
                            Patch(
                                album_id,
                                fake_path,
                                genres=match.genres if need_album_genres else None,
                                album_artist=dz_album_artist if need_album_artists else None,
                                artists=want_album_artists if need_album_artists else None,
                                explicit=True if need_album_e else None,
                                name=desired_title(album_names.get(album_id) or _album, True)
                                if need_album_e
                                else None,
                                detail=" · ".join(album_bits),
                                show_genres=match.genres if need_album_genres else None,
                                show_explicit=True if need_album_e else None,
                            )
                        )

                stamped = 0
                artist_writes = 0
                file_writes = 0
                for item in tracks:
                    name = item.get("Name") or ""
                    dz_track = match_deezer_track(name, match.tracks)
                    tagged = has_tag(item, "Explicit")
                    marked = has_explicit_mark(name)
                    add_tags: list[str] = []
                    remove_tags: list[str] = []
                    if dz_track is None:
                        nomatch_tracks += 1
                        explicit_unknown += 1
                        if not has_tag(item, NOMATCH_TAG):
                            add_tags.append(NOMATCH_TAG)
                    else:
                        if has_tag(item, NOMATCH_TAG):
                            remove_tags.append(NOMATCH_TAG)
                        if dz_track.explicit is True:
                            explicit_yes += 1
                        elif dz_track.explicit is False:
                            explicit_no += 1
                        else:
                            explicit_unknown += 1

                    want_e = tagged or (dz_track is not None and dz_track.explicit is True)
                    need_e = want_e and (not tagged or not marked)
                    want_artists = (dz_track.artists if dz_track and dz_track.artists else None) or (
                        [dz_album_artist] if dz_album_artist else []
                    )
                    remember_lyrics(
                        item,
                        path=fake_path / (strip_mark(name) or "track"),
                        extra_artists=want_artists,
                        album=_album,
                    )
                    need_artists = bool(want_artists) and should_write(
                        present=bool(current_artists(item) or (item.get("AlbumArtist") or "").strip()),
                        differs=(
                            current_artists(item) != want_artists
                            or (item.get("AlbumArtist") or "").strip() != dz_album_artist
                        ),
                        force=force,
                    )

                    file_path = resolve_local_path(item.get("Path") or "", music_root, name_index)
                    file_title = None
                    file_genres = None
                    file_advisory = None
                    if want_e or match.genres:
                        if file_path is None:
                            files_missing += 1
                        else:
                            tags = read_file_tags(file_path)
                            if tags is None:
                                files_missing += 1
                            else:
                                cur_title, cur_genres, cur_advisory = tags
                                if want_e:
                                    want_title = desired_title(name, True)
                                    if cur_title != want_title:
                                        file_title = want_title
                                    if cur_advisory != ADVISORY_EXPLICIT:
                                        file_advisory = ADVISORY_EXPLICIT
                                if match.genres and should_write(
                                    present=bool(cur_genres),
                                    differs=cur_genres != match.genres,
                                    force=force,
                                ):
                                    file_genres = match.genres

                    if (
                        not need_e
                        and not need_artists
                        and not add_tags
                        and not remove_tags
                        and file_title is None
                        and file_genres is None
                        and file_advisory is None
                    ):
                        unchanged += 1
                        continue

                    new_name = desired_title(name, True) if need_e else None
                    details: list[str] = []
                    if need_e:
                        details.append("Explicit + 🅴")
                        stamped += 1
                    if need_artists:
                        details.append(f"artists → {', '.join(want_artists)}")
                        artist_writes += 1
                    if file_title is not None or file_genres is not None or file_advisory is not None:
                        bits = []
                        if file_title is not None:
                            bits.append("title 🅴")
                        if file_advisory is not None:
                            bits.append("itunes")
                        if file_genres is not None:
                            bits.append("genre")
                        details.append("file " + "+".join(bits))
                        file_writes += 1
                    if NOMATCH_TAG in add_tags:
                        details.append(NOMATCH_TAG)
                    if NOMATCH_TAG in remove_tags:
                        details.append(f"clear {NOMATCH_TAG}")
                    queue(
                        Patch(
                            item["Id"],
                            file_path or (fake_path / (strip_mark(name) or "track")),
                            artists=want_artists if need_artists else None,
                            album_artist=dz_album_artist if need_artists and dz_album_artist else None,
                            explicit=True if need_e else None,
                            name=new_name,
                            add_tags=add_tags,
                            remove_tags=remove_tags,
                            file_path=file_path,
                            file_title=file_title,
                            file_genres=file_genres,
                            file_advisory=file_advisory,
                            detail=" · ".join(details),
                            show_explicit=True if need_e or file_title or file_advisory else None,
                            show_genres=file_genres,
                        )
                    )

                if album_bits or stamped or artist_writes or file_writes:
                    extra = f" · explicit {stamped}" if stamped else ""
                    if file_writes:
                        extra += f" · files {file_writes}"
                    if artist_writes and not any(b.startswith("artists") for b in album_bits):
                        album_bits.append(f"artists → {dz_album_artist or '?'}")
                    ui.event_line(
                        "dry",
                        fake_path,
                        f"{' · '.join(album_bits) or 'ok'}{extra} · {match.source}",
                        genres=match.genres if any(b.startswith("genres") for b in album_bits) or file_writes else None,
                        explicit=True if stamped or need_album_e else None,
                    )

        if jf_artists:
            ui.print_section(
                "artists",
                hint="only misses print · photo + bio list waits at the end",
            )
            artist_items = sorted(
                (item for item in jf_artists.values() if item.get("Id")),
                key=lambda item: str(item.get("Name") or "").lower(),
            )
            jobs: list[tuple[str, str, bool, bool, str]] = []
            for jf_artist in artist_items:
                name = str(jf_artist.get("Name") or "").strip()
                if skip_artist_meta(name):
                    continue
                need_photo = force or not has_primary_image(jf_artist)
                need_bio = force or not has_overview(jf_artist)
                if not need_photo:
                    artist_photos_skip += 1
                if not need_bio:
                    artist_bios_skip += 1
                if not need_photo and not need_bio:
                    continue
                picture = (artist_photos.get(name.lower()) or ("", ""))[1] if need_photo else ""
                jobs.append((jf_artist["Id"], name, need_photo, need_bio, picture))
            with ui.progress("artists") as progress:
                task = progress.add_task("matching", total=len(jobs))
                with ThreadPoolExecutor(max_workers=workers) as pool:
                    futures = [pool.submit(_artist_lookup_job, *job) for job in jobs]
                    for fut in as_completed(futures):
                        progress.advance(task)
                        item_id, name, need_photo, need_bio, picture, bio, err = fut.result()
                        if err is not None:
                            failed += 1
                            gap_fails.append((name, str(err)))
                            ui.event_line("fail", Path(name), str(err))
                        missing: list[str] = []
                        bits: list[str] = []
                        if need_photo:
                            if picture:
                                artist_photos_queued += 1
                                bits.append("photo overwrite" if force else "photo fill")
                            else:
                                artist_photos_nomatch += 1
                                missing.append("photo")
                        if need_bio:
                            if bio.overview:
                                artist_bios_queued += 1
                                bits.append(f"bio · {bio.source}")
                            else:
                                artist_bios_nomatch += 1
                                missing.append("bio")
                        if missing:
                            gap_artists[name] = missing
                            ui.event_line("miss", Path(name), " ".join(missing))
                        if not bits:
                            continue
                        queue(
                            Patch(
                                item_id,
                                Path(name),
                                image_url=picture or None,
                                overview=bio.overview or None,
                                detail=" · ".join(bits),
                            )
                        )

        lyrics_synced = lyrics_plain = lyrics_instrumental = lyrics_nomatch = 0
        if lyrics_jobs:
            ui.print_section(
                "lyrics",
                hint="LRCLIB · unmatched tracks collect into the manual list",
            )
            with ui.progress("lrclib") as progress:
                task = progress.add_task("matching", total=len(lyrics_jobs))
                with ThreadPoolExecutor(max_workers=workers) as pool:
                    futures = {
                        pool.submit(
                            _lyrics_lookup_job, item_id, path, title, artists, album, duration, jf_path
                        ): (path, title, album)
                        for item_id, (path, title, artists, album, duration, jf_path) in lyrics_jobs.items()
                    }
                    for fut in as_completed(futures):
                        progress.advance(task)
                        path, title, album = futures[fut]
                        item_id, _path, jf_path, match_lrc, err = fut.result()
                        if err is not None:
                            failed += 1
                            gap_fails.append((f"{album} — {title}".strip(" —"), str(err)))
                            ui.event_line("fail", path, str(err))
                            continue
                        if match_lrc.instrumental and not match_lrc.synced and not match_lrc.plain:
                            lyrics_instrumental += 1
                            continue
                        if match_lrc.synced:
                            kind, ext, text = "synced", ".lrc", match_lrc.synced
                            lyrics_synced += 1
                        elif match_lrc.plain:
                            kind, ext, text = "plain", ".txt", match_lrc.plain
                            lyrics_plain += 1
                        else:
                            lyrics_nomatch += 1
                            gap_lyrics[album or "unknown album"].append(title)
                            continue
                        queue(
                            Patch(
                                item_id,
                                path,
                                lyrics_text=text,
                                lyrics_name=lyrics_filename(jf_path, ext),
                                detail=f"lyrics {kind} · {match_lrc.source}",
                            )
                        )
    except KeyboardInterrupt:
        ui.console.print()
        ui.print_callout(
            f"Interrupted at album {current_album} / {total_albums}. Resume with: --from {current_album}",
            kind="warn",
        )
        ui.print_gap_report(
            albums=gap_albums,
            artists=sorted(gap_artists.items(), key=lambda kv: kv[0].lower()),
            lyrics=sorted(
                ((album, sorted(tracks)) for album, tracks in gap_lyrics.items()),
                key=lambda kv: kv[0].lower(),
            ),
            fails=gap_fails,
        )
        raise SystemExit(130) from None

    pending = len(patches)
    ui.console.print()
    ui.console.print(
        Text.assemble(
            ui.badge(f"{pending} WRITES", "apply" if args.apply else "dry"),
            Text("  ", style="mute"),
            ui.badge(f"{nomatch_albums} album misses", "chip_miss" if nomatch_albums else "chip_off"),
            Text(" "),
            ui.badge(f"{len(gap_artists)} artist misses", "chip_miss" if gap_artists else "chip_off"),
            Text(" "),
            ui.badge(f"{lyrics_nomatch} lyric misses", "chip_lyric" if lyrics_nomatch else "chip_off"),
        )
    )

    updated = skipped = files_updated = files_unwritable = lyrics_uploaded = photos_uploaded = bios_uploaded = 0
    if args.apply and patches:
        ui.print_section("apply", hint=f"{workers} parallel Jellyfin metadata + lyrics + photos + bios + file writes")
        with ui.progress("jellyfin") as progress:
            task = progress.add_task("writing", total=len(patches))
            with ThreadPoolExecutor(max_workers=workers) as pool:
                futures = {pool.submit(apply_patch, uid, patch): patch for patch in patches.values()}
                for fut in as_completed(futures):
                    progress.advance(task)
                    patch = futures[fut]
                    try:
                        result = fut.result()
                    except PermissionError as e:
                        files_unwritable += 1
                        gap_fails.append((str(patch.file_path or patch.path), str(e)))
                        ui.event_line("fail", patch.file_path or patch.path, str(e))
                        continue
                    except FileNotFoundError as e:
                        files_missing += 1
                        gap_fails.append((str(patch.file_path or patch.path), str(e)))
                        ui.event_line("fail", patch.file_path or patch.path, str(e))
                        continue
                    except Exception as e:
                        failed += 1
                        gap_fails.append((str(patch.path), str(e)))
                        ui.event_line("fail", patch.path, str(e))
                        continue
                    if result.jellyfin or result.file or result.lyrics or result.image:
                        updated += 1
                    else:
                        skipped += 1
                    if result.file:
                        files_updated += 1
                    if result.lyrics:
                        lyrics_uploaded += 1
                    if result.image:
                        photos_uploaded += 1
                    if result.jellyfin and patch.overview:
                        bios_uploaded += 1
                    if result.file_error is not None:
                        err = result.file_error
                        if isinstance(err, PermissionError):
                            files_unwritable += 1
                        elif isinstance(err, FileNotFoundError):
                            files_missing += 1
                        else:
                            failed += 1
                        ui.event_line("fail", patch.file_path or patch.path, str(err))
                    if result.lyrics_error is not None:
                        failed += 1
                        gap_fails.append((str(patch.path), str(result.lyrics_error)))
                        ui.event_line("fail", patch.path, str(result.lyrics_error))
                    if result.image_error is not None:
                        failed += 1
                        gap_fails.append((str(patch.path), str(result.image_error)))
                        ui.event_line("fail", patch.path, str(result.image_error))

    ui.print_gap_report(
        albums=gap_albums,
        artists=sorted(gap_artists.items(), key=lambda kv: kv[0].lower()),
        lyrics=sorted(
            ((album, sorted(tracks)) for album, tracks in gap_lyrics.items()),
            key=lambda kv: kv[0].lower(),
        ),
        fails=gap_fails,
    )

    stats = {
        "jellyfin_tracks": len(jf_items),
        "albums": total_albums,
        "albums_matched": albums_matched,
        "unchanged": unchanged,
        "nomatch": nomatch_albums,
        "nomatch_tracks": nomatch_tracks,
        "pending": pending if not args.apply else 0,
        "updated": updated,
        "failed": failed,
        "explicit_yes": explicit_yes,
        "explicit_no": explicit_no,
        "explicit_unknown": explicit_unknown,
        "album_explicit": album_explicit,
        "files_missing": files_missing,
        "lyrics_synced": lyrics_synced,
        "lyrics_plain": lyrics_plain,
        "lyrics_instrumental": lyrics_instrumental,
        "lyrics_nomatch": lyrics_nomatch,
        "lyrics_skipped": lyrics_skipped,
        "already_has": lyrics_already,
        "artist_photos": artist_photos_queued,
        "artist_photos_skip": artist_photos_skip,
        "artist_photos_nomatch": artist_photos_nomatch,
        "artist_bios": artist_bios_queued,
        "artist_bios_skip": artist_bios_skip,
        "artist_bios_nomatch": artist_bios_nomatch,
    }
    if args.apply:
        stats["skipped"] = skipped
        stats["files_updated"] = files_updated
        stats["files_unwritable"] = files_unwritable
        stats["lyrics_uploaded"] = lyrics_uploaded
        stats["artist_photos_uploaded"] = photos_uploaded
        stats["artist_bios_uploaded"] = bios_uploaded
    stats.update(dz_stats())
    stats.update(lrc_stats())
    stats.update(bio_stats())
    ui.print_summary(
        "peanut butter & jelly",
        stats,
        footer="NEEDS YOU is the list to work by hand · --force only if you meant to replace existing values",
    )


if __name__ == "__main__":
    try:
        run()
    except KeyboardInterrupt:
        ui.console.print()
        ui.print_callout(
            "Interrupted. Resume with --from N (album number in sorted artist/album order).",
            kind="warn",
        )
        raise SystemExit(130) from None
