# WhatsAppWinUI Master Implementation Plan

> **Living Architecture & Roadmap Document** (`docs/MASTER_PLAN.md`)
> Grounded in: (1) Live `web.whatsapp.com` wire captures (QR pairing, cold-boot `initScript` trace, on-demand PDO `syncType = 6`), (2) `reference/whatsapp-rust` Sans-I/O & concurrency invariants, and (3) the consolidated **Option A `.NET 10 (`net10.0`)`** solution layout.
>
> **Core Rule:** Zero WinUI 3 / XAML UI code will be written until headless `WhatsApp.Api` + `apps/WhatsApp.Cli` live protocol verification passes cleanly.
>
> **Windows Environment Note:** The solution pins `.NET 10` SDK `10.0.404` via [global.json](../global.json). If `DOTNET_ROOT` in your user environment points to an older local `.NET 9` directory, run builds/tests with `$env:DOTNET_ROOT = "C:\Program Files\dotnet"`.

---

## 1. Empirical Ground Truth (Live Browser Captures)

### A. Cold-Boot Reconnect (`initScript` Trace from `0 ms`)
Reopening an already-paired session uses **offline stanzas + AppState `syncd`**, **never** HistorySync chunks (`history-sync-notification = 0`):
* `609–1620 ms`: Opens IndexedDB stores and prewarms background workers (`sr-init`, `ww-hrp-init`, `endpoint_started`, `prewarm`).
* `1635 ms`: Dual-port **Happy Eyeballs** WebSocket race carrying persisted Edge Routing token (`?ED=CAgIIwgS`):
  * `wss://web.whatsapp.com/ws/chat?ED=CAgIIwgS` (`:443`)
  * `wss://web.whatsapp.com:5222/ws/chat?ED=CAgIIwgS` (`:5222`, loser closes with `1006` at `2279 ms`).
* `1904–2200 ms`: Noise IK `ClientHello` (231B) -> `ServerHello` (57B) -> `<success/>` (134B) -> burst of ~37 outgoing control frames (`45B–67B`: acks, pre-key check, `<iq type="set" xmlns="passive"><active/></iq>`).
* `2452–10243 ms`: Receives `<ib><offline_preview count="N"/>` + client-paced `<ib><offline_batch count="200"/>` stanzas, applies `syncd` (`critical_block` / `regular`, `10093 ms`), and updates `active-message-ranges` (`10243 ms`).

### B. Initial Pairing & On-Demand History Pagination
* **Initial Pairing:** Delivers `HistorySync` chunks (`Chunk 0` inline `INITIAL_BOOTSTRAP`/`PUSH_NAME`, `Chunks 1..N` via CDN `/mms/md-msg-hist/...`, Zlib `0x78 0x01`, encrypted with `"WhatsApp History Keys"` HKDF-112B + AES-256-CBC + trailing 10-byte HMAC-SHA256).
* **On-Demand Backfill:** Scrolling past the local `active-message-ranges` anchor dispatches an E2EE `ProtocolMessage` (Tag 31) -> `PeerDataOperationRequestMessage(HISTORY_SYNC_ON_DEMAND)` to `<primary_pn>.0:0@s.whatsapp.net`. Phone replies asynchronously with `HistorySyncNotification` (`syncType = 6`) correlated by `peerDataRequestSessionId`.

---

## 2. Clean Option A `.NET 10` Architecture (Scaffolded & Active)

All legacy split folders (`wacore/`, `waproto/`, `storages/`, `transports/`, `http_clients/`, and old split test projects) have been deleted and replaced by an idiomatic, API-isolated `.NET 10` dependency graph managed via [Directory.Build.props](../Directory.Build.props) and [Directory.Packages.props](../Directory.Packages.props):

