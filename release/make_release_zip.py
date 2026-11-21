"""Project StarCraft release zip from the live install.
usage: python make_release_zip.py <StarCraft dir> <tester zip> <repo dir> <version> [<ready-to-fly .craft> ...]
- GameData/KAESA/StarCraft: parts (.mu/.cfg/.dds, no Source / PluginData / @thumbs / Pterodactyl), Patches (.cfg),
  Plugins (.dll); the category cfg and its two icons are taken from the tester zip (the live install keeps them at
  GameData/KAESA)
- ONE version file (KSP-AVC, read by CKAN's $vref): GameData/KAESA/StarCraft/ProjectStarCraft.version
- README.md, LICENSE.txt, CHANGELOG.md from the repo at the zip root
- sample crafts (optional extra arguments) go to Ships/VAB/<file name>; CKAN installs each with its own `file:` line
- ONE install root for the mod itself (GameData/KAESA/StarCraft). Refuses to overwrite an existing zip."""
import os, sys, zipfile, json
SC, TESTER, REPO, VER = sys.argv[1:5]; CRAFTS = sys.argv[5:]
V = tuple(int(x) for x in VER.split("."))
NEW = os.path.join(REPO, "release", "ProjectStarCraft-%s.zip" % VER)

def ship(rel):
    p = rel.replace("\\", "/")
    if any(s in ("Source", "PluginData", "@thumbs") for s in p.split("/")): return False
    if p.startswith("Parts/Pterodactyl/"): return False      # the user's own BDB-based engine, not part of StarCraft
    if p.startswith("Parts/"): return p.endswith((".mu", ".cfg", ".dds"))
    if p.startswith("Patches/"): return p.endswith(".cfg")
    if p.startswith("Plugins/"): return p.endswith(".dll")
    return False

files = sorted(os.path.relpath(os.path.join(dp, f), SC).replace("\\", "/") for dp, dn, fn in os.walk(SC) for f in fn
               if ship(os.path.relpath(os.path.join(dp, f), SC)))
if os.path.exists(NEW): sys.exit("exists, not overwriting: " + NEW)
version = {"NAME": "Project StarCraft", "VERSION": dict(zip(("MAJOR", "MINOR", "PATCH"), V[:3])),
           "KSP_VERSION": {"MAJOR": 1, "MINOR": 12, "PATCH": 5},
           "KSP_VERSION_MIN": {"MAJOR": 1, "MINOR": 12, "PATCH": 0},
           "KSP_VERSION_MAX": {"MAJOR": 1, "MINOR": 12, "PATCH": 99}}
with zipfile.ZipFile(TESTER) as zt, zipfile.ZipFile(NEW, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zn:
    for n in ("GameData/KAESA/StarCraft/KAESA_StarCraft_Category.cfg", "GameData/KAESA/StarCraft/Icons/KAESA_N.png",
              "GameData/KAESA/StarCraft/Icons/KAESA_S.png"):
        zn.writestr(n, zt.read(n))
    zn.writestr("GameData/KAESA/StarCraft/ProjectStarCraft.version", json.dumps(version, indent=2))
    for rel in files:
        zn.write(os.path.join(SC, rel), "GameData/KAESA/StarCraft/" + rel)
    for c in CRAFTS:
        zn.write(c, "Ships/VAB/" + os.path.basename(c))
    for doc in ("README.md", "LICENSE.txt", "CHANGELOG.md"):
        zn.write(os.path.join(REPO, doc), doc)
print("files", len(files) + 7 + len(CRAFTS), "zip MB %.1f" % (os.path.getsize(NEW) / 1048576), NEW)
