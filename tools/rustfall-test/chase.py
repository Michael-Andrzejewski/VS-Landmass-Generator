"""Wake a mine giant in a real client and see whether it follows the player
up its run. The player stands within its wake range, then moves up the run
whenever the giant comes within 14 blocks.
    python chase.py <spawner x,y,z> <stand x,y,z> <next x,y,z> ..."""
import json, math, os, re, sys, time, urllib.request, urllib.parse

B = "http://127.0.0.1:4590"
LOGS = os.path.join(os.environ["APPDATA"], "VintagestoryData", "Logs")


def get(path, t=60):
    with urllib.request.urlopen(B + path, timeout=t) as r:
        return json.loads(r.read().decode("utf-8"))


def cmd(c):
    try:
        return get("/cmd?" + urllib.parse.urlencode({"c": c, "as": "AlignedMichael"})).get("message")
    except Exception as e:
        return "ERR " + str(e)


def tail(name, mark):
    p = os.path.join(LOGS, name)
    with open(p, encoding="utf-8", errors="replace") as f:
        f.seek(mark)
        return f.read()


def size(name):
    p = os.path.join(LOGS, name)
    return os.path.getsize(p) if os.path.exists(p) else 0


pts = [tuple(int(v) for v in a.split(",")) for a in sys.argv[1:]]
spawner, route = pts[0], pts[1:]
for _ in range(200):
    try:
        if get("/status", 5).get("playing", 0) > 0: break
    except Exception: pass
    time.sleep(3)
time.sleep(25)
print("gamemode:", cmd("gamemode survival"))
dmark, mmark = size("server-debug.log"), size("server-main.log")
k = 0
print("tp:", cmd("tp %d %d %d" % route[k]), route[k])
t0 = time.time(); spawned = None; track = []; gid = None
while time.time() - t0 < 150:
    time.sleep(1.5)
    d = tail("server-debug.log", dmark)
    if spawned is None:
        for line in d.splitlines():
            if "[oversizedhorrors]" in line and ("spawn" in line.lower()):
                spawned = line.split("] ", 2)[-1][:220]
                print("%5.1fs spawner: %s" % (time.time() - t0, spawned))
    cmd("gencount %d %d 90" % (spawner[0], spawner[2]))
    time.sleep(0.6)
    m = tail("server-main.log", mmark); mmark = size("server-main.log")
    gs = re.findall(r"\[gencount-giant\] (\S+) id (\d+) at HUD (-?[\d.]+), (-?[\d.]+), (-?[\d.]+)", m)
    if not gs: continue
    # the giant that started nearest the spawner
    if gid is None:
        best = min(gs, key=lambda g: (float(g[2]) - spawner[0]) ** 2 + (float(g[3]) - spawner[1]) ** 2 + (float(g[4]) - spawner[2]) ** 2)
        gid = best[1]
        print("following", best[0], "id", gid, "first seen at", best[2:], "; all giants:", [(g[0], g[2:]) for g in gs])
    mine = [g for g in gs if g[1] == gid]
    if not mine:
        print("%5.1fs giant %s gone" % (time.time() - t0, gid)); break
    gx, gy, gz = (float(v) for v in mine[-1][2:])
    px, py, pz = route[k]
    dist = math.sqrt((gx - px) ** 2 + (gy - py) ** 2 + (gz - pz) ** 2)
    track.append((round(time.time() - t0, 1), gx, gy, gz, k, round(dist, 1)))
    print("%5.1fs giant at %.1f %.1f %.1f, player at point %d, %.1f apart" % (time.time() - t0, gx, gy, gz, k, dist))
    if dist < 14:
        if k + 1 < len(route):
            k += 1
            print("   move on:", cmd("tp %d %d %d" % route[k]), route[k])
        else:
            print("   end of the route reached with the giant close behind"); break
print("creative:", cmd("gamemode creative"), cmd("tp 1850 140 -1500"))
print("TRACK", json.dumps(track))
print("CHASE DONE")