```
apps/WhatsApp.Cli   (Headless console harness; future apps/WhatsApp.Desktop WinUI host)
        │
        ├───────────────────────────┬──────────────────────────┐
        ▼                           ▼                          ▼
src/WhatsApp.Api          src/WhatsApp.Storage.Sqlite   src/WhatsApp.Transport
(Client facade,           (SqliteBackend :              (WebSocketTransport +
 orchestration, runtime    IWhatsAppBackend, WAL)        Happy Eyeballs +
 actor / channels)                                       WhatsAppHttpClient)
        │                           │                          │
        └───────────────────────────┼──────────────────────────┘
                                    ▼
                          src/WhatsApp.Protocol
         (Binary codec/JID/Node, Noise XX/IK/XXfallback, XEdDSA/Curve25519,
          SignalCipher/Messages, HistorySync, MediaCipher, AppState constants,
          Abstractions: DeviceCredentials, IWhatsAppBackend, ITransport, IHttpClient, Events)
                                    │
                                    ▼
                          src/WhatsApp.WaProto
            (Grpc.Tools protoc codegen from src/WhatsApp.WaProto/whatsapp.proto
             -> global::Whatsapp.*, plus ClientPayloadFactory)
```

### Design Invariants Carried Over from `reference/whatsapp-rust`
1. **Strict API vs App Boundary:** `WhatsApp.Api` exposes strongly-typed commands + event streams (`WhatsAppEvent`) and knows nothing about console I/O or WinUI XAML.
2. **Pre-Wire Durability:** `Signal` / Noise / AppState storage mutations commit through `IWhatsAppBackend` BEFORE encrypted frames are written to `ITransport`.
3. **Single-Writer Serialization + Generation Cancellation:** Per-session/per-chat serialized execution (`Channel<T>`) and `ConnectionGeneration` tokens so stale sockets cannot corrupt state across reconnects.
4. **Pure Sans-I/O Protocol Core:** `WhatsApp.Protocol` performs zero socket or SQLite I/O; `WhatsApp.Transport` depends only on `WhatsApp.Protocol` (never on `WhatsApp.Storage.Sqlite` or `WhatsApp.WaProto`).

---

## 3. Phase 0 Progress & Next Incremental Steps

### Completed in Initial `.NET 10` Option A Scaffold (Phases 0A–0D Foundation)
* Pinned `.NET 10.0.404` in [global.json](../global.json), `net10.0` + nullable/implicit usings in [Directory.Build.props](../Directory.Build.props), and Central Package Management in [Directory.Packages.props](../Directory.Packages.props).
* Wired `Grpc.Tools` `protoc` codegen in `src/WhatsApp.WaProto` against `src/WhatsApp.WaProto/whatsapp.proto` (self-contained copy of `oxidezap/whatsapp-rust` `whatsapp.proto`, `global::Whatsapp.*`, zero warnings/errors) plus `ClientPayloadFactory`.
* **Live Web Schema Audit Note (`web.whatsapp.com` vs `whatsapp.proto`):** Verified `WAWebProtobufsWa6.pb`, `WAWebProtobufsAdv.pb`, `WAWebProtobufsCompanionReg.pb`, `WAWebProtobufsHistorySync.pb`, `WAWebProtobufsE2E.pb`, and `WAWebProtobufsServerSync.pb` directly in Chrome. All core wire fields (`HandshakeMessage`, `ClientPayload` `1..50`, `ADV*`, `Syncd*`, `HistorySync` `1..26`, `Message` `1..141`, `PeerDataOperationRequestMessage` `1..13`) match 1:1. **Tracked future delta (5 newer optional draft/reply fields on live web not yet in `whatsapp.proto`):**
  * `DeviceProps.HistorySyncConfig`: `29` (`supportDraftSync`)
  * `Conversation`: `70` (`unreadReplyToMyMessageCount`), `71` (`draft`: `ConversationDraft`)
  * `Message.ProtocolMessage`: `42` (`messageEditDisplayType`), `43` (`draftMessageSync`: `DraftMessageSync`)
  *(Protobuf skips unknown optional tags safely; add later if cross-device draft sync is needed.)*
