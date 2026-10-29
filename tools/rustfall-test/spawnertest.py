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

# spawner HUD position, where to stand (x y z, on a lair's level run and
# inside its wake range), name: Landmass Generator 0.71.0's tin mine
SPOTS = [
    ((1901, 122, -1586), (1909, 126, -1586), "mouth (surface) shiver"),
    ((1919, 30, -1559), (1925, 32, -1563), "mine lair shiver 1 (wakes within 14)"),
    ((1890, 25, -1552), (1877, 31, -1533), "mine lair shiver 2 (wakes within 32)"),
    ((1944, 30, -1569), (1948, 32, -1578), "mine lair shiver 3 (wakes within 18)"),
    ((1958, 31, -1597), (1963, 34, -1603), "mine lair shiver 4 (wakes within 18)"),
    ((1879, 23, -1578), (1885, 25, -1571), "deepest lair locust (wakes within 15)"),
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
