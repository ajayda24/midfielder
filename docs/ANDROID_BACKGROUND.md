# Android background limits and how Midfielder handles them

## The problem

| Direction | Android rule | Consequence |
|---|---|---|
| PC → phone clipboard | Apps may **write** the clipboard from the background | Works with no setup |
| Phone → PC clipboard | Since Android 10, only the focused app or the default keyboard may **read** the clipboard, and background apps don't get change callbacks | Needs a trigger + a moment of focus |
| Staying connected | Background work is killed or deferred (Doze, App Standby, OEM battery managers) | Needs a foreground service + battery exemption |

## Staying alive, cheaply

* `SyncService` is a foreground service of type `connectedDevice` with a minimum-importance notification
  (no sound, no status-bar icon). This type is allowed to start at boot and has no runtime cap on
  Android 14/15.
* The user grants a **battery-optimization exemption** once. Without it, Doze cuts the app's network
  access in deep sleep. Xiaomi, Samsung, OnePlus and others also have their own "auto-start" or
  "unrestricted" switches. The setup checklist points there.
* Inside the service nothing polls (see [ARCHITECTURE.md](ARCHITECTURE.md#efficiency-and-robustness-rules)).
  In deep sleep the phone sends nothing at all. It wakes when the PC delivers data or knocks, when Wi-Fi
  changes, or when the screen turns on.

## Detecting a copy (phone → PC)

Each layer only **detects** that you copied something. Reading always happens through
`ClipboardReaderActivity`: an invisible 1×1 activity that takes focus for a moment, reads, and closes. It
is excluded from Recents and has no animation. Starting it from the background requires *Display over other
apps* (or an enabled accessibility service).

| Layer | How it detects | Setup | Battery cost |
|---|---|---|---|
| Clipboard listener | `OnPrimaryClipChangedListener` | none | zero; fires only while our UI is in front (on some OEM builds, always) |
| **Logcat** (recommended) | When the clipboard changes, Android tries to notify our listener, refuses because we're in the background, and logs `ClipboardService: Denying clipboard access to dev.midfielder…`. We read that line. | `adb shell pm grant dev.midfielder android.permission.READ_LOGS`, once. On Android 13+ also tap **Allow** on "access all device logs?" when Midfielder is open (again after a reboot or app update) | small: one filtered `logcat` reader, kept running once approved; it never wakes the CPU itself |
| Accessibility | Taps on "Copy" and "Copied" toasts (localized via `android.R.string.copy`) | enable in Accessibility settings | very small; click and notification events only, no window content |
| Manual | Quick Settings tile, notification button, Share → Send to PC | none | zero |

Opening the Midfielder app also sends whatever is on the clipboard at that moment (duplicates are ignored).

**Android 13+ log approval:** every time a log reader starts, Android asks the user to approve access
to other apps' logs, and only while the app is on screen. A reader started in the background silently
sees only its own lines. Midfielder therefore starts the reader while its screen is open, keeps the
approved reader running, and shows "Copy detection: active" in Recent activity once it is approved.

**Known side effect:** the focus trick can briefly hide the keyboard in the app you copied from. A
"pasted from Midfielder" toast may also appear on Android 12+.

## On-device test checklist

Behavior varies by Android version and OEM, so run this on your phone before relying on automatic phone → PC
sync. Each row records which layer worked.

| # | Test | Expected |
|---|---|---|
| 1 | Pair via QR; reboot phone and PC | reconnects by itself within seconds of unlocking |
| 2 | Copy text on PC, unlock phone, paste | text is there (also after 1 h with screen off) |
| 3 | Screenshot or copy an image on PC, paste on phone | image pastes in a chat app |
| 4 | Copy text in Chrome on the phone (logcat layer) | appears on PC in < 1 s, no visible flash |
| 5 | Same with the accessibility layer only | appears on PC |
| 6 | Copy a password from your password manager | syncs, absent from Win+V history |
| 7 | Share a 1 GB video to PC; toggle Wi-Fi off and on mid-way | resumes and completes; file hash matches |
| 8 | Send to ▸ Midfielder on a PC folder of photos | all arrive in Download/Midfielder |
| 9 | Leave everything idle overnight | `adb shell dumpsys batterystats dev.midfielder` shows ≤ 1 %/day |
| 10 | 24 h soak: copy every 30 s plus periodic transfers | heap (Android Studio profiler) and private bytes (`dotnet-counters`) flat |