* Consolidated `Binary`, `Noise`, `Signal`, `AppState` constants, `HistorySync`, generalized `MediaCipher`, `DeviceCredentials`, `IWhatsAppBackend`, `ITransport`, `IHttpClient`, and `WhatsAppEvent` records into `src/WhatsApp.Protocol`.
* Created clean boundary scaffolds for `src/WhatsApp.Api`, `src/WhatsApp.Storage.Sqlite`, `src/WhatsApp.Transport`, and `apps/WhatsApp.Cli`.
* Migrated all existing tests + added `WaProtoCodegenTests` and `ScaffoldContractsTests` in `tests/WhatsApp.Protocol.Tests` (`118` tests) plus `tests/WhatsApp.Api.Tests` (`1` test).
* Deleted superseded legacy directories (`wacore/`, `waproto/`, `storages/`, `transports/`, `http_clients/`, `tests/WhatsApp.Binary.Tests/`, `tests/WhatsApp.Crypto.Tests/`, `tests/WhatsApp.HistorySync.Tests/`) and recreated clean [WhatsApp.sln](../WhatsApp.sln).
* **Phase 0E — Typed Stanza & IQ Builders (`src/WhatsApp.Protocol/Binary/Stanzas/`):** Implemented & wire-round-trip verified `StanzaBuilder`, `PassiveIq`, `PingIq`, `OfflineBatchIb`, `DirtyBitsIq`, `PreKeyIq`, `AppStateSyncIq`, `UsyncIq`, `MediaConnIq`, `PrivacyIq`, `BlocklistIq`, `AckStanza`, and `ReceiptStanza`.
* **Phase 0F — Wire-Level `NoiseFrameCodec` & `NoiseCertVerifier` (`src/WhatsApp.Protocol/Noise/`):** Implemented `NoiseHandshakeHeader` (`ED\x00\x01` + `WA\x06\x03`), `NoiseFrameEncoder` (counter safety + poison semantics), `NoiseFrameDecoder` (fragmented/coalesced framing), `NoiseSessionCodec` (preserving coalesced handshake remainder), and XEdDSA `NoiseCertVerifier`.

*(Phase 0 complete — ready for Phase 1!)*

---

## 4. Subsequent Phases (1 through 7 — Executed Incrementally)

### Phase 1 — Transport Pipeline & `NoiseSessionSocket` (`WhatsApp.Transport` + `WhatsApp.Api`) ✅
* **1A (`WhatsAppEndpointBuilder` & Happy Eyeballs):** Added dual-port `:443`/`:5222` endpoint builder with unpadded base64url `?ED=` query encoding and async loser-socket disposal + fail-fast fallback wakeup in `WebSocketTransportFactory`.
* **1B (`System.IO.Pipelines` WebSocket Pump):** Upgraded `WebSocketTransport` to pump `WebSocketMessageType.Binary` fragments into pooled `Pipe` memory with `SemaphoreSlim` write serialization and `TransportDisconnectReason` tracking.
* **1C (`NoiseHandshakeRunner`):** Implemented `XX`, `IK`, and automatic `IK -> XXfallback` handshake state machine with `ED\x00\x01` preamble isolation, XEdDSA `NoiseCertVerifier`, 30s timeout, and `RemainingWireBytes` handoff.
* **1D (`NoiseSessionSocket`):** Implemented bounded `Channel<T>` single-writer AES-GCM loop (opportunistic batching + `Poison` on write failure), inbound `BinaryPacker`/`BinaryDecoder` `Node` pump, and `ConnectionGeneration` fencing in `WhatsAppApiClient`.

