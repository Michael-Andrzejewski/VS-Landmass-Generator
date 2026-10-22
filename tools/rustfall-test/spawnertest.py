"""Walk the joined player up to each Tin Mine spawner and back out.
    python spawnertest.py"""
import json, os, time, urllib.request, urllib.parse

B = "http://127.0.0.1:4590"
LOG = os.path.join(os.environ["APPDATA"], "VintagestoryData", "Logs", "server-debug.log")


def get(path):
    with urllib.request.urlopen(B + path, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


def cmd(c):
    return get("/cmd?" + urllib.parse.urlencode({"c": c, "as": "AlignedMichael"})).get("message")


for _ in range(120):
    try:
        if get("/status").get("playing", 0) > 0: break
    except Exception: pass
    time.sleep(3)
time.sleep(5)

# spawner HUD position, where to stand (x y z), name
SPOTS = [
    ((1901, 122, -1586), (1909, 126, -1586), "mouth (surface) shiver"),
    ((1890, 65, -1636), (1897, 67, -1636), "mine lair shiver 1"),
    ((1916, 88, -1633), (1923, 90, -1633), "mine lair shiver 2"),
    ((1913, 36, -1577), (1920, 38, -1577), "mine lair shiver 3"),
    ((1947, 29, -1588), (1954, 31, -1588), "mine lair shiver 4"),
    ((1887, 23, -1577), (1894, 25, -1577), "deepest lair locust"),
]
SAFE = "/tp 1850 140 -1500"
print(cmd("/gamemode survival"))
for spawner, stand, name in SPOTS:
    mark = os.path.getsize(LOG)
    print(cmd("/tp %d %d %d" % stand), end=" | ")
    got = None
    for _ in range(30):
        time.sleep(0.5)
        with open(LOG, encoding="utf-8", errors="replace") as f:
            f.seek(mark)
            new = f.read()
        for line in new.splitlines():
            if "[oversizedhorrors] spawner" in line:
                got = line.split("[Debug] ")[-1]
        if got: break
    cmd(SAFE)
    print(name, "->", got or "NOTHING within 15 s")
    time.sleep(2)
