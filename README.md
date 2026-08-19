# Peanut Butter & Jelly

```
         .----------.
        /  peanut    \
       /    butter    \
      /________________\
      \      jelly     /
       \______________/
            │  │
         jellyfin
```

**Deezer Music API × Jellyfin** — genres · artists · 🅴 · lyrics · bios

Two slices. One library.

**Peanut butter** is Deezer: genres, album/track artists, explicit marks.  
**Jelly** is Jellyfin: the metadata, lyrics, photos, and playlists actually get written on the server.

There used to be a terminal UI. It was cute. It also meant you had to remember to run a binary. This version lives where the music already lives — Dashboard → Scheduled Tasks — and tags the library while you are doing anything else.

---

## Flavor

| Layer | What you get |
| --- | --- |
| **Albums** | Deezer genres. **Album artists** and **song artists** go to Jellyfin’s two separate fields (not copied into each other). Title gets 🅴 when the *album* is explicit. The `Explicit` **tag is never written on albums** (Jellyfin would smear it onto every track). If that tag is already there, it gets scraped off. |
| **Tracks** | Per-track song artists from Deezer’s track credits, album artists from the album, `Explicit` tag + title 🅴 from the *track* lookup. Unmatched songs get `DeezerNoMatch`; the tag drops when a match shows up. |
| **Lyrics** | [LRCLIB](https://lrclib.net) — synced `.lrc` when it exists, otherwise plain text. Instrumentals with no words are left alone. |
| **Artists** | Photos from Deezer. Bios from AudioDB, then Wikipedia / Wikidata. |
| **Playlists** | Membership is snapshotted, then rematched by library-relative path (the per-user folder under `media/music/` is stripped). Empty playlists can be salvaged from Jellyfin cleanup logs: `Item in "NAME" cannot be found at "PATH"`. |

Fill-missing is the default. Flip **Overwrite existing values** only when you want sources to replace tags you already like.

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

Then: **Dashboard → Plugins → Peanut Butter & Jelly** for toggles, and **Dashboard → Scheduled Tasks → Peanut Butter & Jelly** to run it (default every 24 hours).

Built for **Jellyfin 10.10.x**. Releases are versioned zips (`dll` + `meta.json`) attached to [GitHub Releases](https://github.com/TidBits16/peanut-butter-jelly/releases).

<details>
<summary>Sideload a zip by hand</summary>

```bash
bash scripts/package.sh
```

Unzip `dist/peanut-butter-jelly_*.zip` into `{jellyfin-data}/plugins/PeanutButterJelly/` and restart.
</details>

---

## After the sandwich

If a track still has `DeezerNoMatch`, Deezer never locked onto it — check artist/album spelling in Jellyfin, then run the task again (or enable overwrite if stale metadata is blocking a rewrite).

If playlists went hollow after a library move, leave **Repair playlists** on and let one full run finish; the snapshot + cleanup-log salvage is the recovery path.

That is the whole lunch menu. Spread it on the server and let it sit.
