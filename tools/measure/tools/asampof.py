#!/usr/bin/env python3
# asampof.py LOG BIN PATTERN [N]: collected bytes of samples with a frame matching PATTERN, by the frame above it
import sys, bisect, subprocess, collections, re
log, binp, pat = sys.argv[1], sys.argv[2], re.compile(sys.argv[3]); N = int(sys.argv[4]) if len(sys.argv) > 4 else 30
syms = []
for line in subprocess.run(["nm", "-n", binp], capture_output=True, text=True).stdout.splitlines():
    p = line.split()
    if len(p) == 3 and p[1] in "tTwW": syms.append((int(p[0], 16), p[2]))
addrs = [a for a, _ in syms]
def name(a):
    i = bisect.bisect_right(addrs, a) - 1
    return syms[i][1] if i >= 0 else "?"
recs, cur = [], {}
for line in open(log, errors="replace"):
    if not line.startswith("alloc sample "): continue
    k, _, v = line[13:].rstrip("\n").partition(" ")
    if k == "site" and cur: recs.append(cur); cur = {}
    cur[k] = v
if cur: recs.append(cur)
by = collections.Counter(); tot = 0; hit = 0
for r in recs:
    c = int(r.get("collected", 0)); tot += c
    frames = [name(int(r.get(k, 0))) for k in ("caller", "above", "above2")]
    for i, f in enumerate(frames):
        if pat.search(f):
            hit += c
            by[(frames[i + 1] if i + 1 < len(frames) else "(deeper)")[:110]] += c
            break
print("matching %.2f%% of collected" % (100 * hit / max(1, tot)))
for k, c in by.most_common(N): print("%6.2f%%  %s" % (100 * c / max(1, tot), k))
