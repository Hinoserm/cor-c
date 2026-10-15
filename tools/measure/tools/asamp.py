#!/usr/bin/env python3
# asamp.py LOG BIN [N]: rank --alloc-sample records by bytes the collector took, by site and callers
import sys, bisect, subprocess, collections
log, binp = sys.argv[1], sys.argv[2]; N = int(sys.argv[3]) if len(sys.argv) > 3 else 40
syms = []
for line in subprocess.run(["nm", "-n", binp], capture_output=True, text=True).stdout.splitlines():
    p = line.split()
    if len(p) == 3 and p[1] in "tTwW": syms.append((int(p[0], 16), p[2]))
addrs = [a for a, _ in syms]
def name(a):
    i = bisect.bisect_right(addrs, a) - 1
    n = syms[i][1] if i >= 0 else "?"
    return n[:110]
recs, cur = [], {}
for line in open(log, errors="replace"):
    if not line.startswith("alloc sample "): continue
    k, _, v = line[13:].rstrip("\n").partition(" ")
    if k == "site" and cur: recs.append(cur); cur = {}
    cur[k] = v
if cur: recs.append(cur)
by = collections.Counter(); byw = collections.Counter(); tot = collections.Counter()
for r in recs:
    c = int(r.get("collected", 0)); f = int(r.get("freed", 0))
    tot["collected"] += c; tot["freed"] += f
    w = r.get("where", "?")
    key = (w, name(int(r.get("caller", 0))), name(int(r.get("above", 0))))
    by[key] += c; byw[w] += c
print("sampled collected", tot["collected"], "freed", tot["freed"], "collector share %.1f%%" % (100 * tot["collected"] / max(1, tot["collected"] + tot["freed"])))
print("--- by site")
for w, c in byw.most_common(N): print("%6.2f%%  %s" % (100 * c / max(1, tot["collected"]), w))
print("--- by site, caller, above")
for (w, a, b), c in by.most_common(N): print("%6.2f%%  %s\n         <- %s\n         <- %s" % (100 * c / max(1, tot["collected"]), w, a, b))
