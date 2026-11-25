# Live `web.whatsapp.com` vs `reference/whatsapp-rust` Differential Log

> **Maintained across every implementation phase** via live `web.whatsapp.com` Chrome DevTools MCP inspection (`WAWebProtobufs*`, `WAWapDict`, `WAWebABPropsConfigs`, `WAWebSyncd*`, `WAWebTrustedContactsUtils`, `WhatsAppVoipWasmCallbacks`) and `reference/whatsapp-rust`.

---

## 1. Protobuf Schemas (`WAWebProtobufs*` vs `reference/whatsapp-rust/waproto`)

Exhaustive automated diff across all 9 live Web `.pb` modules (`WAWebProtobufsE2E.pb`, `WAWebProtobufSyncAction.pb`, `WAWebProtobufsHistorySync.pb`, `WAWebProtobufsWa6.pb`, `WAWebProtobufsDeviceCapabilities.pb`, `WAWebProtobufsServerSync.pb`, `WAWebProtobufsCompanionReg.pb`, `WAWebProtobufsAdv.pb`, `WAWebProtobufsMmsRetry.pb`) found **4 messages and 14 fields/enum values present on live `web.whatsapp.com` that are absent in `reference/whatsapp-rust/waproto/src/whatsapp.proto`**:

| Message / Type | Tag | Live `web.whatsapp.com` Addition | Purpose | Synced to `WhatsApp.WaProto` |
|---|---:|---|---|---|
| `Message.ProtocolMessage` | `42` | `messageEditDisplayType` (`MessageEditDisplayType`: `DEFAULT=0`, `LINK_PREVIEW_ONLY=1`) | Controls whether message edits update full text or link preview only | Yes |
| `Message.ProtocolMessage` | `43` | `draftMessageSync` (`DraftMessageSync`) + `Type.DRAFT_MESSAGE_SYNC = 45` | Cross-device unsent chat draft sync (`SET`/`CLEAR`, `ClearReason`) | Yes |
| `HistorySync.Conversation` | `70` | `unreadReplyToMyMessageCount` (`uint32`) | Tracks unread replies quoting the user's messages | Yes |
| `HistorySync.Conversation` | `71` | `draft` (`ConversationDraft`) | Persists chat draft (`Message`, `editTimestampMs`, `editorDeviceId`) in history chunks | Yes |
| `DeviceProps.HistorySyncConfig` | `29` | `supportDraftSync` (`bool`) | Negotiates cross-device draft sync during initial device linking | Yes |
| `ClientPairingProps` | `10` | `isCompanionPairingProgressUiEnabled` (`bool`) | Controls linking bootstrap progress UI telemetry | Yes |
| `SyncActionValue` | `102` | `businessFolderChatPreferenceAction` (`BusinessFolderChatPreferenceAction`) | Business Inbox folder routing (`AUTOMATIC`/`INBOX`/`BUSINESS`) | Yes |
| `InteractiveAnnotation` + `StatusPixKey` | `9` | `pixKey` (`StatusPixKey`: `key`, `KeyType` `CPF`/`CNPJ`/`PHONE`/`EMAIL`/`EVP`, `displayName`) | Interactive Brazilian PIX payment key sticker annotations in Statuses | Yes |
| `DeviceCapabilities` | `15` | `recentConversationsOnDemand` (`RecentConversationsOnDemand.supported`) | Recent-conversation on-demand history sync capability flag | Yes |
| `DeviceCapabilities` | `16` | `aiMuseJarvisGzip` (`AiMuseJarvisGzip.supportVersion`) | Gzip-compressed Meta AI / Muse Jarvis payload capability | Yes |
| `DeviceCapabilities` | `17` | `draftSync` (`DraftSync.version`) | Draft sync protocol version capability | Yes |
| `Message.PollCreationMessage.Option` | `3` | `optionDatetime` (`int64`) | Date/time poll option metadata | Yes |
| `Message.InitialSecurityNotificationSettingSync` | `2` | `initialAppTheme` (`int32`) | Syncs initial theme setting on primary security notification sync | Yes |
| `ServerErrorReceipt` | `2` | `keepMediaKey` (`bool`) | Preserves original `mediaKey` across media retry server errors | Yes |
| `SyncActionValue.ContactAction` / `LidContactAction` | `8` / `5` | `nonE2EeType` (`int32`) | Identifies non-E2EE / 3P-bridged interoperable contact threads | Yes |

---

## 2. Cryptography, Wire Framing & Binary XML Differences

