# Installing Midfielder

You need: a Windows 10/11 PC and an Android 10+ phone **on the same Wi-Fi network**. Setup takes about
10 minutes, once.

## 1. Download

1. Open the repository on GitHub → **Actions** tab → click the latest run of **CI** with a green check ✅.
2. Scroll to **Artifacts** and download:
   * `midfielder-windows`: contains `Midfielder.exe`
   * `midfielder-android`: contains the APK

   Both download as `.zip` files. Unzip them. (You must be signed in to GitHub to download artifacts.)

## 2. Windows laptop

1. Create a folder that will stay put, e.g. `C:\Users\<you>\AppData\Local\Programs\Midfielder`, and move
   `Midfielder.exe` there. Midfielder sets itself to start with Windows from that location, so don't run
   it from Downloads.
2. Double-click `Midfielder.exe`.
   * **"Windows protected your PC"** (SmartScreen): click **More info → Run anyway**. The app isn't
     code-signed yet.
   * **Windows Firewall** asks about network access: tick **Private networks** and click **Allow**. Without
     this, the phone can't connect.
3. A Midfielder icon appears in the tray (bottom right; it may be under the **^** arrow). A window with a
   **QR code** opens. Keep it open for the next step. (Later: right-click the tray icon → **Pair a phone…**.)

No .NET or anything else to install: the exe is self-contained.

## 3. Android phone

1. Copy the APK to the phone (USB cable, Google Drive, email to yourself, or Nearby Share).
2. Tap the APK. Android asks to allow installing from that app (Files, Drive, Gmail…): **Settings → Allow
   from this source**, go back, then **Install**. If Play Protect warns about an unknown app, choose
   **Install anyway**.
3. Open **Midfielder** → tap **Pair with PC (scan QR code)** → allow the camera → point it at the QR code on
   the laptop.
4. Check that the fingerprint shown on the phone matches the one under the QR code on the PC, then tap
   **Pair**.

You should see *"Connected to <your PC>"* on the phone, and the tray menu shows the phone.

## 4. One-time phone setup (makes it automatic)

On the Midfielder screen, under **Make it automatic**, tap **Fix** on each item:

| Item | Why |
|---|---|
| Show transfer notifications | progress and "received" alerts |
| Stay connected in the background | stops Android from cutting the connection while the phone sleeps |
| Display over other apps | lets Midfielder read what you copy (Android hides the clipboard from background apps) |

Then choose **one** way to detect copies:

**Option A (recommended, most seamless):** one command from the laptop.

1. Download Google's **SDK Platform-Tools for Windows**
   (https://developer.android.com/tools/releases/platform-tools) and unzip it.
2. On the phone, enable **Developer options**: Settings → About phone → tap **Build number** 7 times.
   Then Settings → System → Developer options → turn on **USB debugging**.
3. Connect the phone by USB and accept the **Allow USB debugging?** prompt on the phone.
4. In the unzipped `platform-tools` folder, click the address bar, type `cmd`, press Enter, and run:
   ```
   adb devices
   adb shell pm grant dev.midfielder android.permission.READ_LOGS
   ```
   The first command should list your phone. The second prints nothing when it succeeds.

   **Realme, Oppo, OnePlus (ColorOS / Realme UI):** if it fails with
   `Neither user 2000 nor current process has android.permission.GRANT_RUNTIME_PERMISSIONS`, go to
   Settings → Additional settings → Developer options, turn on **Disable permission monitoring**, unplug and
   replug the cable, and run it again. **Xiaomi / Redmi / POCO:** turn on **USB debugging (Security
   settings)** in Developer options instead.
5. On the phone: Settings → Apps → Midfielder → **Force stop**, then open Midfielder again. The checklist
   shows ✓ for *Detect copies silently*. You can turn USB debugging off again; the permission stays (until
   you uninstall the app).

**Option B (no PC needed):** tap **…or detect copies with accessibility → Open**, choose **Midfielder
clipboard detection**, and turn it on.

**Extra step on Xiaomi, Samsung, OnePlus, Oppo, Vivo, Huawei:** these phones have their own battery
managers. Set Midfielder to **Unrestricted / No restrictions** and enable **Autostart** if offered
(Settings → Apps → Midfielder → Battery). https://dontkillmyapp.com shows the exact steps per brand.

## 5. Try it

| Do this | Expect |
|---|---|
| Copy text on the laptop, paste on the phone | same text |
| Take a screenshot on the laptop (Win+Shift+S), paste in a chat app on the phone | the image |
| In File Explorer, right-click a photo → **Copy**, paste in a chat app on the phone | the photo |
| Copy text on the phone, press Ctrl+V on the laptop | same text, within about a second |
| On the phone, Share a photo → **Send to PC** | appears in `Downloads\Midfielder` with a notification |
| On the laptop, right-click a file → **Send to → Midfielder (phone)** | appears in the phone's `Download/Midfielder` |
| Swipe down twice on the phone → edit tiles → add **Send clipboard** | a one-tap fallback |

## Troubleshooting

* **"App not installed" on the phone**:
  1. Settings → Apps: if any **Midfielder** is already listed (e.g. from an older build or the debug
     download), uninstall it and install again. Builds up to CI run #3 were each signed with a different
     key and can't update one another. Later builds share one key and update in place.
  2. If Google Play Protect or the phone's own security scan (Realme/Oppo/OnePlus: Settings → Privacy &
     security, "Pure mode" / "App installation security") is on, allow the install or turn the check off
     for this install.
  3. Still failing? Run `adb install -r app-release.apk` from the platform-tools folder (see step 4A). It
     prints the exact reason, e.g. `Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE …]`.

* **Phone says "Waiting for <PC>…"**: both devices must be on the same Wi-Fi. Guest networks and some
  public or office Wi-Fi block devices from seeing each other ("AP/client isolation"). A phone hotspot with
  the laptop connected to it works too. VPNs on either device can also block it.
* **Pairing fails / QR expired**: codes work once and expire after 5 minutes. Right-click the tray icon →
  **Pair a phone…** for a fresh code.
* **Firewall prompt was dismissed**: Windows Security → Firewall & network protection → *Allow an app
  through firewall* → enable Midfielder for **Private**.
* **Phone → PC copying doesn't arrive**: open Midfielder and check the line *Phone → PC clipboard:*. If it
  says "Tap Send clipboard", step 4 isn't finished. As a fallback, use the notification button or the
  Quick Settings tile.
* **Something doesn't sync — send me the logs**: on the PC, tray → **Open log** (`midfielder.log`). On the
  phone, the **Recent activity** section at the bottom of the Midfielder screen (a screenshot is fine), or
  `adb logcat -s Midfielder`. They contain events, kinds and sizes, never what you copied.
* **Start over**: tray → **Forget paired devices**, and on the phone → **Forget paired PC**, then pair
  again.

## Uninstall

* **Phone:** long-press Midfielder → Uninstall.
* **PC:** tray → **Exit**, untick **Start with Windows** first if you like, then delete `Midfielder.exe`,
  `%LOCALAPPDATA%\Midfielder`, and the *Midfielder (phone)* shortcut in `shell:sendto`.
