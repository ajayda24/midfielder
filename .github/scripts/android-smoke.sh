#!/usr/bin/env bash
# Installs the release APK on the emulator, launches it, and fails on install errors or crashes.
# The output (exact INSTALL_FAILED_* reason, crash stack traces) is the point of this job.
set -u
APK=$(ls apk/*.apk | head -1)
echo "== Installing $APK"
adb install -r "$APK" > install.txt 2>&1
cat install.txt
if ! grep -q "Success" install.txt; then
  echo "::error::APK install failed: $(tail -1 install.txt)"
  exit 1
fi

echo "== Launching"
adb logcat -c
adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity
sleep 15

echo "== Crash buffer"
adb logcat -d -b crash > crash.txt
cat crash.txt
if grep -q "dev.midfielder" crash.txt; then
  echo "::error::Midfielder crashed after launch"
  exit 1
fi
if ! adb shell pidof dev.midfielder > /dev/null; then
  echo "::error::Midfielder process is not running after launch"
  adb logcat -d | grep -i midfielder | tail -100
  exit 1
fi

echo "== Foreground service"
adb shell dumpsys activity services dev.midfielder | head -30
echo "== Memory"
adb shell dumpsys meminfo dev.midfielder | head -25
echo "Smoke test passed"
