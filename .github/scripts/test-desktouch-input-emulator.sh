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
for probe in status click drag scroll; do
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

# Android's InputConnection is ordinarily a Binder bridge from an IME process
# into a DIFFERENT application process. Test that real boundary explicitly.
adb shell am start -n com.desktouch.vision/.ExternalEditorProbeActivity >/dev/null
sleep 2
adb shell input tap 225 110
sleep 1
adb shell dumpsys input_method | grep -E 'mCurId|mServedView|mServedInputConnection' | head -12 || true

editor_send() {
  local label="$1"; shift
  local expected="$1"; shift
  adb logcat -c
  adb shell am broadcast \
    -a com.desktouch.vision.DEBUG_SEND_INPUT \
    -n com.desktouch.vision/.DebugRemoteInputReceiver "$@" >/dev/null
  local result=timeout
  for attempt in $(seq 1 20); do
    local trace
    trace="$(adb logcat -d -s DeskTouchExternalEditor:I 2>/dev/null || true)"
    if echo "$trace" | grep -Fq "EDITOR_VALUE=${expected} CARET="; then
      if echo "$trace" | grep -Fq "IME_REQUEST command=" && \
         echo "$trace" | grep -Fq "accepted=true"; then
        result=pass
        echo "PASS: Android cross-process keyboard $label" |
          tee -a android-input-emulator.txt
        break
      fi
    fi
    sleep 1
  done
  if [[ "$result" != pass ]]; then
    echo "FAIL: Android cross-process keyboard $label; expected=$expected" |
      tee -a android-input-emulator.txt
    adb logcat -d -s DeskTouchExternalEditor:I AndroidRuntime:E | tail -80 || true
    adb shell dumpsys input_method | grep -E \
      'mCurId|mInputShown|mServedView|mServedInputConnection|mInputBinding' |
      head -35 || true
    failed=$((failed+1))
  fi
}
editor_send "text" "hello" --es command text --es value hello
editor_send "Backspace" "hell" --es command key --ei vk 8
editor_send "retype" "hello" --es command text --es value o
adb logcat -c
adb shell am broadcast -a com.desktouch.vision.DEBUG_SEND_INPUT \
  -n com.desktouch.vision/.DebugRemoteInputReceiver \
  --es command key --ei vk 37 >/dev/null
sleep 1
adb logcat -d -s DeskTouchExternalEditor:I |
  grep -F 'IME_REQUEST command=key accepted=true' || failed=$((failed+1))
# Left-arrow moves the caret without changing the text; verify insertion.
editor_send "insert-at-caret" "hell!o" --es command text --es value '!'
# Next use the SAME Android-side handlers as actual authenticated reverse
# packets from Windows, rather than calling the IME endpoint directly.
editor_send "DeskTouch reverse-text" "hell!zo" \
  --es command reverse-text --es value z
editor_send "DeskTouch reverse-key" "hell!o" \
  --es command reverse-key --ei vk 8

if [[ "$failed" -ne 0 ]]; then
  echo "FAIL: $failed Android real-input probes did not pass"
  exit 1
fi
echo "PASS: Android real editor input and click, drag, wheel events in emulator"
