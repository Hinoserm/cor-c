#!/usr/bin/env python3
# asamp3.py LOG BIN [N]: collected bytes by the first frame (caller, above, above2) outside the runtime and the collections
import sys, bisect, subprocess, collections, re
log, binp = sys.argv[1], sys.argv[2]; N = int(sys.argv[3]) if len(sys.argv) > 3 else 40
syms = []
for line in subprocess.run(["nm", "-n", binp], capture_output=True, text=True).stdout.splitlines():
    p = line.split()
    if len(p) == 3 and p[1] in "tTwW": syms.append((int(p[0], 16), p[2]))
addrs = [a for a, _ in syms]
def name(a):
    i = bisect.bisect_right(addrs, a) - 1
    return syms[i][1] if i >= 0 else "?"
lib = re.compile(r'^m_(List|HashSet|Dictionary|Runtime|Gc|Enumerable|Stack|Queue|SortedSet|SortedDictionary|String|StringBuilder|Array|Iter|Corsac\$Lang\$Opt\$(AnalysisCache|PipelineAnalyses))')
recs, cur = [], {}
for line in open(log, errors="replace"):
    if not line.startswith("alloc sample "): continue
    k, _, v = line[13:].rstrip("\n").partition(" ")
    if k == "site" and cur: recs.append(cur); cur = {}
    cur[k] = v
if cur: recs.append(cur)
by = collections.Counter(); tot = 0; chain = {}
for r in recs:
    c = int(r.get("collected", 0)); tot += c
    frames = [name(int(r.get(k, 0))) for k in ("caller", "above", "above2")]
    owner = next((f for f in frames if not lib.match(f)), frames[-1])
    key = owner[:120]
    by[key] += c
print("collected sampled", tot)
for k, c in by.most_common(N): print("%6.2f%%  %s" % (100 * c / max(1, tot), k))
