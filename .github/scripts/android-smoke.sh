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
  # Without READ_LOGS, Android's log-access prompt doesn't cover the screenshots.
  adb shell pm revoke dev.midfielder android.permission.READ_LOGS || true
  adb shell am force-stop dev.midfielder
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --ez demo true > /dev/null
  sleep 6
  for tab in home files settings developer; do
    adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab "$tab" > /dev/null
    sleep 3
    adb exec-out screencap -p > "screens/android-$API-$tab.png"
  done
  # Home scrolled down (recent files and activity).
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab home > /dev/null
  sleep 2
  SIZE=$(adb shell wm size | tr -d '\r' | awk '{print $NF}')
  W=${SIZE%x*}; H=${SIZE#*x}
  adb shell input swipe $((W / 2)) $((H * 3 / 4)) $((W / 2)) $((H / 5)) 400
  sleep 2
  adb exec-out screencap -p > "screens/android-$API-home-scrolled.png"
  adb logcat -d -b crash | grep -q "dev.midfielder" && { echo "::error::debug build crashed"; adb logcat -d -b crash; exit 1; }

  echo "== Every feature on: Settings top to bottom, Home"
  for p in READ_PHONE_STATE READ_CALL_LOG ANSWER_PHONE_CALLS READ_CONTACTS SEND_SMS BLUETOOTH_ADVERTISE; do
    adb shell pm grant dev.midfielder android.permission.$p 2>/dev/null || true
  done
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab settings --ez demo_features true > /dev/null
  sleep 3
  adb exec-out screencap -p > "screens/android-$API-settings-1.png"

  # Flip a feature switch and check the screen doesn't rebuild: the same cards are there right after the tap
  # (a rebuild used to fade the whole Features section out and in), and the switch really changed.
  adb shell uiautomator dump /sdcard/ui.xml > /dev/null && adb shell cat /sdcard/ui.xml > ui-before.xml
  NODE=$(grep -o 'content-desc="Notifications on PC"[^>]*' ui-before.xml | head -1)
  if [ -n "$NODE" ]; then
    B=$(echo "$NODE" | grep -o 'bounds="[^"]*"' | grep -o '[0-9]\+' | tr '\n' ' ')
    set -- $B
    X=$(( ($1 + $3) / 2 )); Y=$(( ($2 + $4) / 2 ))
    BEFORE=$(echo "$NODE" | grep -o 'checked="[a-z]*"')
    adb shell input tap $X $Y
    sleep 0.15
    adb exec-out screencap -p > "screens/android-$API-settings-toggle.png"
    adb shell uiautomator dump /sdcard/ui.xml > /dev/null && adb shell cat /sdcard/ui.xml > ui-after.xml
    AFTER=$(grep -o 'content-desc="Notifications on PC"[^>]*' ui-after.xml | head -1 | grep -o 'checked="[a-z]*"')
    CARDS_BEFORE=$(grep -o 'text="[^"]*"' ui-before.xml | grep -c -E 'Notifications on PC|Battery sharing|Find my phone|Music control')
    CARDS_AFTER=$(grep -o 'text="[^"]*"' ui-after.xml | grep -c -E 'Notifications on PC|Battery sharing|Find my phone|Music control')
    echo "switch: $BEFORE -> $AFTER; cards on screen: $CARDS_BEFORE -> $CARDS_AFTER"
    [ "$BEFORE" != "$AFTER" ] || { echo "::error::tapping the feature switch didn't change it"; exit 1; }
    [ "$CARDS_BEFORE" = "$CARDS_AFTER" ] || { echo "::error::the feature cards changed after one toggle (rebuild/blink)"; exit 1; }
    # Back on without the notification-access screen: set it again the demo way (rebuilds Settings at the top).
    adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab settings --ez demo_features true > /dev/null
    sleep 2
  else
    echo "::warning::couldn't find the Notifications switch for the toggle check"
  fi

  for n in 2 3 4 5 6 7 8; do
    adb shell input swipe $((W / 2)) $((H * 3 / 4)) $((W / 2)) $((H / 4)) 500
    sleep 1.5
    adb exec-out screencap -p > "screens/android-$API-settings-$n.png"
  done
  adb shell am start -W -n dev.midfielder/dev.midfielder.android.MainActivity --es tab home > /dev/null
  sleep 2
  adb exec-out screencap -p > "screens/android-$API-home-features.png"
  adb logcat -d -b crash | grep -q "dev.midfielder" && { echo "::error::debug build crashed with features on"; adb logcat -d -b crash; exit 1; }
fi
echo "Smoke test passed"