| Area | Earlier Assumption / Spec | Verified Ground Truth (Live Web + Rust) | Status in `WhatsAppWinUI` |
|---|---|---|---|
| **AppState `LtHash` Hash Function** | Earlier plans noted `"128B HKDF-SHA512"` | Live `WALTHash` (`WAWebSyncdLTHash`) & `wacore-appstate` BOTH use **HKDF-SHA256** (`"WhatsApp Patch Integrity"`) over LE `uint16` lanes; **HMAC-SHA512** (truncated 32B) is used only for `generateContentMac`. | Fixed & verified (`LtHash.cs`, `AppStateHasher.cs`) |
| **AppState Version Encoding** | Full 64-bit big-endian integer | Live WA Web `to64BitNetworkOrder` masks/wraps version to **low 32 bits big-endian at bytes `[4..8]`** (`version & 0xFFFFFFFF`). | Matches (`AppStateHasher.EncodeVersionNetworkOrder`) |
| **AppState `ValueMac` Short-Slice Wrap** | Simple `blob[len-32..]` | Live WA Web JS `slice(len - 32)` double-negative behavior on short blobs (`len < 32` -> `max(0, 2*len - 32)`). | Matches (`AppStateHasher.GetValueMacStartOffset`) |
| **Session Storage Format (`SignalStateCodec`)** | Bare `SessionStructure` | `whatsapp-rust` wraps `SessionStructure` in `RecordStructure { CurrentSession }` and strips `0x05` key prefix cleanly. | Fixed & verified (`SignalStateCodec.cs`) |
| **Binary XML Dictionary (`tokens.json`)** | Static copy of tokens | Verified 100% against live `WAWapDict` (`DICT_VERSION = 3`): `235` single-byte tokens (`1..235`) + all `4 × 256` (`1024`) double-byte tokens match identically. | Verified (`tokens.json`) |
| **JID Domains (`Jid.cs`)** | Core `@s.whatsapp.net`, `@g.us`, `@lid` | Live `WAWap` defines `7` domains including `hosted`, `hosted.lid`, `newsletter`, `call`, `status@broadcast`. | All `7` supported (`Jid.cs`) |

---

## 3. Protocol Capabilities Present on Live `web.whatsapp.com` Ahead of `whatsapp-rust`

Live inspection of `WAWebABPropsConfigs` (`3,068` active AB prop configs), `WAWebUsernameUtils`, `WhatsAppVoipWasmWorkerCompatibleCallbacks`, and `WAWebProtobufs*` highlighted capabilities that are absent or partial in `reference/whatsapp-rust` and explicitly scheduled into Phases 6–10 of [MASTER_PLAN.md](./MASTER_PLAN.md):

1. **Post-Quantum Noise & PreKeys (`noise_pq_mode`, `pq_*` props `21198`/`21200`/`21201`/`24160`/`35210`/`35305`):**
   - `HandshakeMessage.ClientHello` / `ClientFinish` carry `HandshakePqMode`, `extendedEphemeral`, `extendedCiphertext`, `paddedBytes`, and `simulateXxkemFs`. `whatsapp-rust`'s `NoiseState` does not negotiate PQ XXKEM/IKKEM yet.
2. **Full `@username` Identity & PN-Sharing Flows (`WAWebUsernameUtils` + `username_*` AB props):**
   - Live web exposes `getLIDByUsername`, `generateUsernameKeySuggestion`, `username_key_regex` (`"^([0-9]{4}|[A-Z0-9]{6})$"`), `mex_usync_username_query` (`8421`), `username_mex_account_sync_enabled` (`8763`), and companion PN-sharing gating (`username_pn_sharing_*`). `whatsapp-rust` only reads the `peer_recipient_username` stanza attribute. *(Target: Phase 6C)*
3. **Syncd Coex V2 & Post-ACK Key Persistence (`enable_syncd_coex_v2` `31810`, `enable_syncd_write_coex_v2` `37465`, `wa_web_enable_syncd_key_persistence_only_after_server_ack` `27069`, `web_syncd_batched_orphan_apply` `41877`):**
   - New WhatsApp Web syncd key persistence ordering (defer outbound key state updates until server ACK) and batched orphan mutation handling. *(Target: Phase 6B/7A)*
4. **Latest E2EE Rich Content Variants (`Message` tags `110..141`):**
   - `spoilerMessage` (`118`), `conditionalRevealMessage` (`120`), `pollCreationMessageV5`/`V6` (`111`/`119`), `pollAddOptionMessage` (`121`), `eventInviteMessage` (`122`), `musicMessage` (`129`), `groupStatusMessageV2` (`103`), `newsletterAdminProfileMessage` (`116`), `newsletterScheduledMessage` (`132`), `instantImageMessage`/`instantVideoMessage` (`135`/`140`), and `DraftMessageSync` (`ProtocolMessage` `43`). `whatsapp-rust` omits or routes several of these as `Unknown`. *(Target: Phases 7B, 8, 9)*
