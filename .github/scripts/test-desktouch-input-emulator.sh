#!/usr/bin/env bash
set -euo pipefail

# This is an emulator-only integration test for the real Android IME and
# Accessibility APIs. The production APK cannot silently enable either.
apk="AndroidDeskTouch/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"
test -s "$apk"
adb install -r "$apk"
adb wait-for-device

ime="com.desktouch.vision/.DeskTouchInputMethod"
adb shell ime enable "$ime"
adb shell ime set "$ime"
adb shell settings get secure default_input_method | tr -d '\r' | grep -F 'DeskTouchInputMethod'
adb shell dumpsys accessibility | grep -F 'DeskTouchAccessibilityService'

echo "RUNNING: Android real IME input and Accessibility mouse acceptance"
set +e
adb shell am instrument -w -r \
  com.desktouch.vision.test/androidx.test.runner.AndroidJUnitRunner \
  > android-input-instrumentation.txt 2>&1
status=$?
set -e
cat android-input-instrumentation.txt
if [[ "$status" != 0 ]] || ! grep -Eq '^OK \(3 tests?\)' android-input-instrumentation.txt; then
  echo "FAIL: Android input behavior did not satisfy all three runtime tests"
  adb logcat -d -s AndroidRuntime:E | tail -90 || true
  exit 1
fi
echo "PASS: Real Android editor typing/caret, Accessibility click, drag and scroll"
