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
    bytes([2,0,3,255,0,0])+
    bytes([3])
)
process=subprocess.run(cmd,input=payload,capture_output=True,timeout=25)
stdout=process.stdout.decode(errors="replace")
stderr=process.stderr.decode(errors="replace")
if process.returncode!=0 or "READY" not in stdout or "ACTIVE" not in stdout:
    raise SystemExit(f"FAIL: Android kernel UHID not confirmed: code={process.returncode}, stdout={stdout[:300]}, stderr={stderr[:600]}")
print("PASS: Android shell created real /dev/uhid keyboard and mouse and accepted native HID input")
PY