5. **WebTransport & Hybrid VoIP Bridge (`WhatsAppVoipWasmWorkerCompatibleCallbacks`, `enable_web_voip_webtransport*` `29764`/`33539`, `win_hybrid_voip_bridge` `44379`):**
   - Live WhatsApp Web VoIP exposes `onSignalingXmpp`, `onCallEvent`, `sendDataToRelay`, `dataChannelStateCallback`, `cryptoHkdfExtractWithSaltAndExpand`, `hmacSha256KeyGenerator`, and BWE/ML audio/video pipelines over WebTransport/DTLS-SRTP. *(Target: Phase 10)*
6. **Live Web Verified Behavior Matching Both Specs (`Phase 6B`):**
   - `PairCodeCrypto` (`PBKDF2-SHA256` `131,072` iterations + `AES-256-CTR`) verified byte-for-byte against Chrome `crypto.subtle` on `web.whatsapp.com`.
   - Live `WAWap.encodeStanzaWithoutHeader` wire outputs for `link_code_companion_reg` (`stage="companion_hello"`, `[0x00]` nonce, `"Chrome (Windows)"`), `<media_conn>`, `<props>`, and `<tokens type="trusted_contact">` verified 1:1 with `WhatsApp.Protocol`.
   - `TcTokenProtocol` bucket math verified 1:1 against `WAWebTrustedContactsUtils` (`tokenExpirationCutoff`, `isTokenExpired`, `shouldSendNewToken`) and live `tctoken_*` AB prop defaults (`865`/`996` = `604800`, `909`/`997` = `4`).

7. **Live Web Additions & Differences Discovered During `Phase 6C` (`USync`, Avatars, `@username`, Notifications):**
   - **USync Hybrid Architecture (`WAWebUsync` vs `WAWebMexUsync`):** Live WA Web keeps XMPP `<iq xmlns="usync">` for `devices` (`v2`, `device_hash`/`ts`/`expected_ts`), `lid`, `contact` (`addressing_mode="lid"`, `<contact username="..." pin="..." lid="..."/>`), `business` (`verified_name` + `profile`), `picture`, `status`, `disappearing_mode`, `username`, `text_status`, `feature`, and `bot` (`persona_id`), AND additionally runs `WAWebMexUsyncQuery.graphql` (`xwa2_fetch_wa_users` with `include_country_code`, `include_orgs` -> `XWA2Org`, `include_username`). `reference/whatsapp-rust` implements XMPP USync only. Both live XMPP `WAWebUsync.$3()` wire hex captures and `UsyncProtocol` match 1:1.
   - **Profile Pictures (`WASmaxProfilePictureGetRPC` vs `wacore/src/iq/contacts.rs`):** Live WA Web extends `w:profile:picture` `<picture>` beyond `preview` / `image` to support **`cover`** (`ENUM_COVER_IMAGE_PREVIEW`), optional **`size`** integer attribute, **`has_staging`** response attribute, **`query="blob"`** inline binary blobs, and **`<avatar>`** URL child arrays (`parseGetResponseSuccessAvatarURLs`) in addition to standard URL/direct_path responses (`ProfilePictureIq` now handles all of them).
   - **ADV Key-Index & Hosted Account Verification (`WAWebHandleAdvDeviceNotificationApi` / `WAWebAdvSignatureConstants`):** Live Web verifies `ADVSignedKeyIndexList` via `[0x06, 0x02] || details` (`ADV_PREFIX_KEY_INDEX_LIST_ACCOUNT_SIGNATURE`) and explicitly clears device records on `ADVEncryptionType` mismatch between `HOSTED` (Coex) and `E2EE` (`PairingCrypto.VerifyKeyIndexList` + `VerifyHostedKeyIndexList`).
   - **Contact Hash Function (`WAWebApiContact.getContactHash`):** Verified live on `web.whatsapp.com` (`"100000000000001@lid"` -> `"s7oK"`), identical to `MD5(user + "WA_ADD_NOTIF")[0..3]` Base64 (`ContactHash.ComputeBase64`).
   - **Additional Live Notification Handlers (`WAWebHandle*Notification` / MEX):** Live `web.whatsapp.com` dispatches newer MEX/notification handlers absent from Rust's `NotificationType` dispatch table, including `WAWebMexLidChangeNotificationHandler`, `WAWebUsernameMexNotificationHandlers`, `WAWebHandleDeviceSwitchingNotification`, `WAWebAccountLinkingNotificationHandler`, `WAWebShortcakeLinkingHandleNotification`, `WAWebHandleBrigadingUpdateNotification`, and `WAWebVoipBridgeNotificationHandlers` (cleanly surfaced as typed `MexNotificationEvent`s).
