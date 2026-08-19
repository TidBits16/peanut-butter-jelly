#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

version="$(python3 - <<'PY'
import re
from pathlib import Path
text = Path("Jellyfin.Plugin.PeanutButterJelly.csproj").read_text()
print(re.search(r"<Version>([^<]+)</Version>", text).group(1))
PY
)"

export PATH="${HOME}/.dotnet:${PATH}"
dotnet build -c Release --nologo

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp "bin/Release/net9.0/Jellyfin.Plugin.PeanutButterJelly.dll" "$stage/"
cp meta.json "$stage/"

mkdir -p dist
zip_path="$root/dist/peanut-butter-jelly_${version}.zip"
rm -f "$zip_path"
python3 - "$stage" "$zip_path" <<'PY'
import sys, zipfile
from pathlib import Path
stage, zip_path = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for name in ("Jellyfin.Plugin.PeanutButterJelly.dll", "meta.json"):
        zf.write(Path(stage) / name, name)
PY

checksum="$(python3 - "$zip_path" <<'PY'
import hashlib, sys
from pathlib import Path
print(hashlib.md5(Path(sys.argv[1]).read_bytes()).hexdigest())
PY
)"
timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
source_url="https://github.com/TidBits16/peanut-butter-jelly/releases/download/v${version}/peanut-butter-jelly_${version}.zip"

python3 - "$version" "$checksum" "$timestamp" "$source_url" <<'PY'
import json, sys
from pathlib import Path

version, checksum, timestamp, source_url = sys.argv[1:]
meta = json.loads(Path("meta.json").read_text())
entry = {
    "guid": meta["guid"],
    "name": meta["name"],
    "description": meta["description"],
    "overview": meta["overview"],
    "owner": meta["owner"],
    "category": meta["category"],
    "imageUrl": meta.get("imageUrl") or "",
    "versions": [],
}

manifest_path = Path("manifest.json")
if manifest_path.exists():
    data = json.loads(manifest_path.read_text())
    if isinstance(data, list) and data:
        entry = data[0]

versions = [v for v in entry.get("versions", []) if v.get("version") != version]
versions.insert(0, {
    "version": version,
    "changelog": meta.get("changelog") or f"Release {version}",
    "targetAbi": meta.get("targetAbi") or "10.11.0.0",
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp,
})
entry["versions"] = versions
entry["guid"] = meta["guid"]
entry["name"] = meta["name"]
entry["description"] = meta["description"]
entry["overview"] = meta["overview"]
entry["owner"] = meta["owner"]
entry["category"] = meta["category"]
Path("manifest.json").write_text(json.dumps([entry], indent=2) + "\n")
print(f"zip {source_url}")
print(f"md5 {checksum}")
PY
