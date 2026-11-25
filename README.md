# WhatsAppWinUI

A self-contained **.NET 10** (`net10.0`) implementation of the WhatsApp Multi-Device binary protocol, Noise transport handshake, Signal Double Ratchet / SenderKey cryptography, and client architecture — cleanly structured for a headless verification CLI and a native Windows / WinUI 3 UI frontend.

> **Note:** This is an independent engineering implementation. Do not commit session credentials, private keys, or live user databases.

---

## Architecture

The solution follows a strict **Sans-I/O protocol boundary** with Central Package Management (`Directory.Packages.props`) and SDK version pinning (`global.json` -> `.NET 10.0.404`):

```text
apps/WhatsApp.Cli                -> Thin console/UI boundary consuming WhatsApp.Api
        │
        ▼
src/WhatsApp.Api                 -> NoiseHandshakeRunner, NoiseSessionSocket, IqClient, StanzaRouter,
                                    PairingHandler, ConnectionOrchestrator, KeepaliveWatchdog,
                                    OfflineBatchCoordinator, PreKeyUploader, PreKeyFetchClient,
                                    MessageSender, MessageReceiver, RetryCoordinator, PresenceClient,
                                    MediaTransferClient, HistorySyncCoordinator, PdoHistoryClient,
                                    SyncdCoordinator, UsyncClient, ProfilePictureClient, NotificationRouter, WhatsAppApiClient
        ├──▶ src/WhatsApp.Transport       -> WhatsAppEndpointBuilder, WebSocketTransport (System.IO.Pipelines + Happy Eyeballs)
        ├──▶ src/WhatsApp.Storage.Sqlite  -> Normalized WAL SQLite backend implementing IWhatsAppBackend (unified Web + Rust schemas)
        │
        ▼
src/WhatsApp.Protocol            -> Pure Sans-I/O protocol core (zero socket or SQLite I/O):
                                    Binary codec/JID/Stanza builders (Pairing, Messaging/Rich, Presence/ChatState, USync, Avatars, Notifications, ABProps/TcToken/MediaConn),
                                    Noise XX/IK/XXfallback, XEdDSA/Curve25519, PairingCrypto/PairCodeCrypto/ContactHash,
                                    MessageFraming/ReportingToken/AddonEncryption/MediaRetryCrypto,
                                    Signal Double Ratchet & SenderKey, MediaCipher (upload/sidecar/CDN URLs), HistorySync/LidPnCache,
                                    AppState (syncd + 128B LTHash), Abstractions
        │
        ▼
src/WhatsApp.WaProto             -> Self-contained Grpc.Tools protoc codegen from live-synced whatsapp.proto + ClientPayloadFactory
```

---

## Implementation Status

