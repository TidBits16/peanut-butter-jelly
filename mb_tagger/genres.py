"""Genre name pretty-printing and dedupe."""

from __future__ import annotations

_GENRE_PRETTY: dict[str, str] = {
    # media
    "soundtrack": "Soundtrack",
    "score": "Score",
    "ost": "OST",
    "original soundtrack": "Original Soundtrack",
    "film soundtrack": "Film Soundtrack",
    "tv soundtrack": "TV Soundtrack",
    "video game music": "Video Game Music",
    "vgm": "VGM",
    "anime": "Anime",
    "musical": "Musical",
    "instrumental": "Instrumental",
    # electronic
    "electronic": "Electronic",
    "electronica": "Electronica",
    "edm": "EDM",
    "ambient": "Ambient",
    "dark ambient": "Dark Ambient",
    "drone": "Drone",
    "idm": "IDM",
    "glitch": "Glitch",
    "breakbeat": "Breakbeat",
    "drum and bass": "Drum and Bass",
    "dnb": "DnB",
    "jungle": "Jungle",
    "garage": "Garage",
    "uk garage": "UK Garage",
    "2 step": "2-Step",
    "house": "House",
    "deep house": "Deep House",
    "tech house": "Tech House",
    "progressive house": "Progressive House",
    "techno": "Techno",
    "minimal techno": "Minimal Techno",
    "trance": "Trance",
    "psytrance": "Psytrance",
    "hardstyle": "Hardstyle",
    "dubstep": "Dubstep",
    "brostep": "Brostep",
    "riddim": "Riddim",
    "phonk": "Phonk",
    "drift phonk": "Drift Phonk",
    "brazilian phonk": "Brazilian Phonk",
    "wave": "Wave",
    "hardwave": "Hardwave",
    "synthwave": "Synthwave",
    "retrowave": "Retrowave",
    "outrun": "Outrun",
    "darksynth": "Darksynth",
    "dark wave": "Dark Wave",
    "cold wave": "Cold Wave",
    "new wave": "New Wave",
    "minimal wave": "Minimal Wave",
    "industrial": "Industrial",
    "ebm": "EBM",
    "aggrotech": "Aggrotech",
    "future bass": "Future Bass",
    "chillwave": "Chillwave",
    "vaporwave": "Vaporwave",
    "witch house": "Witch House",
    "hyperpop": "Hyperpop",
    "electropop": "Electropop",
    "synthpop": "Synthpop",
    "chiptune": "Chiptune",
    "8 bit": "8-Bit",
    # rock / metal
    "rock": "Rock",
    "alternative rock": "Alternative Rock",
    "indie rock": "Indie Rock",
    "art rock": "Art Rock",
    "progressive rock": "Progressive Rock",
    "psychedelic rock": "Psychedelic Rock",
    "garage rock": "Garage Rock",
    "punk": "Punk",
    "punk rock": "Punk Rock",
    "post punk": "Post-Punk",
    "pop punk": "Pop Punk",
    "emo": "Emo",
    "midwest emo": "Midwest Emo",
    "screamo": "Screamo",
    "hardcore": "Hardcore",
    "post hardcore": "Post-Hardcore",
    "metalcore": "Metalcore",
    "deathcore": "Deathcore",
    "metal": "Metal",
    "heavy metal": "Heavy Metal",
    "black metal": "Black Metal",
    "death metal": "Death Metal",
    "doom metal": "Doom Metal",
    "sludge metal": "Sludge Metal",
    "thrash metal": "Thrash Metal",
    "nu metal": "Nu Metal",
    "folk metal": "Folk Metal",
    "gothic": "Gothic",
    "gothic rock": "Gothic Rock",
    "goth": "Goth",
    "shoegaze": "Shoegaze",
    "dream pop": "Dream Pop",
    "noise rock": "Noise Rock",
    "math rock": "Math Rock",
    "grunge": "Grunge",
    "britpop": "Britpop",
    # pop / hip-hop / r&b
    "pop": "Pop",
    "indie pop": "Indie Pop",
    "art pop": "Art Pop",
    "k pop": "K-Pop",
    "j pop": "J-Pop",
    "dance pop": "Dance Pop",
    "hip hop": "Hip Hop",
    "rap": "Rap",
    "trap": "Trap",
    "drill": "Drill",
    "cloud rap": "Cloud Rap",
    "lo-fi": "Lo-Fi",
    "lo fi hip hop": "Lo-Fi Hip Hop",
    "r&b": "R&B",
    "contemporary r&b": "Contemporary R&B",
    "soul": "Soul",
    "neo soul": "Neo-Soul",
    "funk": "Funk",
    "disco": "Disco",
    # other
    "jazz": "Jazz",
    "smooth jazz": "Smooth Jazz",
    "fusion": "Fusion",
    "blues": "Blues",
    "country": "Country",
    "folk": "Folk",
    "indie folk": "Indie Folk",
    "singer songwriter": "Singer-Songwriter",
    "classical": "Classical",
    "orchestral": "Orchestral",
    "chamber music": "Chamber Music",
    "opera": "Opera",
    "world": "World",
    "latin": "Latin",
    "reggae": "Reggae",
    "dub": "Dub",
    "ska": "Ska",
    "experimental": "Experimental",
    "avant garde": "Avant-Garde",
    "noise": "Noise",
    "indie": "Indie",
    "alternative": "Alternative",
    "acoustic": "Acoustic",
    "ballad": "Ballad",
}

