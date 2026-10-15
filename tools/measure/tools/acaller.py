#!/usr/bin/env python3
# acaller.py LOG BIN [N]: collected bytes of slow-path allocations, by the allocating function and its caller
import subprocess,bisect,collections,sys
log,binp=sys.argv[1],sys.argv[2]; N=int(sys.argv[3]) if len(sys.argv)>3 else 30
syms=[]
for line in subprocess.run(["nm","-n",binp],capture_output=True,text=True).stdout.splitlines():
    p=line.split()
    if len(p)==3 and p[1] in "tTwW": syms.append((int(p[0],16),p[2]))
addrs=[a for a,_ in syms]
def name(a):
    i=bisect.bisect_right(addrs,a)-1; return syms[i][1][:105] if i>=0 else "?"
recs=[];cur={}
for line in open(log,errors="replace"):
    if not line.startswith("alloc sample "): continue
    k,_,v=line[13:].rstrip("\n").partition(" ")
    if k=="site" and cur: recs.append(cur); cur={}
    cur[k]=v
recs.append(cur)
tot=sum(int(r.get("collected",0)) for r in recs)
c=collections.Counter(); c2=collections.Counter(); fr=collections.Counter()
for r in recs:
    if "AllocMissed" not in r.get("where",""): continue
    f=name(int(r["caller"])); c[f]+=int(r.get("collected",0)); fr[f]+=int(r.get("freed",0))
    c2[(f,name(int(r["above"])))]+=int(r.get("collected",0))
print("by allocating function (collected%, its own freed/collected)")
for a,v in c.most_common(N): print("%6.2f%%  freed:coll %5.2f  %s"%(100*v/tot, fr[a]/max(1,v), a))