| Phase | Scope | Status |
|---|---|---|
| **Phase 0** | `.NET 10` solution layout, self-contained `whatsapp.proto` codegen, Binary XML codec & stanza builders, Noise `NoiseFrameCodec` + `NoiseCertVerifier` | Complete |
| **Phase 1** | Happy Eyeballs dialer (`WhatsAppEndpointBuilder`), `System.IO.Pipelines` `WebSocketTransport`, `NoiseHandshakeRunner` (`XX`/`IK`/`XXfallback`), `NoiseSessionSocket` | Complete |
| **Phase 2** | Signal X3DH + bidirectional Double Ratchet (`SignalCipher`/`SignalSession`), group `SenderKeyCipher`, `PreKeyManager`, `SignalStateCodec` | Complete |
| **Phase 3** | Normalized WAL SQLite persistence (`WhatsApp.Storage.Sqlite`: all 6 `IWhatsAppBackend` stores unified across Web IndexedDB & Rust schemas) | Complete |
| **Phase 4** | Correlated `IqClient`, `StanzaRouter`, QR & ADV `PairingCrypto`/`PairingHandler`, `KeepaliveWatchdog`, `OfflineBatchCoordinator`, `PreKeyUploader`, and `ConnectionOrchestrator` | Complete |
| **Phase 5** | AppState (`syncd`) + 128B HKDF-SHA256 `LtHash` engine (`AppStateKeys`, `AppStateHasher`, `AppStateMutationCodec`, `AppStatePatchParser`, `AppStateProcessor`, `SyncdCoordinator`) | Complete |
| **Phase 6** | Live Web `.pb` sync, Pair-by-Phone-Code (`PairCodeCrypto`), `AppStateSyncKeyShare`/dirty-bit glue, `media_conn`/`abprops`/`tctoken`, `<notification>` router, `@username`/`usync`, avatars | Complete |
| **Phase 7** | 1:1/group E2EE messaging, retry/PDO backfill, rich messages (reactions/edits/revokes/polls/events/pins/drafts/presence), MMS upload/download, `HistorySync` | Complete |
| **Phase 8** | Full Groups + Group History Sharing, Communities (`CAG` & subgroups), Statuses/Stories (`status@broadcast` & `GroupStatusV2`) | Next |
| **Phase 9** | Channels/Newsletters (`w:mex` & plaintext media), Business profiles/Catalogs/Labels/Quick Replies/Blocking, Meta AI/Bots | Planned |
| **Phase 10** | VoIP & WebRTC Calling (`<call>` signaling, ICE/TURN/DTLS-SRTP media loop, group calls, call logs) | Planned |
| **Phase 11** | End-to-end headless CLI live verification harness (`apps/WhatsApp.Cli`) | Planned |
| **Phase 12** | Native Windows 11 WinUI 3 Desktop Client (`apps/WhatsApp.Desktop`) | Planned |

See [`docs/MASTER_PLAN.md`](docs/MASTER_PLAN.md) for detailed design invariants and milestone plans, and [`docs/LIVE_WEB_VS_RUST_DIFF.md`](docs/LIVE_WEB_VS_RUST_DIFF.md) for the running live `web.whatsapp.com` vs `whatsapp-rust` differential audit log.

---

## Building & Testing

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/) (`10.0.404` or compatible roll-forward per `global.json`).

```bash
dotnet restore WhatsApp.sln
dotnet build WhatsApp.sln --no-restore
dotnet test WhatsApp.sln --no-build
```

All unit tests use deterministic synthetic fixtures and run completely offline with zero external dependencies.

---

## Credits & Acknowledgments

This project builds on architectural insights, protocol research, and open-source schema work from the following sources:

* **[`oxidezap/whatsapp-rust`](https://github.com/oxidezap/whatsapp-rust)** by **João Lucas de Oliveira Lopes** (MIT License, Copyright (c) 2025):
  * Primary inspiration for the Sans-I/O protocol architecture, pre-wire durability invariants, single-writer channel serialization, Noise handshake / framing design, Signal session / SenderKey mechanics, and binary JID/node codec conventions.
  * The protobuf definition bundled in [`src/WhatsApp.WaProto/whatsapp.proto`](src/WhatsApp.WaProto/whatsapp.proto) originates from `whatsapp-rust`'s `waproto/src/whatsapp.proto` (MIT License).
* **WhatsApp Web (`web.whatsapp.com`) protocol analysis**:
  * Wire-level behavior, protobuf tag audits, Noise `ED\x00\x01` + `WA\x06\x03` header framing, and on-demand PDO history-sync structures were cross-verified against WhatsApp Web client behavior.
* **Cryptographic & Protocol Specifications**:
  * [Noise Protocol Framework](https://noiseprotocol.org/) (`Noise_XX_25519_AESGCM_SHA256`, `Noise_IK_25519_AESGCM_SHA256`, `Noise_XXfallback_25519_AESGCM_SHA256`)
  * [Signal Protocol](https://signal.org/docs/) (X3DH, Double Ratchet, Sender Keys, XEdDSA)
  * [Google.Protobuf](https://protobuf.dev/) & `Grpc.Tools` for deterministic build-time C# code generation
