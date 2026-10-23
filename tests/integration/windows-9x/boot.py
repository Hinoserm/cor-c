#!/usr/bin/env python3
"""boot.py MACHINE SECONDS [REQUEST...]: boot the Windows 9x machine in the folder MACHINE headless for SECONDS, take the guest's
frame through the automation socket, stop it. Qt's offscreen platform (no X server), no sound, and
every process started here stopped here by its own pid."""
import os, signal, subprocess, sys, time, json, socket, re
case = os.path.abspath(sys.argv[1])
seconds = int(sys.argv[2]) if len(sys.argv) > 2 else 180
cfg = os.path.join(case, "86box.cfg")
text = open(cfg).read()
text = re.sub(r"(?m)^fast_forward = .*\n", "", text)
text = re.sub(r"(?m)^\[General\]\n", "[General]\nfast_forward = 1\n", text, count=1)
open(cfg, "w").write(text)
runtime = os.path.join(case, "rt"); os.makedirs(runtime, exist_ok=True)
env = {k: v for k, v in os.environ.items() if k not in ("WAYLAND_DISPLAY", "DISPLAY", "PULSE_SERVER", "PIPEWIRE_REMOTE")}
env.update(QT_QPA_PLATFORM="offscreen", XDG_RUNTIME_DIR=runtime, PULSE_SERVER="unix:/nonexistent/pulse",
           ALSOFT_DRIVERS="null", SDL_AUDIODRIVER="dummy", AUDIODEV="null")
sock = os.path.join(case, "a.sock")
if os.path.exists(sock): os.unlink(sock)
box = subprocess.Popen([os.path.join(case, "86Box"), "-P", case, "-C", cfg, "-V", "w98dll", "--automation", "unix:" + sock],
                       stdout=open(os.path.join(case, "86box.out"), "w"), stderr=subprocess.STDOUT, env=env, cwd=case,
                       start_new_session=True)
open(os.path.join(case, "pid"), "w").write(str(box.pid))
def request(line):
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.settimeout(30); s.connect(sock)
    s.sendall((line + "\n").encode()); buf = b""
    while not buf.endswith(b"\n"):
        c = s.recv(65536)
        if not c: break
        buf += c
    s.close(); return json.loads(buf)
try:
    end = time.time() + seconds
    while time.time() < end and box.poll() is None: time.sleep(2)
    print("running" if box.poll() is None else "exited %s" % box.returncode)
    # Then each further argument, an automation request ("sleep N" waits), and a screenshot last.
    for line in sys.argv[3:]:
        if line.startswith("sleep "): time.sleep(float(line.split()[1])); continue
        try: print(line, "->", request(line))
        except Exception as e: print(line, "failed:", e)
    try: print(request("screenshot " + os.path.join(case, "screen.png")))
    except Exception as e: print("no screenshot:", e)
finally:
    if box.poll() is None:
        os.killpg(box.pid, signal.SIGTERM); time.sleep(2)
        if box.poll() is None: os.killpg(box.pid, signal.SIGKILL)
    box.wait()
    print("stopped", box.pid)
