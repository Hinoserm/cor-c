#!/usr/bin/env python3
# killsuite.py HOSTDIR: kill every process whose environment CORC= names HOSTDIR,
# plus their parents that are xargs/run.sh, never this process.
import os, sys, signal
host = sys.argv[1]; me = os.getpid(); hit = set()
for p in os.listdir('/proc'):
    if not p.isdigit() or int(p) == me: continue
    try:
        env = open(f'/proc/{p}/environ','rb').read().split(b'\0')
    except Exception: continue
    for e in env:
        if e.startswith(b'CORC=') and host.encode() in e:
            hit.add(int(p)); break
for p in sorted(hit):
    try: os.kill(p, signal.SIGKILL)
    except Exception: pass
print(f'killed {len(hit)}')
