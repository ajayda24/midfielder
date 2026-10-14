# Security model

**Goal:** only your paired devices can read or inject your clipboard and files, even on a shared or hostile
Wi-Fi.

| Threat | Defense |
|---|---|
| Eavesdropping on the LAN | TLS 1.3 for all traffic (control and data) |
| Impersonating the PC or phone | Mutual TLS with **pinned** certificate fingerprints, checked on every handshake; `Hello.device_id` must match the certificate |
| MITM during first pairing | The QR code carries the PC's fingerprint and a 256-bit secret out of band (camera). Both sides prove knowledge of the secret with HMACs bound to both certificates |
| Guessing the pairing code | 256-bit secret; the window is single-use and expires in 5 minutes |
| Malicious peer input | Frame lengths checked before allocation (64 KiB before authentication), checked arithmetic, offsets validated, hashes verified before files are committed, file names sanitized (no paths, reserved names, control characters) |
| Connection floods | At most 16 concurrent connections; unauthenticated peers fail the TLS handshake unless a pairing window is open |
| Spoofed UDP knock | Carries no secret; it only makes the phone dial an already-paired device over authenticated TLS, rate limited |
| Key theft at rest | Windows: identity and device list encrypted with DPAPI (current user). Android: AES-256-GCM with a Keystore key that never leaves the Keystore, stored in `noBackupFilesDir` |
| Secrets in clipboard history | Password-manager exclusion formats skipped or flagged; sensitive items never enter history; on Windows they're excluded from Win+V and cloud clipboard |

**Not covered:** a compromised device that is already paired, and someone with your unlocked PC or phone.
Unpair from either side to revoke ("Forget paired devices" / "Forget paired PC").
