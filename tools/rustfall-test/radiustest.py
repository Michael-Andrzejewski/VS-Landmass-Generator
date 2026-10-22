"""Per-spawner radius and serpent linger, with the joined player.
    python radiustest.py"""
import json, os, time, urllib.request, urllib.parse

B = "http://127.0.0.1:4590"
LOGS = os.path.join(os.environ["APPDATA"], "VintagestoryData", "Logs")
DBG = os.path.join(LOGS, "server-debug.log")
MAIN = os.path.join(LOGS, "server-main.log")


def get(path):
    with urllib.request.urlopen(B + path, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


def cmd(c):
    return get("/cmd?" + urllib.parse.urlencode({"c": c, "as": "AlignedMichael"})).get("message")


def tail(path, mark):
    with open(path, encoding="utf-8", errors="replace") as f:
        f.seek(mark)
        return f.read()


def count(x, z, r, what):
    mark = os.path.getsize(MAIN)
    cmd("/gencount %d %d %d" % (x, z, r))
    for _ in range(40):
        time.sleep(0.5)
        for line in tail(MAIN, mark).splitlines():
            if "[gencount] around" in line:
                return what in line, line.split("r%d:" % r)[-1].strip()
    return None, "no answer"


for _ in range(120):
    try:
        if get("/status").get("playing", 0) > 0: break
    except Exception: pass
    time.sleep(3)
time.sleep(5)
print(cmd("/gamemode survival"))

# 1. Deep shiver lair at 1890,65,-1636 (radius 60): stand 45 blocks away, out of 20
print("shiver: standing 45 blocks from the lair spawner")
mark = os.path.getsize(DBG)
cmd("/tp 1890 67 -1591")      # in the mine? may be rock: use the mine path instead below if it fails
got = None
for _ in range(20):
    time.sleep(0.5)
    for line in tail(DBG, mark).splitlines():
        if "[oversizedhorrors] spawner at 513890, 65" in line: got = line.split("[Debug] ")[-1]
    if got: break
cmd("/tp 1850 140 -1500")
print("  ->", got or "NOTHING")

# 2. Serpent spawner at -72,85,-146 (radius 80): swim 65 blocks east of it
print("serpent: in the water 65 blocks from the spawner")
mark = os.path.getsize(DBG)
cmd("/tp -7 109 -146")
t0 = time.time()
got = None
for _ in range(40):
    time.sleep(0.5)
    log = tail(DBG, mark)
    if "spawned seaserpent" in log:
        got = [l for l in log.splitlines() if "spawned seaserpent" in l][0].split("] ")[-1]
        break
print("  ->", got or "NOTHING (debug logging may be off)")
time.sleep(4)
print("  serpent near spawner:", count(-40, -146, 90, "seaserpent"))

# 3. Ashore within 120 blocks: the cattail islet
cmd("/tp -107 117 -137")
t_land = time.time()
for wait in (30, 60, 100, 140):
    time.sleep(max(0, t_land + wait - time.time()))
    present, line = count(-72, -146, 130, "seaserpent")
    print("  %3d s ashore: serpent %s   (%s)" % (wait, "still here" if present else "gone", line[:120]))
