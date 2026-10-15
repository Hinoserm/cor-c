#!/usr/bin/env python3
# deadcensus.py LOG BIN [N]: the last dead census (--gc-dead-census) by type, MB and blocks
import re,bisect,subprocess,sys
log,binp=sys.argv[1],sys.argv[2]; N=int(sys.argv[3]) if len(sys.argv)>3 else 30
syms=[]
for l in subprocess.run(['nm','-n',binp],capture_output=True,text=True).stdout.splitlines():
    p=l.split()
    if len(p)==3: syms.append((int(p[0],16),p[2]))
keys=[a for a,_ in syms]
def name(v):
    if v>=1<<30: return f"untyped size {v-(1<<30)}"
    i=bisect.bisect_right(keys,v)-1
    return syms[i][1] if i>=0 else hex(v)
text=open(log,errors='replace').read()
last=text.rfind('gc dead census: everything swept so far')
rows=[];b=None
for l in text[last:].split('\n')[1:]:
    m=re.match(r'gc dead census: bytes (\d+)',l)
    if m: b=[int(m.group(1)),0,0]; rows.append(b); continue
    m=re.match(r'gc dead census:   blocks (\d+)',l)
    if m and b: b[1]=int(m.group(1)); continue
    m=re.match(r'gc dead census:   key (\d+)',l)
    if m and b: b[2]=int(m.group(1)); continue
    if not l.startswith('gc dead census'): break
tot=sum(r[0] for r in rows)
print("total dead MB %.1f"%(tot/1e6))
for r in sorted(rows,reverse=True)[:N]:
    print("%7.1f MB %5.1f%% %9d  %s"%(r[0]/1e6,100*r[0]/tot,r[1],name(r[2])[:90]))
