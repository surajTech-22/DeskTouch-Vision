#!/usr/bin/env bash
set -euo pipefail
# Exercise the ACTUAL Android IME and Accessibility services in their normal
# application process. ADB only enables system services on this isolated CI
# emulator; no bypass or test receiver is included in the release build.
ime="com.desktouch.vision/.DeskTouchInputMethod"
adb wait-for-device
adb shell ime enable "$ime"
adb shell ime set "$ime"
adb shell settings get secure default_input_method | tr -d '\r' | grep -F 'DeskTouchInputMethod'
adb shell dumpsys accessibility | grep -F 'DeskTouchAccessibilityService'

: > android-input-emulator.txt
failed=0
for probe in status keyboard click drag scroll; do
  # An actual Android tap establishes input focus; a programmatic request
  # alone is not equivalent to a user focusing a text field.
  if [[ "$probe" == keyboard ]]; then
    adb shell input tap 240 110
    sleep 1
  fi
  label="$(echo "$probe" | tr '[:lower:]' '[:upper:]')"
  adb logcat -c
  adb shell am start -n com.desktouch.vision/.InputProbeActivity \
    --es probe "$probe"
  result=timeout
  for attempt in $(seq 1 30); do
    logs="$(adb logcat -d -s DeskTouchInputProbe:V 2>/dev/null || true)"
    if echo "$logs" | grep -Fq "RESULT_${label}_PASS"; then
      result=pass
      echo "PASS: $probe" | tee -a android-input-emulator.txt
      break
    fi
    if echo "$logs" | grep -Fq "RESULT_${label}_FAIL"; then
      result=fail
      echo "FAIL: $probe" | tee -a android-input-emulator.txt
      echo "$logs" | grep -F "RESULT_${label}_FAIL" | tail -5
      break
    fi
    sleep 1
  done
  if [[ "$result" != pass ]]; then
    echo "FAIL: Android $probe input behavior was not verified" | tee -a android-input-emulator.txt
    adb shell dumpsys accessibility | grep -E 'Enabled services|Bound services|DeskTouch' | head -30 || true
    adb shell dumpsys input_method | grep -E 'mCurId|mCurMethodId|mShowRequested|mInputShown|mServedView|DeskTouchInputMethod|mCurFocusedWindow' | head -45 || true
    adb logcat -d -s DeskTouchInputProbe:V | grep -E 'KEYBOARD_FOCUS|KEYBOARD_AFTER_FOCUS|RESULT_' | tail -45 || true
    adb logcat -d -s AndroidRuntime:E DeskTouchInputProbe:V | tail -80 || true
    failed=$((failed+1))
  fi
done
if [[ "$failed" -ne 0 ]]; then
  echo "FAIL: $failed Android real-input probes did not pass"
  exit 1
fi
echo "PASS: Android real editor input and click, drag, wheel events in emulator"
