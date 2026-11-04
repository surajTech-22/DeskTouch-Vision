#!/usr/bin/env bash
set -euo pipefail
# A newly installed emulator can briefly show permission/setup UI before
# DeskTouch's activity hierarchy is ready. Retry actual widget assertions;
# never convert a missing screen into a false green test.
adb wait-for-device
ready=0
for attempt in $(seq 1 12); do
  adb shell uiautomator dump /sdcard/desktouch-ui.xml >/dev/null 2>&1 || true
  current="$(adb shell cat /sdcard/desktouch-ui.xml 2>/dev/null || true)"
  if [[ "$current" == *'DeskTouch Vision'* &&
        "$current" == *'Find devices'* &&
        "$current" == *'Take control of active environment screen'* ]]; then
    ready=1
    break
  fi
  if [[ "$attempt" == 5 || "$attempt" == 9 ]]; then
    adb shell am start -n com.desktouch.vision/.MainActivity >/dev/null || true
  fi
  sleep 2
done
if [[ "$ready" != 1 ]]; then
  echo "FAIL: Android app UI did not expose required controls after retries"
  adb shell dumpsys activity activities | grep -E 'ResumedActivity|mFocusedApp|com.desktouch.vision' | head -40 || true
  echo "DEBUG: foreground app UI hierarchy (emulator only, no user data)"
  adb shell cat /sdcard/desktouch-ui.xml 2>/dev/null | head -c 7000 || true
  echo
  adb shell dumpsys window windows | grep -E 'mCurrentFocus|mFocusedApp|Window' | head -25 || true
  adb logcat -d -s AndroidRuntime:E | tail -90 || true
  exit 1
fi
echo "PASS: Android shared environment screen controls visible"
# CI-only grant is equivalent to a user enabling Accessibility on a test
# emulator, never bypasses the in-app Windows-control opt-in.
adb shell settings put secure enabled_accessibility_services \
  com.desktouch.vision/com.desktouch.vision.DeskTouchAccessibilityService
adb shell settings put secure accessibility_enabled 1
sleep 3
adb shell dumpsys accessibility > /tmp/desktouch-accessibility.txt
grep -F 'Enabled services:{{com.desktouch.vision/com.desktouch.vision.DeskTouchAccessibilityService}}' \
  /tmp/desktouch-accessibility.txt
adb shell pidof com.desktouch.vision
echo "PASS: Android 35 Accessibility service enabled and app alive"

# A package REPLACEMENT must preserve the existing Android app-data sandbox,
# stored allow/deny preference and encrypted trusted-device records. The
# platform may require the user to approve Accessibility again; that SYSTEM
# permission is deliberately never granted silently by DeskTouch.
adb shell "run-as com.desktouch.vision sh -c 'mkdir -p files; echo signed-upgrade-retains-appdata > files/upgrade-continuity-test.txt'"
adb install -r AndroidDeskTouch/app/build/outputs/apk/debug/app-debug.apk
adb shell run-as com.desktouch.vision cat files/upgrade-continuity-test.txt |
  tr -d '\r' | grep -F 'signed-upgrade-retains-appdata'
adb shell am start -n com.desktouch.vision/.MainActivity >/dev/null
sleep 3
adb shell pidof com.desktouch.vision
echo "PASS: Android in-place APK upgrade retained app-private data and relaunched permission-health UI"

