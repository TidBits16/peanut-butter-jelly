# Peanut Butter & Jelly

**Deezer Music API → Jellyfin** — genres · artists · dates · covers · 🅴 · lyrics · bios

Two slices. One library.

**Peanut butter** is Deezer: the API is the source of truth for genres, album/track artists, release dates, track numbers, labels, covers, photos, and explicit marks.  
**Jelly** is Jellyfin: that metadata actually gets written on the server. Lyrics (LRCLIB) and bios (AudioDB / Wikipedia) are optional extras because Deezer does not provide them.

There used to be a terminal UI. It was cute. It also meant you had to remember to run a binary. This version lives where the music already lives — Dashboard → Scheduled Tasks — and tags the library while you are doing anything else.

---

## Flavor

| Layer | What you get |
| --- | --- |
| **Albums** | Deezer genres, album artists, song artists (separate Jellyfin fields), release date, label, UPC, Deezer ID, and cover art. Title can get the explicit mark when the *album* is explicit. **Tags are never written on albums** (Jellyfin would smear them onto every track). If those tag names are already there, they get scraped off. |
| **Tracks** | Song artists from Deezer track credits, album artists from the album, genres, release date, track/disc numbers, ISRC, Deezer ID, and optional explicit tags + title mark from the *track* lookup. Unmatched songs get `DeezerNoMatch`; the tag drops when a match shows up. |
| **Lyrics** | [LRCLIB](https://lrclib.net) — not Deezer. Synced `.lrc` when it exists, otherwise plain text. Instrumentals with no words are left alone. |
| **Artists** | Photos and Deezer IDs from Deezer. Bios from AudioDB, then Wikipedia / Wikidata (optional; Deezer has no bios). |
| **Playlists** | Membership is snapshotted, then rematched by library-relative path (the per-user folder under `media/music/` is stripped). Empty playlists can be salvaged from Jellyfin cleanup logs: `Item in "NAME" cannot be found at "PATH"`. |

**Explicit** has its own plugin page section: which Jellyfin tag names to add (comma-separated), whether to rename titles, what text to append or prepend, and whether to rewrite titles every run. Default is add `Explicit`, append ` 🅴`, and only touch a title when the mark is missing or stale — turn on rewrite after you change the mark or placement. Unknown Deezer values are left alone.

Deezer genres, artists, dates, track numbers, labels, and IDs **always update** when they differ. Flip **Overwrite lyrics, bios, and artwork** only when you want those extras replaced too.

The scheduled task **writes for real**. There is no dry-run switch anymore.

---

## Install

This is a normal Jellyfin plugin. You add **this repo** as a plugin catalog, then install it from the Catalog tab like anything else.

1. **Dashboard → Plugins → Repositories** (the `+` / gear on some skins).
2. Add a repository:
   - **Name:** `Peanut Butter & Jelly`
   - **URL:** `https://cdn.jsdelivr.net/gh/TidBits16/peanut-butter-jelly@main/manifest.json`
3. Open **Catalog**, find **Peanut Butter & Jelly**, hit **Install**.
4. Restart Jellyfin when it asks.

Then: **Dashboard → Plugins → Peanut Butter & Jelly** for toggles. **Save** writes settings; **Force Run** saves and starts the task immediately. It also runs on a schedule from **Dashboard → Scheduled Tasks → Peanut Butter & Jelly** (default every 24 hours).

Built for **Jellyfin 10.11.x**. Releases are versioned zips (`dll` + `meta.json`) attached to [GitHub Releases](https://github.com/TidBits16/peanut-butter-jelly/releases).

<details>
<summary>Sideload a zip by hand</summary>

```bash
bash scripts/package.sh
```

Unzip `dist/peanut-butter-jelly_*.zip` into `{jellyfin-data}/plugins/PeanutButterJelly/` and restart.
</details>

---

## After the sandwich

If a track still has `DeezerNoMatch`, Deezer never locked onto it — check artist/album spelling in Jellyfin, then run the task again.

If playlists went hollow after a library move, leave **Repair playlists** on and let one full run finish; the snapshot + cleanup-log salvage is the recovery path.

That is the whole lunch menu. Spread it on the server and let it sit.
