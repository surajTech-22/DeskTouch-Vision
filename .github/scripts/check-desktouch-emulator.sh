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
