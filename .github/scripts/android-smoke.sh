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

echo "== Visiting every tab, three rounds (crash and memory check)"
for round in 1 2 3; do
  for tab in home files settings developer; do
    adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab "$tab" > /dev/null
    sleep 2
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
echo "== Screenshots with sample data (debug build: same key, installs as an update)"
API=$(adb shell getprop ro.build.version.sdk | tr -d '\r')
mkdir -p screens
DEBUG_APK=$(ls apk-debug/*.apk 2>/dev/null | head -1)
if [ -n "$DEBUG_APK" ]; then
  adb install -r "$DEBUG_APK" | tail -1
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --ez demo true > /dev/null
  sleep 6
  for tab in home files settings developer; do
    adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab "$tab" > /dev/null
    sleep 3
    adb exec-out screencap -p > "screens/android-$API-$tab.png"
  done
  # Home scrolled down (recent files and activity), and the gallery view.
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab home > /dev/null
  sleep 2
  adb shell input swipe 500 1600 500 500 300
  sleep 2
  adb exec-out screencap -p > "screens/android-$API-home-scrolled.png"
  adb logcat -d -b crash | grep -q "dev.midfielder" && { echo "::error::debug build crashed"; adb logcat -d -b crash; exit 1; }
fi
echo "Smoke test passed"
