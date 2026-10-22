"""Make a headless Rustfall test world folder.

    python mkworld.py <name> <port> <savename> [plan-file]

Creates <RUSTFALL_TEST_DIR or %TEMP%\\rustfall-test>\\<name>\\data with:
  - template/serverconfig.json (a Rustfall world config: rustfallWorld on),
    its Port, ModPaths and save location filled in;
  - a copy of Michael's own Mods and ModConfig, so the world holds only
    blocks his game knows;
  - the repo's shapes and lore, and the world plan (default: his live one).
Then run it (see README.md in this folder):
  VintagestoryServer.exe --dataPath <that data folder>
with PILOT_BRIDGE_PORT set to a port DIFFERENT from <port>.
"""
import json, os, shutil, sys, glob

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DATA = os.path.join(os.environ["APPDATA"], "VintagestoryData")
SCR = os.environ.get("RUSTFALL_TEST_DIR") or os.path.join(os.environ["TEMP"], "rustfall-test")
name, port, save = sys.argv[1], int(sys.argv[2]), sys.argv[3]
plan = sys.argv[4] if len(sys.argv) > 4 else os.path.join(DATA, "LandmassGenerator", "worldplan.txt")

dst = os.path.join(SCR, name, "data")
if os.path.exists(os.path.join(SCR, name)):
    shutil.rmtree(os.path.join(SCR, name))
os.makedirs(os.path.join(dst, "Saves"))
shutil.copy(os.path.join(HERE, "template", "serverconfig.json"), dst)

mods = os.path.join(dst, "Mods")
shutil.copytree(os.path.join(DATA, "Mods"), mods)
if os.path.isdir(os.path.join(DATA, "ModConfig")):
    shutil.copytree(os.path.join(DATA, "ModConfig"), os.path.join(dst, "ModConfig"))

lg = os.path.join(dst, "LandmassGenerator")
os.makedirs(lg)
for f in glob.glob(os.path.join(REPO, "shapes", "*.txt")):
    shutil.copy(f, lg)
shutil.copytree(os.path.join(REPO, "lore"), os.path.join(lg, "lore"))
shutil.copy(plan, os.path.join(lg, "worldplan.txt"))

cfgp = os.path.join(dst, "serverconfig.json")
c = json.load(open(cfgp, encoding="utf-8-sig"))
c["Port"] = port
c["ModPaths"] = ["Mods", mods]   # "Mods" is the game install's own core mods; dropping it fails with game@1.22.0 missing
c["WorldConfig"]["SaveFileLocation"] = os.path.join(dst, "Saves", save + ".vcdbs")
c["WorldConfig"]["WorldName"] = save
json.dump(c, open(cfgp, "w", encoding="utf-8"), indent=2)
print("ready:", dst)
print(sorted(os.listdir(mods)))
