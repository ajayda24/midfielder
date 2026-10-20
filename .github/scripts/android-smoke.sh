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

echo "== Granting READ_LOGS (like the one-time ADB setup users do) and launching"
adb shell pm grant dev.midfielder android.permission.READ_LOGS || echo "grant not allowed on this image"
adb logcat -c
adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity
sleep 8
# Leave and reopen a few times: every resume (re)starts the log reader, which once crashed the app.
for i in 1 2 3; do
  adb shell input keyevent KEYCODE_HOME
  sleep 2
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity
  sleep 4
done
sleep 5

echo "== Visiting every tab (screenshots for review), three rounds"
API=$(adb shell getprop ro.build.version.sdk | tr -d '\r')
mkdir -p screens
for round in 1 2 3; do
  for tab in home files settings developer; do
    adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab "$tab" > /dev/null
    sleep 3
    if [ "$round" = 1 ]; then adb exec-out screencap -p > "screens/android-$API-$tab.png"; fi
  done
  echo "-- after round $round"
  adb shell dumpsys meminfo dev.midfielder | grep -E "TOTAL PSS|TOTAL:|Views:|Activities:" | head -4
done

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
