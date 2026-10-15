#!/usr/bin/env python3
# parents.py LOG BIN [N]: the last parent census -- what live objects hold, by (child type, holder type)
import subprocess, sys
log = open(sys.argv[1], errors='replace').read().split('\n'); binp = sys.argv[2]; N = int(sys.argv[3]) if len(sys.argv) > 3 else 40
syms = {}
for line in subprocess.run(["nm", binp], capture_output=True, text=True).stdout.splitlines():
    p = line.split()
    if len(p) == 3: syms[int(p[0], 16)] = p[2]
def nm(k):
    if k >= 0x40000000 and k < 0x50000000: return "untyped %d" % (k - 0x40000000)
    return (syms.get(k) or syms.get(k - 48) or hex(k))[:70]
tables = []; cur = None
for l in log:
    if l.startswith('gc parent census: what live'): cur = []; tables.append(cur)
    elif cur is not None and l.startswith('gc parent census: bytes '): cur.append([int(l.split()[-1]), 0, 0, 0])
    elif cur is not None and l.startswith('gc parent census:   blocks '): cur[-1][1] = int(l.split()[-1])
    elif cur is not None and l.startswith('gc parent census:   child '): cur[-1][2] = int(l.split()[-1])
    elif cur is not None and l.startswith('gc parent census:   parent '): cur[-1][3] = int(l.split()[-1])
best = max(tables, key=lambda t: sum(r[0] for r in t))
print(len(tables), "tables; largest shown", sum(r[0] for r in best) >> 20, "MB")
for b, n, c, p in best[:N]:
    print("%7.1f MB %8d  %-45s <- %s" % (b / 1048576, n, nm(c)[:45], nm(p)))
