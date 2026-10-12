#!/usr/bin/env python3
"""Adds a release to manifest.json (Jellyfin plugin repository format).

usage: update_manifest.py <zip> <version e.g. 1.0.0.0> <download url>
Reads name/guid/targetAbi/changelog from build.yaml.
"""
import datetime, hashlib, json, os, re, sys

zip_path, version, url = sys.argv[1:4]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
meta = {}
text = open(os.path.join(root, "build.yaml"), encoding="utf-8").read()
for key in ("name", "guid", "targetAbi", "owner", "overview", "category"):
    m = re.search(rf'^{key}:\s*"?(.*?)"?\s*$', text, re.M)
    meta[key] = m.group(1) if m else ""
desc = re.search(r"^description: >\n((?:  .*\n)+)", text, re.M)
change = re.search(r"^changelog: >\n((?:  .*\n)+)", text, re.M)
fold = lambda m: " ".join(l.strip() for l in m.group(1).splitlines()) if m else ""

path = os.path.join(root, "manifest.json")
manifest = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else []
plugin = next((p for p in manifest if p["guid"] == meta["guid"]), None)
if plugin is None:
    plugin = {"guid": meta["guid"], "versions": []}
    manifest.append(plugin)
plugin.update(name=meta["name"], description=fold(desc), overview=meta["overview"], owner=meta["owner"], category=meta["category"])
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": fold(change),
    "targetAbi": meta["targetAbi"],
    "sourceUrl": url,
    "checksum": hashlib.md5(open(zip_path, "rb").read()).hexdigest(),
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})
json.dump(manifest, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print(f"manifest.json: {meta['name']} {version}")
