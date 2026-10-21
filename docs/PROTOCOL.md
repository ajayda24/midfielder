# Wire protocol (v1)

Source of truth: [`protocol/midfielder.proto`](../protocol/midfielder.proto). Golden vectors:
[`protocol/test-vectors/vectors.properties`](../protocol/test-vectors/vectors.properties). These were
generated independently, in Python, and both test suites check them.

## Transport

* TCP, TLS 1.3 only, **mutual** authentication. Each device has an EC P-256 key with a self-signed
  certificate. Trust means the peer certificate's SHA-256 fingerprint is in the local trust store. There
  are no CAs and no hostname checks.
* `device_id` = the first 16 bytes (32 hex chars) of the fingerprint. `Hello.device_id` must match the
  certificate.

## Framing

`uint32 big-endian length N` then `N` bytes of `Envelope`. `N` must be in `1..8 MiB` from a paired peer,
and `1..64 KiB` before authentication and for data-connection control frames. Anything else closes the
connection *before* allocating. Unknown `oneof` cases are ignored, for forward compatibility.

## Control connection

```
dialer                                  listener
  TLS handshake (client cert required)
  Hello ───────────────────────────────►
  [PairRequest(proof)] ────────────────►   only when pairing for the first time
                    ◄─────────────────── [PairResult(accepted, proof)]
                    ◄─────────────────── Hello
  … Ping/Pong, ClipboardItem, TransferOffer/Answer/Cancel, Bye …
```

Heartbeat: the dialer sends `Ping` after 4 min of inbound silence and closes if nothing arrives within
20 s. The listener never pings. It closes a session silent for 45 min.

## Pairing

The listener opens a window (5 min, **single attempt**) and shows
`midfielder://pair?v=1&fp=<64 hex>&s=<base64url 32-byte secret>&a=<host:port,…>&n=<name>` as a QR code.

* The dialer only accepts a server certificate whose fingerprint equals `fp`.
* `PairRequest.proof = HMAC-SHA256(secret, "midfielder-pair-client-v1" ‖ client_fp ‖ server_fp)`
* `PairResult.proof  = HMAC-SHA256(secret, "midfielder-pair-server-v1" ‖ client_fp ‖ server_fp)`

Binding both fingerprints into the MAC ties the proof to these two certificates (channel binding). The
secret never crosses the network. Any failed attempt closes the window.

## Data connection

Opened by the dialer for each file. First frame `DataOpen(transfer_id, file_index)`. Then:

```
receiver → DataResume(offset)
sender   → raw bytes [offset, size)
sender   → DataDone(ok=true, sha256=<whole file>)
receiver → DataDone(ok, reason)
```

A file is committed only after its hash matches the trailer (and `FileEntry.sha256`, if the offer included
it).

## Knock (UDP 47811)

22 bytes: `"MFK1" ‖ device_id (16 bytes) ‖ uint16 BE listener port`. It carries no secret. The phone
reacts only by dialing an already-paired device over authenticated TLS, rate limited.

## Limits

| | |
|---|---|
| Frame (authenticated / pre-auth) | 8 MiB / 64 KiB |
| Inline clipboard (images) / text | 6 MiB / 1 MiB |
| Files per offer | 1000 |
| File name | 150 chars after sanitizing |
| Device name | 64 chars |

## Optional features (capabilities 50–79)

Optional features ride on the control connection. Each has a capability string that a device puts in
`Hello.capabilities` **only while the user has that feature switched on**. When a switch changes mid-session the
device sends `FeaturesChanged` with its full list, which replaces the earlier one. A device sends a feature message
only to peers that advertise the capability and only while its own switch is on, so turning a feature off on either
device stops it for the pair. Unknown capabilities and messages are ignored.

| Capability | Messages | Direction |
|---|---|---|
| `notify` | `NotificationPosted` (same `key` = update), `NotificationRemoved` | phone → PC |
|  | `NotificationAction` (exactly one of `reply_text`, `button_id`, `dismiss`) | PC → phone |
| `battery` | `BatteryStatus` (sent when level or charger state changes, and on connect) | both ways |
| `ring` | `RingRequest` (`start = false` stops; the receiver also stops on its own after 30 s) | both ways |
| `media` | `MediaState` (what plays on the sender; `active = false` clears; art omitted when `art_unchanged`) | both ways |
|  | `MediaCommand` (controls the receiver's current player) | both ways |
| `audio` | `AudioRequest(route, start)` (asks the other side to start or stop a route) | both ways |
|  | `AudioOffer(stream_id, sample_rate, channels)`, `AudioStop(stream_id, reason)` | listener → dialer, `AudioStop` both ways |

Limits: notification title 200 and text 1,000 characters, icon ≤ 8 KiB PNG, at most 3 buttons; album art ≤ 64 KiB
JPEG (300 px). Senders coalesce: notifications are sent only when title or text change, media state is debounced
(250–300 ms).

### Audio streams

Two routes, each behind its own switch:

- **Phone → PC** (`AUDIO_ROUTE_PHONE_TO_PC`): plain Bluetooth A2DP. The PC opens an `AudioPlaybackConnection` to the
  paired phone and becomes its speaker; no audio crosses Midfielder's connections. `AudioRequest` only lets the
  phone ask the PC to connect or disconnect.
- **PC → phone** (`AUDIO_ROUTE_PC_TO_PHONE`): the PC (the listener) captures what it plays and sends
  `AudioOffer`. The phone (the dialer) opens a data connection whose first frame is `AudioOpen(stream_id)`, then
  reads `AudioChunk(seq, pcm)` frames: 20 ms of interleaved little-endian PCM16 each, on a steady clock (silence
  when nothing plays). The stream ends when either side closes the connection or sends `AudioStop`. A dialer that
  has the route off answers an offer with `AudioStop`.

Players keep the delay bounded: start after ~100 ms, never queue more than ~200 ms, and drop late audio rather
than fall behind.

