"""Add hasTrustDialogAccepted / remoteControlSpawnMode for one project key, atomic replace. preset.py <key> [trust] [spawn]"""
import json, os, sys
live = os.path.expandvars(r"%USERPROFILE%\.claude.json")
key = sys.argv[1]
what = sys.argv[2:] or ["trust", "spawn"]
raw = open(live, "rb").read()
d = json.loads(raw)
p = d.setdefault("projects", {}).setdefault(key, {})
if "trust" in what: p["hasTrustDialogAccepted"] = True
if "spawn" in what: p.setdefault("remoteControlSpawnMode", "same-dir")
tmp = live + ".m0tmp"
with open(tmp, "wb") as f:
    f.write(json.dumps(d, indent=2, ensure_ascii=False).encode("utf-8"))
os.replace(tmp, live)
print("preset", repr(key), json.load(open(live, encoding="utf-8"))["projects"][key])
