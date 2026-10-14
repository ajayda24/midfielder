# Midfielder

Your Windows 11 laptop and Android phone as one system. Copy on one, paste on the other. Send files either
way. No cloud and no account: after a single QR-code pairing the devices find each other on your Wi-Fi and
connect on their own.

| | Phone → PC | PC → Phone |
|---|---|---|
| Copied text | ✅ automatic (see [Android setup](#android-one-time-setup)) | ✅ automatic |
| Copied images / screenshots | ✅ automatic | ✅ automatic |
| Files | Share → **Send to PC**, or the in-app picker | Right-click → **Send to ▸ Midfielder (phone)**, or the tray menu |

## How it works (short version)

* **Pair once.** The PC shows a QR code; the phone scans it. Each device has its own key pair. After
  pairing they recognize each other by certificate fingerprint, the way SSH remembers a host.
* **Connect automatically.** The PC advertises itself on the LAN (mDNS). The phone connects when something
  happens: Wi-Fi comes up, the screen turns on, or the PC "knocks" because it has something to deliver.
  It never polls.
* **Everything is encrypted.** All traffic uses TLS 1.3 with mutual authentication and pinned certificates.
  Nothing leaves your network.
* **Battery first.** No timers tick while idle. The phone sends one tiny heartbeat every ~4 minutes while
  awake and holds Wi-Fi/CPU locks only while file bytes are moving. The Windows app runs in Efficiency
  mode (EcoQoS) when idle.

Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [docs/PROTOCOL.md](docs/PROTOCOL.md) ·
[docs/ANDROID_BACKGROUND.md](docs/ANDROID_BACKGROUND.md) · [docs/SECURITY.md](docs/SECURITY.md)

## Install

Build outputs come from CI (GitHub Actions → latest run → artifacts), or build locally (below).

**Windows:** run `Midfielder.exe` from the `midfielder-windows` artifact (needs the .NET 8 Desktop Runtime).
It sits in the tray. On first start, Windows Firewall asks whether to allow it. Allow **Private networks**.

**Android (10+):** install the APK from `midfielder-android` (sideload). Open it and tap **Pair with PC**,
then scan the code from the tray menu (**Pair a phone…**).

### Android one-time setup

PC → phone works as soon as you pair. Phone → PC needs a little help, because Android 10+ hides clipboard
changes from background apps. The app's checklist walks you through it:

1. **No battery limits** for Midfielder, so the connection survives Doze.
2. **Display over other apps.** This lets Midfielder bring an invisible window forward for a split second
   to read what you copied.
3. **Pick one way to detect copies:**
   * **Recommended:** a one-time command from a PC with USB debugging enabled:
     `adb shell pm grant dev.midfielder android.permission.READ_LOGS`
   * **Or** turn on *Midfielder clipboard detection* in Accessibility settings.
   * **Or neither:** use the **Send clipboard** Quick Settings tile or notification button.

Why these steps exist and what each one costs: [docs/ANDROID_BACKGROUND.md](docs/ANDROID_BACKGROUND.md).

## Build from source

```
protocol/            wire format (.proto) + golden test vectors shared by both languages
android/core/        Kotlin core: protocol, crypto, sessions, sync, transfers (pure JVM)
android/app/         Android app (framework APIs only, no AndroidX)
windows/             .NET 8 solution: Midfielder.Core, .Core.Tests, .Cli (headless node), .App (tray)
docs/                design notes
```

| What | Command | Needs |
|---|---|---|
| Kotlin core tests | `cd android && ./gradlew :core:test` | JDK 17+ |
| C# core tests | `dotnet test windows/Midfielder.Core.Tests` | .NET 8 SDK |
| Kotlin ↔ C# interop test | `dotnet build windows/Midfielder.Cli -c Release` then `cd android && ./gradlew :core:test -PinteropCli=$PWD/../windows/Midfielder.Cli/bin/Release/net8.0/Midfielder.Cli.dll` | both |
| Android APK | `cd android && ./gradlew :app:assembleDebug` | Android SDK (API 35) |
| Windows app | `dotnet publish windows/Midfielder.App -c Release -r win-x64 --self-contained false` | Windows, .NET 8 SDK |

The Android Gradle Plugin is loaded only when an Android SDK is configured, so the core builds and tests
anywhere.

## Status

This is a first milestone. Clipboard (text and images) and file transfer work in both directions. Tests
cover framing, crypto, pairing, sessions, sync logic and resumable transfers in both languages, plus a test
where a real Kotlin node talks to a real C# node. Things to validate on real hardware are listed in
[docs/ANDROID_BACKGROUND.md](docs/ANDROID_BACKGROUND.md#on-device-test-checklist). Planned next: notification
mirroring, then SMS, remote input and calls. The protocol already has room for them.
