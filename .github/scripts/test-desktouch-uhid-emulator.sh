#!/usr/bin/env bash
set -euo pipefail
# Run the actual signed/installed application's UHID bridge as Android's ADB
# shell user, exactly as the Windows no-Bluetooth mode launches it. Requires
# a working /dev/uhid on this emulator; never grant Android app root/system.
python3 - <<'PY'
import subprocess
pkgs=subprocess.run(["adb","shell","pm","path","com.desktouch.vision"],
    text=True,capture_output=True,check=True).stdout
apk=next((l[len("package:"):].strip()
    for l in pkgs.splitlines()
    if l.startswith("package:") and l.strip().endswith("/base.apk")), None)
if not apk:
    raise SystemExit("FAIL: installed DeskTouch Android base.apk missing")
if not apk.startswith("/data/app/"):
    raise SystemExit("FAIL: unexpected app path")
cmd=["adb","shell","-T",
    f"CLASSPATH={apk} exec app_process / com.desktouch.vision.UhidShellBridge"]
# First a standard native HID keyboard press+release, then a mouse report
# for which the shell bridge must send an ACTIVE acknowledgement.
payload=(
    bytes([1,2,0,4,0,0,0,0,0])+
    bytes([1,0,0,0,0,0,0,0,0])+
    bytes([2,0,3,255,0,0])
)
# Keep the bridge alive while querying Android InputReader; successful Os.write
# alone is not sufficient evidence that native keyboard and mouse registered.
process=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.PIPE,
    stderr=subprocess.PIPE)
try:
    process.stdin.write(payload)
    process.stdin.flush()
    import time
    observed=False
    for attempt in range(12):
        time.sleep(.5)
        result=subprocess.run(["adb","shell","dumpsys","input"],
            capture_output=True,text=True,timeout=12)
        if result.returncode==0 and (
            "DeskTouch Vision Keyboard" in result.stdout and
            "DeskTouch Vision Mouse" in result.stdout):
            observed=True
            break
    process.stdin.write(bytes([3]))
    process.stdin.flush()
    process.stdin.close()
    process.stdin=None
    stdout,stderr=process.communicate(timeout=15)
    stdout=stdout.decode(errors="replace")
    stderr=stderr.decode(errors="replace")
    if process.returncode!=0 or "READY" not in stdout or "ACTIVE" not in stdout:
        raise SystemExit(f"FAIL: Android UHID not confirmed: code={process.returncode}, stdout={stdout[:300]}, stderr={stderr[:600]}")
    if not observed:
        raise SystemExit("FAIL: Android InputReader did not register both DeskTouch native HID devices")
    print("PASS: Android InputReader registered native keyboard AND mouse; actual shell reports accepted")
finally:
    if process.poll() is None:
        process.kill()
        process.wait(timeout=5)
PY
