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
