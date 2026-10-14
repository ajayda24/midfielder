# Architecture

```
┌──────────── Windows 11 (C# / .NET 8) ────────────┐          ┌─────────── Android 10+ (Kotlin) ───────────┐
│ Midfielder.App (tray only)                       │          │ SyncService (foreground, connectedDevice)  │
│  ClipboardMonitor ── WM_CLIPBOARDUPDATE          │          │  SyncEngine                                │
│  LanDiscovery ────── mDNS advert + UDP knock     │  TLS 1.3 │   ConnectionManager ─ NSD bursts, knocks   │
│  SendToPipe ──────── Explorer "Send to"          │◄────────►│   CaptureTriggers ─── listener/logcat/a11y │
│  AppStorage ──────── DPAPI                       │  mutual  │   ClipboardBridge, MediaStoreFileSink      │
│ Midfielder.Core (listener role)                  │   auth   │ :core (dialer role, same design as C#)     │
└──────────────────────────────────────────────────┘          └────────────────────────────────────────────┘
```

Both cores implement the same design, file for file: `Limits`, framing, `DeviceIdentity`, pairing,
`PeerSession`, `MidfielderNode`, `ClipSync`, `ClipboardHistory`, `TransferEngine`, `FileNameSanitizer`,
`Knock`, `Backoff`. Golden vectors in `protocol/test-vectors` keep them byte-compatible, and
`InteropTest` runs a Kotlin node against the C# node over real TLS.

## Roles

The **PC listens** (TCP 47810, advertised as `_midfielder._tcp`). The **phone dials**. That split is
deliberate:

* The phone never needs an open port or an mDNS advertisement, and never has to answer queries.
* The dialer drives heartbeats, so the phone controls its own radio wake-ups.
* Every data connection is opened by the dialer, in either direction of transfer.

## Connection lifecycle (phone)

Attempts happen only on events: service start, Wi-Fi available, screen on, a knock from the PC, a dropped
session, or a send with no session. An attempt goes:

1. Try the last known address.
2. Otherwise run one mDNS burst (≤ 8 s, multicast lock held only for the burst) looking for the PC by its
   device id, then connect.
3. If it still fails and the screen is on, retry with capped exponential backoff (2 s → 5 min). With the
   screen off, give up until the next event.

When the PC has something to deliver and no session is up, it sends a 22-byte UDP **knock** to the phone's
last address. The phone, blocked in `receive()` and using no CPU, dials straight back.

## Clipboard

`ClipSync` (same in both cores):

* **Conflation:** during a burst of copies only the newest item is sent.
* **Echo prevention:** content we just applied from a peer is not sent back when the OS reports the change.
  On top of that, each platform suppresses the change event its own write causes. Windows does this by
  clipboard sequence number, because it may re-encode images. Android uses a short window, and recognizes
  its own image URIs.
* **Catch-up:** on (re)connect each side re-sends its latest item flagged `catch_up`. The receiver applies it
  only if it's newer, so a phone that slept through a copy has it when you pick it up.
* **Size:** text up to 1 MiB and images up to 6 MiB go inline. Bigger images go as a clipboard-purpose
  transfer.
* **Privacy:** password-manager exclusion formats on Windows and `EXTRA_IS_SENSITIVE` on Android are
  honored. Sensitive items sync but never enter history.

## File transfer

An offer on the control connection is followed by one data connection per file (opened by the dialer):

```
receiver → DataResume(offset)        how much it already has
sender   → raw bytes [offset, size)  no per-chunk framing
sender   → DataDone(sha256)          hash of the whole file, computed while streaming
receiver → DataDone(ok)              after verifying; only then is the file committed
```

Partial data persists (`.part` file on Windows, `IS_PENDING` MediaStore entry on Android). A broken
connection retries with backoff and resumes from the last byte received. If the whole session dropped, the
sender re-offers the same transfer id when the peer reconnects, within 10 minutes. Received files never
overwrite existing ones (`name (1).ext`).

## Efficiency and robustness rules

| Rule | Where |
|---|---|
| No polling: OS events, blocking reads, suspended coroutines/tasks only | everywhere |
| Phone pings only after 4 min of silence; PC never pings and drops a session only after 45 min of silence | `NodeConfig` |
| Monotonic clock that counts deep sleep on Android (`elapsedRealtime`) | `SyncEngine` |
| Logcat watcher runs only while the screen is on | `CaptureTriggers` |
| Wake + Wi-Fi locks only during transfers, ref-counted, 30 min hard cap | `TransferLocks` |
| EcoQoS on Windows when idle, off during transfers | `TrayContext` |
| Every frame length checked before allocation (8 MiB authenticated, 64 KiB pre-auth) | framing |
| 64-bit sizes, checked arithmetic, offsets validated against size | `TransferEngine` |
| Bounded send queues; conflated clipboard channel | `PeerSession`, `ClipSync` |
| History bounded by count and bytes | `ClipboardHistory` |
| Streaming I/O with one fixed 64 KiB buffer per transfer | `TransferEngine` |
| Each session owns its coroutines/tasks; closing cancels them and closes the socket | `PeerSession` |
| Stale incoming transfers cleaned lazily (no timer) | `MidfielderNode` |
| Max 16 concurrent connections; pairing window single-use | `MidfielderNode` |
| UI only when opened; Android UI uses framework views (no AndroidX) | apps |