# Small words kept lowercase inside multi-word Title Case (unless first).
_SMALL_WORDS = {"and", "the", "of", "a", "an", "vs", "with"}

# Hyphenated compound heads that should keep Cap-Cap form.
_HYPHEN_COMPOUNDS = {
    "post",
    "pre",
    "neo",
    "non",
    "anti",
    "multi",
}


def norm_genre_key(name: str) -> str:
    """Normalize for dedupe / lookup (lowercase, collapsed spaces, aliases)."""
    s = name.strip().lower().replace("_", " ")
    s = s.replace("-", " ")
    while "  " in s:
        s = s.replace("  ", " ")
    equivalents = {
        "darkwave": "dark wave",
        "coldwave": "cold wave",
        "hiphop": "hip hop",
        "hip hop": "hip hop",
        "lofi": "lo-fi",
        "lo fi": "lo-fi",
        "rnb": "r&b",
        "drum & bass": "drum and bass",
        "d&b": "drum and bass",
        "video game soundtrack": "video game music",
        "game soundtrack": "video game music",
        "kpop": "k pop",
        "jpop": "j pop",
        "neosoul": "neo soul",
        "avantgarde": "avant garde",
        "singersongwriter": "singer songwriter",
        "singer & songwriter": "singer songwriter",
        "rap/hip hop": "hip hop",
        "soul & funk": "soul",
        "films/games": "soundtrack",
    }
    return equivalents.get(s, s)


def pretty_genre(name: str) -> str:
    """Canonical display form for a genre tag."""
    raw = (name or "").strip()
    if not raw:
        return ""
    key = norm_genre_key(raw)
    if key in _GENRE_PRETTY:
        return _GENRE_PRETTY[key]

    # Smart Title Case for unknown / compound tags.
    words = key.split()
    out: list[str] = []
    for i, word in enumerate(words):
        if i > 0 and word in _SMALL_WORDS:
            out.append(word)
            continue
        if word in {"uk", "us", "tv", "dj", "mc", "r&b", "dnb", "edm", "idm", "ebm", "ost", "vgm"}:
            out.append(word.upper() if word != "r&b" else "R&B")
            continue
        if word.isdigit() or (len(word) > 1 and word[0].isdigit()):
            # e.g. 2 step already handled; 8 bit → 8-Bit via map
            out.append(word.upper() if word.isalpha() else word.capitalize())
            continue
        out.append(word.capitalize())

    # Re-join known compound pairs with hyphens: "Post Punk" → already in map;
    # for "melodic death metal" leave spaces.
    pretty = " ".join(out)

    # Hyphenate post-/neo- style when second token exists and head is known.
    parts = pretty.split()
    if len(parts) >= 2 and parts[0].lower() in _HYPHEN_COMPOUNDS:
        pretty = f"{parts[0]}-{parts[1]}" + ((" " + " ".join(parts[2:])) if len(parts) > 2 else "")

    return pretty


def pretty_genres(names: list[str], *, max_genres: int | None = None) -> list[str]:
    """Dedupe + pretty-format a genre list (order preserved)."""
    out: list[str] = []
    seen: set[str] = set()
    for name in names:
        pretty = pretty_genre(name)
        if not pretty:
            continue
        key = norm_genre_key(pretty)
        if key in seen:
            continue
        seen.add(key)
        out.append(pretty)
        if max_genres is not None and len(out) >= max_genres:
            break
    return out
