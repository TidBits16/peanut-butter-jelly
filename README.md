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
| **Albums** | Deezer genres + artists. Title gets 🅴 when the *album* is explicit. The `Explicit` **tag is never written on albums** (Jellyfin would smear it onto every track). If that tag is already there, it gets scraped off. |
| **Tracks** | Per-track `Explicit` tag + title 🅴 from the *track* lookup. Unmatched songs get `DeezerNoMatch`; the tag drops when a match shows up. |
| **Lyrics** | [LRCLIB](https://lrclib.net) — synced `.lrc` when it exists, otherwise plain text. Instrumentals with no words are left alone. |
| **Artists** | Photos from Deezer. Bios from AudioDB, then Wikipedia / Wikidata. |
| **Playlists** | Membership is snapshotted, then rematched by library-relative path (the per-user folder under `media/music/` is stripped). Empty playlists can be salvaged from Jellyfin cleanup logs: `Item in "NAME" cannot be found at "PATH"`. |

Fill-missing is the default. Flip **Overwrite existing values** only when you want sources to replace tags you already like.

The scheduled task **writes for real**. There is no dry-run switch anymore.

---

## Install

Built for **Jellyfin 10.10.x** (`net8.0`).

```bash
dotnet build -c Release
```

Drop the DLL (and `meta.json` if you want the catalog extras) into a plugin folder:

```
{jellyfin-data}/plugins/PeanutButterJelly/Jellyfin.Plugin.PeanutButterJelly.dll
```

Linux packages usually mean `/var/lib/jellyfin/plugins/`. Docker means whatever you mounted as config/data. Then **restart Jellyfin**.

1. **Dashboard → Plugins → Peanut Butter & Jelly** — save the toggles you want.
2. **Dashboard → Scheduled Tasks → Peanut Butter & Jelly** — default is every 24 hours. Hit **Run now** once so you can watch the log.

The plugin talks outbound HTTPS to Deezer, LRCLIB, AudioDB, Wikipedia, and Wikidata. HTTP answers are cached under Jellyfin’s cache dir (`peanut-butter-jelly/`). Playlist snapshots sit in plugin data.

---

## After the sandwich

If a track still has `DeezerNoMatch`, Deezer never locked onto it — check artist/album spelling in Jellyfin, then run the task again (or enable overwrite if stale metadata is blocking a rewrite).

If playlists went hollow after a library move, leave **Repair playlists** on and let one full run finish; the snapshot + cleanup-log salvage is the recovery path.

That is the whole lunch menu. Spread it on the server and let it sit.
