#!/usr/bin/env python3
# census.py LOG BIN [N]: the whole collection with the most live bytes, its top types
import subprocess,sys
log=open(sys.argv[1],errors='replace').read().split('\n'); binp=sys.argv[2]; N=int(sys.argv[3]) if len(sys.argv)>3 else 25
syms={}
for line in subprocess.run(["nm",binp],capture_output=True,text=True).stdout.splitlines():
    p=line.split()
    if len(p)==3: syms[int(p[0],16)]=p[2]
cycles=[];cur=None
for l in log:
    if l.startswith('gc census: whole cycle, live bytes'): cur={'live':int(l.split()[-1]),'rows':[]}; cycles.append(cur)
    elif cur is not None and l.startswith('gc census: bytes '): cur['rows'].append([int(l.split()[-1]),0,0])
    elif cur is not None and l.startswith('gc census:   blocks '): cur['rows'][-1][1]=int(l.split()[-1])
    elif cur is not None and l.startswith('gc census:   key '): cur['rows'][-1][2]=int(l.split()[-1])
big=max(cycles,key=lambda c:c['live'])
print("cycles",len(cycles),"max live",big['live']>>20,"MB")
for b,n,k in sorted(big['rows'],reverse=True)[:N]:
    name=syms.get(k) or syms.get(k-48) or hex(k)
    print("%8.1f MB %8d  %s"%(b/1048576,n,name[:90]))
