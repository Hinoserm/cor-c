import sys, collections
# spill-blocks.py MAP ERR [N]: a --count-spills-map run's spill-slot traffic by function --
# each counted block's runs (ERR's "spills: block K" / "spills: runs R" pairs) times its
# reads and writes (MAP: number, function, block, reads, writes); the top N functions.
blocks = {}
for line in open(sys.argv[1]):
    k, fn, b, r, w = line.rstrip('\n').split('\t')
    blocks[int(k)] = (fn, int(b), int(r), int(w))
fr, fw, fb = collections.Counter(), collections.Counter(), collections.defaultdict(list)
k = None
for line in open(sys.argv[2]):
    if line.startswith('spills: block '): k = int(line.split()[2])
    elif line.startswith('spills: runs ') and k is not None:
        runs = int(line.split()[2]); fn, b, r, w = blocks[k]
        fr[fn] += runs * r; fw[fn] += runs * w; fb[fn].append((runs * (r + w), b, runs, r, w)); k = None
tr, tw = sum(fr.values()), sum(fw.values())
print(f'total reads {tr} writes {tw}')
for fn, _ in sorted(fr.items(), key=lambda x: -(x[1] + fw[x[0]]))[:int(sys.argv[3]) if len(sys.argv) > 3 else 12]:
    top = ' '.join(f'b{b}:{runs}x({r}r{w}w)' for _, b, runs, r, w in sorted(fb[fn], reverse=True)[:4])
    print(f'{fr[fn]:>12} {fw[fn]:>12}  {fn}  [{top}]')
