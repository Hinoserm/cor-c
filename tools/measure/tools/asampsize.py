#!/usr/bin/env python3
# asampsize.py LOG BIN MAXSIZE [N]: collected bytes of sites whose average block is at most MAXSIZE, by the first frame outside the runtime/collections
import sys, bisect, subprocess, collections, re
log, binp, mx = sys.argv[1], sys.argv[2], int(sys.argv[3]); N = int(sys.argv[4]) if len(sys.argv) > 4 else 30
syms = []
for line in subprocess.run(["nm", "-n", binp], capture_output=True, text=True).stdout.splitlines():
    p = line.split()
    if len(p) == 3 and p[1] in "tTwW": syms.append((int(p[0], 16), p[2]))
addrs = [a for a, _ in syms]
def name(a):
    i = bisect.bisect_right(addrs, a) - 1
    return syms[i][1] if i >= 0 else "?"
lib = re.compile(r'^m_(List|HashSet|Dictionary|Runtime|Gc|Enumerable|Stack|Queue|String|StringBuilder|Array|Iter)')
recs, cur = [], {}
for line in open(log, errors="replace"):
    if not line.startswith("alloc sample "): continue
    k, _, v = line[13:].rstrip("\n").partition(" ")
    if k == "site" and cur: recs.append(cur); cur = {}
    cur[k] = v
if cur: recs.append(cur)
by = collections.Counter(); tot = 0; small = 0
for r in recs:
    c = int(r.get("collected", 0)); tot += c
    n = int(r.get("count", 1)) or 1; b = int(r.get("bytes", 0))
    if b / n > mx: continue
    small += c
    frames = [name(int(r.get(k, 0))) for k in ("caller", "above", "above2")]
    owner = next((f for f in frames if not lib.match(f)), frames[-1])
    by[(owner[:100], r.get("where", "?")[:60])] += c
print("small blocks: %.2f%% of collected" % (100 * small / max(1, tot)))
for (o, w), c in by.most_common(N): print("%6.2f%%  %s  [%s]" % (100 * c / max(1, tot), o, w))