### Phase 2 — Complete Double Ratchet & SenderKey (`src/WhatsApp.Protocol/Signal/`) ✅
* **2A (Outbound X3DH & Encryption):** Extended `SignalSession`, implemented `InitiateSession` + `Encrypt` (`PreKeySignalMessage`/`SignalMessage`), full responder ratchet turns on `DecryptPreKey`/`DecryptSignal`, and `HMACSHA256.TryHashData` chain stepping.
* **2B (Bidirectional Ratchet & Skipped Keys):** Added bounded `SkippedMessageKeys` (`MaxSkippedKeys = 2000`, `PeerFutureLimit = 2000`, `SelfFutureLimit = 25000`), replay rejection, and atomic MAC-verified state updates.
* **2C (`SenderKey` Group E2EE):** Added `SenderKeyDistributionMessage`, `SenderKeyMessage`, and `SenderKeyCipher` (`"WhisperGroup"` HKDF-48B, XEdDSA signatures, SKDM replay preservation, bounded group skipped seeds).
* **2D (`PreKeyManager` & Serialization):** Added `PreKeyManager` (OTP batch generation, SPK rotation, SHA-1 `digestKey` hash) and protobuf `SignalStateCodec` (`SessionStructure` / `SenderKeyRecordStructure`).

### Phase 3 — Normalized SQLite Backend (`src/WhatsApp.Storage.Sqlite`) ✅
* Unified normalized WAL schema cross-verified against both live `web.whatsapp.com` IndexedDB (`signal-storage` v90, `model-storage` v2180) and `whatsapp-rust` SQLite migrations (`device_id` multi-account scoped, `@c.us` → `@s.whatsapp.net` normalization, `active_message_ranges` keyed by `(chat_jid, action, device_id)`).
* **3A (`SqliteOptions`, `SqliteMigrations`, `SqliteRunner`):** Thread-safe lazy initialization gate, `PRAGMA journal_mode = WAL; synchronous = NORMAL; foreign_keys = ON; busy_timeout = 5000`, `BEGIN IMMEDIATE` write-transaction runner with `SQLITE_BUSY`/`SQLITE_LOCKED` exponential backoff retry, and 17 normalized tables in `001_initial_schema`.
* **3B (`SqliteDeviceStore`, `SqliteSignalStore`):** Persists `DeviceCredentials` (64-byte `private || public` keypairs, pairing reset via `ClearPairingAsync`) plus `signal_sessions` (`SignalStateCodec`), `signal_identities`, batched `prekeys`, and group `sender_keys`.
* **3C (`SqliteAppSyncStore`, `SqliteProtocolStore`, `SqliteMsgSecretStore`):** Single-transaction atomic `CommitPatchAsync` (version + 128B LTHash + removed/added MACs), `lid_pn_mapping`, `device_list_cache`, `tc_tokens`, and partial-indexed `msg_secrets` expiration cleanup.
* **3D (`SqliteHistoryStore`):** Transactional `HistorySyncBatch` ingestion (`lid_pn_mapping`, `contacts`/pushnames, `chats`, `messages`, automatic `active_message_ranges` stitching), cursor pagination (`beforeTimestamp` + `limit`), monotonic range expansion, and synthetic live-Web shape tests. *(Note: add `signed_prekeys` history table before implementing Signed PreKey rotation in Phase 4/6.)*

### Phase 4 — Stanza Router, QR Pairing & Connection Lifecycle (`src/WhatsApp.Api` + `src/WhatsApp.Protocol`) ✅
* Cross-language differential verification (`rust_interop_probe` ↔ `cs_probe`) caught and fixed a `SignalStateCodec` envelope mismatch: `SerializeSession` now wraps `SessionStructure` inside `RecordStructure { CurrentSession }` cleanly (keeping legacy fallback) and `DeserializeSession` strips 33-byte `0x05` curve tags consistently.
* **4A (`IqClient` & `StanzaRouter`):** Correlated `<iq id>` waiter table with generation cancellation, pre-send registration, duplicate-ID guard, timeout cleanup, and generation-scoped inbound stanza routing (`<iq>`, `<ib>`, `<success>`, `<failure>`, `<stream:error>`, `<ack>`, `<receipt>`, `<notification>`, `<message>`).
* **4B (`PairingCrypto`, `PairingStanzas`, `PairingHandler`):** 5-field QR payload encode/parse (`CompanionWebClientType`), ADV HMAC (`[0x06, 0x05]` hosted-account check before decoding inner protobufs) + XEdDSA verification/signing (`[0x06, 0x00]`/`[0x06, 0x05]` account, `[0x06, 0x01]`/`[0x06, 0x06]` device), `60s`/`20s` cancellable QR rotation, pre-wire credential persistence (`SaveAsync` before `<pair-device-sign>`), optional PN `jid` with `lid`-only support matching `WASmaxMdSetRegRPC`, and bidirectional Rust↔C# + live `web.whatsapp.com` bundle & WebSocket verification.
* **4C (`ConnectionHealth`, `KeepaliveWatchdog`, `OfflineBatchCoordinator`, `PreKeyUploader`, `ConnectionOrchestrator`):** Randomized `15s–30s` `w:p` keepalive probes with `20s` dead-socket tracking and 3-strike terminal reconnect; `200`-count / `100ms`-debounced / `60s`-watchdog pull-based offline drain; threshold-guarded (`< 5` -> up to `812`) pre-wire Signal pre-key upload (`NextPreKeyId` persisted in `SqliteDeviceStore`, `:memory:` anchor connection fixed in `SqliteRunner`); background post-`<success>` `<active/>` + pre-key sequence, offline-gated dirty-bit clearing, `515` immediate `IK` restart, and terminal `401`/`516`/`409` cleanup (`211` offline MSTest tests + Rust `wacore` suites + live `wss://web.whatsapp.com/ws/chat` probe).

### Phase 5 — AppState (`syncd`) + LTHash Engine (`src/WhatsApp.Protocol/AppState/` + `WhatsApp.Api`) ✅
* Implemented `LtHash.cs` (128B HKDF-SHA256 `"WhatsApp Patch Integrity"` LE uint16 pointwise add/subtract matching live `WALTHash` & `wacore-appstate`), `AppStateKeys.cs` (160B HKDF-SHA256 `"WhatsApp Mutation Keys"` 5x32B subkeys), `AppStateHasher.cs` (HMAC-SHA512 truncated 32B content MAC + HMAC-SHA256 snapshot/patch MAC + 32-bit `to64BitNetworkOrder` version encoding + `indexMode` double-subtraction guard), `AppStateMutationCodec.cs`, `AppStatePatchParser.cs`, `AppStateProcessor.cs`, and `SyncdCoordinator.cs` (external snapshot/mutation download via `IHttpClient` + `MediaCipher`, `has_more_patches` pagination, `PushNameSetting` persistence, `409` conflict absorb/rebuild, `SnapshotMacDiverged` automatic snapshot recovery). Verified against upstream `wacore-appstate`, live `web.whatsapp.com`, and bidirectional Rust↔C# interop (`221` offline C# MSTest tests passing).

### Phase 6 — Messaging, Media Pipeline & On-Demand PDO (`WhatsApp.Api` + `WhatsApp.Protocol`)
* Inbound `<message>` decryption (`pkmsg`/`msg`/`skmsg`/`DeviceSentMessage`), `LidPnCache`, retry receipts, `PLACEHOLDER_MESSAGE_RESEND` PDO fallback.
* Outbound 1:1 & group E2EE sending (`usync` device fan-out, SKDM distribution, `DeviceSentMessage`).
* Initial HistorySync (`Chunks 0..N`) + `PdoHistoryClient` (`ProtocolMessage(31)` -> `HistorySyncOnDemandRequest` correlated by `peerDataRequestSessionId`) + monotonic `active_message_ranges` interval stitching.

### Phase 7 — Headless Verification Harness (`apps/WhatsApp.Cli`)
* Prove end-to-end headless CLI flows before starting WinUI 3:
  1. Single-attempt QR pairing + initial bootstrap sync.
  2. Clean disconnect + cold-boot Noise IK reconnect (offline drain + `syncd`).
  3. Live 1:1 & group message send/receive.
  4. On-demand PDO (`syncType = 6`) historical backfill.
