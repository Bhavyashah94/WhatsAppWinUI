using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class FramingReportingAndRetryProtocolTests
{
    [TestMethod]
    public void MessageFraming_PhashV2_MatchesLiveWhatsAppWebVector()
    {
        // Verified directly on live web.whatsapp.com via WAWebPhashUtils.phashV2(
        //   ['15551234567:0@s.whatsapp.net', '15551234567:1@s.whatsapp.net', '100000000000101:2@lid'])
        Jid[] devices =
        [
            Jid.Parse("15551234567:0@s.whatsapp.net"),
            Jid.Parse("15551234567:1@s.whatsapp.net"),
            Jid.Parse("100000000000101:2@lid")
        ];

        Assert.AreEqual("2:Jxw0XT8D", MessageFraming.ComputeParticipantListHash(devices));
        // Input order must not change output
        Assert.AreEqual("2:Jxw0XT8D", MessageFraming.ComputeParticipantListHash(devices.Reverse().ToArray()));
    }

    [TestMethod]
    public void MessageFraming_PadUnpadV2_And_DeviceSentMessage_RoundTrip()
    {
        var original = new Message { Conversation = "Live WA Web V2 padding check" };
        byte[] padded = MessageFraming.EncodeAndPadMessage(original);

        byte padByte = padded[^1];
        Assert.IsTrue(padByte >= 1 && padByte <= 16);
        for (int i = padded.Length - padByte; i < padded.Length; i++)
        {
            Assert.AreEqual(padByte, padded[i]);
        }

        Message roundTripped = MessageFraming.DecodeAndUnpadMessage(padded);
        Assert.AreEqual("Live WA Web V2 padding check", roundTripped.Conversation);

        Message dsm = MessageFraming.WrapDeviceSentMessage(original, "15559990000@s.whatsapp.net");
        dsm.DeviceSentMessage.Phash = "2:Jxw0XT8D";
        Assert.AreEqual("15559990000@s.whatsapp.net", dsm.DeviceSentMessage.DestinationJid);
        Assert.AreEqual("2:Jxw0XT8D", dsm.DeviceSentMessage.Phash);
        Assert.AreEqual("Live WA Web V2 padding check", dsm.DeviceSentMessage.Message.Conversation);

        var secret32 = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();
        var withSecret = new Message
        {
            Conversation = "hello bot",
            MessageContextInfo = new MessageContextInfo
            {
                MessageSecret = Google.Protobuf.ByteString.CopyFrom(secret32),
                BotMetadata = new BotMetadata { PersonaId = "p-1" }
            }
        };
        Message wrappedWithSecret = MessageFraming.WrapDeviceSentMessage(withSecret, "867051314767696@bot");
        Assert.IsNull(wrappedWithSecret.DeviceSentMessage.Message.MessageContextInfo);
        Assert.IsNotNull(wrappedWithSecret.MessageContextInfo);
        CollectionAssert.AreEqual(secret32, wrappedWithSecret.MessageContextInfo.MessageSecret.ToByteArray());

        Message unwrapped = MessageFraming.UnwrapDeviceSentMessage(wrappedWithSecret);
        Assert.IsNotNull(unwrapped.MessageContextInfo);
        CollectionAssert.AreEqual(secret32, unwrapped.MessageContextInfo.MessageSecret.ToByteArray());
        Assert.AreEqual("p-1", unwrapped.MessageContextInfo.BotMetadata.PersonaId);
    }

    [TestMethod]
    public void ReportingToken_GeneratesDeterministicToken_ForWhitelistedFields()
    {
        var secret = new byte[32];
        for (int i = 0; i < 32; i++) secret[i] = (byte)(i + 1);

        var msgA = new Message
        {
            ImageMessage = new Message.Types.ImageMessage { Caption = "hello photo", Mimetype = "image/jpeg", DirectPath = "/v/path-1", FileLength = 100 }
        };
        var msgB = new Message
        {
            ImageMessage = new Message.Types.ImageMessage { Caption = "hello photo", Mimetype = "image/jpeg", DirectPath = "/v/path-1", FileLength = 999 }
        };

        string sender = "15551110000:0@s.whatsapp.net";
        string remote = "15552220000@s.whatsapp.net";

        ReportingTokenResult? resA = ReportingTokenGenerator.Generate(msgA, "MSG-1", sender, remote, secret);
        ReportingTokenResult? resB = ReportingTokenGenerator.Generate(msgB, "MSG-1", sender, remote, secret);

        // FileLength (field 5) is NOT whitelisted in ImageSubfields, while Caption (3), Mimetype (2), DirectPath (11) ARE!
        Assert.IsNotNull(resA);
        Assert.IsNotNull(resB);
        Assert.AreEqual(16, resA.ReportingToken16.Length);
        CollectionAssert.AreEqual(resA.ReportingToken16, resB.ReportingToken16);

        var msgDiffCaption = new Message
        {
            ImageMessage = new Message.Types.ImageMessage { Caption = "different", Mimetype = "image/jpeg", DirectPath = "/v/path-1", FileLength = 100 }
        };
        ReportingTokenResult? resC = ReportingTokenGenerator.Generate(msgDiffCaption, "MSG-1", sender, remote, secret);
        Assert.IsNotNull(resC);
        Assert.IsFalse(resA.ReportingToken16.AsSpan().SequenceEqual(resC.ReportingToken16));

        Message attached = ReportingTokenGenerator.AttachMessageContext(msgA, secret, 2);
        CollectionAssert.AreEqual(secret, attached.MessageContextInfo.MessageSecret.ToByteArray());
        Assert.AreEqual(2, attached.MessageContextInfo.ReportingTokenVersion);
    }

    [TestMethod]
    public void MediaRetryCrypto_RoundTrips_MatchesLiveWebCrypto_And_RejectsWrongStanzaId()
    {
        // Live web.whatsapp.com WebCrypto AES-256-GCM vector verified on pageId: 1
        byte[] vecMediaKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] vecIv = Enumerable.Range(0, 12).Select(i => (byte)(0xA0 + i)).ToArray();
        byte[] expectedLiveHex = Convert.FromHexString("8f6a8a50eb3120660e638bbf378adb164682d4a7a032df2605d1d94617");
        (byte[] vecCipher, byte[] vecOutIv) = MediaRetryCrypto.EncryptServerErrorReceipt(vecMediaKey, "LIVE-TEST-1", keepMediaKey: false, ivOverride: vecIv);
        CollectionAssert.AreEqual(vecIv, vecOutIv);
        CollectionAssert.AreEqual(expectedLiveHex, vecCipher);

        byte[] mediaKey = RandomNumberGenerator.GetBytes(32);
        string stanzaId = "MEDIA-RETRY-42";

        (byte[] reqCipher, byte[] reqIv) = MediaRetryCrypto.EncryptServerErrorReceipt(mediaKey, stanzaId);
        Assert.AreEqual(12, reqIv.Length);
        Assert.IsTrue(reqCipher.Length > 16);

        var serverResp = new MediaRetryNotification
        {
            StanzaId = stanzaId,
            DirectPath = "/v/t62.7118-24/refreshed.enc",
            Result = MediaRetryNotification.Types.ResultType.Success
        };
        byte[] notifIv = RandomNumberGenerator.GetBytes(12);
        byte[] key = MediaRetryCrypto.DeriveKey(mediaKey);
        byte[] notifCipher = new byte[serverResp.CalculateSize()];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(notifIv, serverResp.ToByteArray(), notifCipher, tag, System.Text.Encoding.UTF8.GetBytes(stanzaId));
        }
        byte[] combined = [.. notifCipher, .. tag];

        MediaRetryDecryptedResult decoded = MediaRetryCrypto.DecryptMediaRetryNotification(mediaKey, stanzaId, notifIv, combined);
        Assert.AreEqual(MediaRetryStatus.Success, decoded.Status);
        Assert.AreEqual("/v/t62.7118-24/refreshed.enc", decoded.DirectPath);

        Assert.ThrowsException<AuthenticationTagMismatchException>(() =>
            MediaRetryCrypto.DecryptMediaRetryNotification(mediaKey, "WRONG-ID", notifIv, combined));
    }

    [TestMethod]
    public void PreKeyIq_ParsePreKeysResponse_ValidatesCompanionAdvIdentity_And_Captures406()
    {
        var primaryId = Curve25519.GenerateKeyPair();
        var primarySpk = PreKeyManager.CreateSignedPreKey(1, primaryId);
        var comp1Id = Curve25519.GenerateKeyPair();
        var comp1Spk = PreKeyManager.CreateSignedPreKey(2, comp1Id);
        var comp2Id = Curve25519.GenerateKeyPair();
        var comp2Spk = PreKeyManager.CreateSignedPreKey(3, comp2Id);

        byte[] advDetails1 = new ADVDeviceIdentity { RawId = 100, Timestamp = 1700000000, KeyIndex = 1 }.ToByteArray();
        byte[] comp1AccountSig = XEdDSA.Sign(primaryId.PrivateKey, [6, 0, .. advDetails1, .. comp1Id.PublicKey]);
        byte[] comp1DevSig = XEdDSA.Sign(comp1Id.PrivateKey, [6, 1, .. advDetails1, .. comp1Id.PublicKey, .. primaryId.PublicKey]);
        byte[] comp1AdvBytes = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(advDetails1),
            AccountSignatureKey = ByteString.CopyFrom(primaryId.PublicKey),
            AccountSignature = ByteString.CopyFrom(comp1AccountSig),
            DeviceSignature = ByteString.CopyFrom(comp1DevSig)
        }.ToByteArray();

        // Companion 2 has bogus device signature -> must be dropped!
        byte[] comp2BadAdv = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(advDetails1),
            AccountSignatureKey = ByteString.CopyFrom(primaryId.PublicKey),
            AccountSignature = ByteString.CopyFrom(comp1AccountSig),
            DeviceSignature = ByteString.CopyFrom(new byte[64])
        }.ToByteArray();

        Node BuildUser(Jid jid, uint reg, KeyPair idKp, GeneratedSignedPreKey spk, uint otpkId, byte[] otpkPub, byte[]? advBytes = null)
        {
            var children = new List<Node>
            {
                new("registration", StanzaBuilder.EncodeUInt32BigEndian(reg)),
                new("type", new[] { PreKeyIq.DjbKeyType }),
                new("identity", idKp.PublicKey),
                new("key", new Node[] { new("id", StanzaBuilder.EncodeKeyId24(otpkId)), new("value", otpkPub) }),
                new("skey", new Node[] { new("id", StanzaBuilder.EncodeKeyId24(spk.Id)), new("value", spk.KeyPair.PublicKey), new("signature", spk.Signature) })
            };
            if (advBytes is not null)
            {
                children.Add(new Node("device-identity", advBytes));
            }
            return new Node("user", new Dictionary<string, NodeValue> { ["jid"] = jid }, children.ToArray());
        }

        Jid p0 = Jid.PnDevice("15550001111", 0);
        Jid c1 = Jid.PnDevice("15550001111", 1);
        Jid c2 = Jid.PnDevice("15550001111", 2);
        Jid c3Gone = Jid.PnDevice("15550001111", 3);

        Node iq = new("iq", new Dictionary<string, NodeValue> { ["id"] = "pk-1", ["type"] = "result" }, new Node[]
        {
            new("list", new Node[]
            {
                BuildUser(p0, 111, primaryId, primarySpk, 10, Curve25519.GenerateKeyPair().PublicKey),
                BuildUser(c1, 222, comp1Id, comp1Spk, 20, Curve25519.GenerateKeyPair().PublicKey, comp1AdvBytes),
                BuildUser(c2, 333, comp2Id, comp2Spk, 30, Curve25519.GenerateKeyPair().PublicKey, comp2BadAdv),
                new("user", new Dictionary<string, NodeValue> { ["jid"] = c3Gone }, new Node[]
                {
                    new("error", new Dictionary<string, NodeValue> { ["code"] = "406", ["text"] = "not-acceptable" })
                })
            })
        });

        PreKeyFetchOutcome outcome = PreKeyIq.ParsePreKeysResponse(iq);
        Assert.IsTrue(outcome.Bundles.ContainsKey(p0));
        Assert.IsTrue(outcome.Bundles.ContainsKey(c1));
        Assert.IsFalse(outcome.Bundles.ContainsKey(c2)); // Invalid companion ADV dropped
        Assert.AreEqual(1, outcome.RejectedDevices.Count);
        Assert.AreEqual(406, outcome.RejectedDevices[0].Code);
        Assert.AreEqual(c3Gone, outcome.RejectedDevices[0].DeviceJid);
    }

    [TestMethod]
    public void MessageStanzas_And_RetryReceipts_BuildAndParseCleanly()
    {
        var msg = new Message
        {
            ReactionMessage = new Message.Types.ReactionMessage { Text = "🔥" }
        };
        Assert.AreEqual("reaction", MessageStanzas.ClassifyStanzaType(msg));
        Assert.IsNull(MessageStanzas.ClassifyMediaType(msg));
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(msg));

        var pdo = MessageStanzas.BuildPlaceholderResendMessage([new MessageKey { Id = "PLACEHOLDER-1", RemoteJid = "1555@s.whatsapp.net" }]);
        Assert.AreEqual(
            Message.Types.PeerDataOperationRequestType.PlaceholderMessageResend,
            pdo.ProtocolMessage.PeerDataOperationRequestMessage.PeerDataOperationRequestType);
        Assert.AreEqual("PLACEHOLDER-1", pdo.ProtocolMessage.PeerDataOperationRequestMessage.PlaceholderMessageResendRequest[0].MessageKey.Id);

        var primaryAcc = Curve25519.GenerateKeyPair();
        var idKp = Curve25519.GenerateKeyPair();
        var spk = PreKeyManager.CreateSignedPreKey(1, idKp);
        byte[] advDetails = new ADVDeviceIdentity { RawId = 9001, Timestamp = 1700000100, KeyIndex = 1 }.ToByteArray();
        byte[] advAccSig = XEdDSA.Sign(primaryAcc.PrivateKey, [6, 0, .. advDetails, .. idKp.PublicKey]);
        byte[] advDevSig = XEdDSA.Sign(idKp.PrivateKey, [6, 1, .. advDetails, .. idKp.PublicKey, .. primaryAcc.PublicKey]);
        byte[] validAdv = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(advDetails),
            AccountSignatureKey = ByteString.CopyFrom(primaryAcc.PublicKey),
            AccountSignature = ByteString.CopyFrom(advAccSig),
            DeviceSignature = ByteString.CopyFrom(advDevSig)
        }.ToByteArray();

        var bundle = new RetryPreKeyUploadBundle(
            idKp.PublicKey, 5, Curve25519.GenerateKeyPair().PublicKey, spk.Id, spk.KeyPair.PublicKey, spk.Signature, validAdv);
        Node retryReceipt = RetryAndMediaRetryStanzas.BuildRetryReceipt(
            "MSG-RETRY-1",
            Jid.Parse("120363001@g.us"),
            1700000100,
            retryCount: 2,
            registrationId: 9001,
            RetryReason.BadMac,
            participant: Jid.PnDevice("15551234567", 1),
            localKeys: bundle,
            hideFailedDecrypt: true);

        ParsedReceiptStanza? parsed = RetryAndMediaRetryStanzas.ParseIncomingReceipt(new Node(
            "receipt",
            new Dictionary<string, NodeValue>
            {
                ["id"] = "MSG-RETRY-1",
                ["from"] = Jid.Parse("120363001@g.us"),
                ["participant"] = Jid.PnDevice("15551234567", 1),
                ["type"] = "retry"
            },
            retryReceipt.Content));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(2, parsed.RetryCount);
        Assert.AreEqual(9001u, parsed.RegistrationId);
        Assert.IsNotNull(parsed.RetryKeyBundle);
        Assert.AreEqual(5u, parsed.RetryKeyBundle.PreKeyId);

        // Decode live WAWap.encodeStanza wire bytes from web.whatsapp.com
        Node liveDm = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString(
            "00f8081308fc09444d2d4c4956452d3111faff8615552220000f030438f802f8026bf801f804110cfaff8615552220000f03f801f8061d51450453fc0401020304f802e6fc0405060708"))).ReadNode();
        Assert.AreEqual("message", liveDm.Tag);
        Assert.AreEqual("DM-LIVE-1", liveDm.GetAttr("id"));
        Assert.AreEqual("pkmsg", liveDm.GetChild("participants")!.GetChild("to")!.GetChild("enc")!.GetAttr("type"));

        Node liveRetry = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString(
            "00f8080708fc03522d3111faff8615551110000f0304ec0bf804f80bec0b414508fc03522d311aff0517000001005155a8e3f802adfc0400002329f803ed7d6a55f802ef1ef802f80204fc0105f8029cfc200101010101010101010101010101010101010101010101010101010101010101"))).ReadNode();
        Assert.AreEqual("receipt", liveRetry.Tag);
        Assert.AreEqual("retry", liveRetry.GetAttr("type"));
        Assert.AreEqual("15551110000@s.whatsapp.net", liveRetry.GetAttr("to"));
        // Wrap as server-delivered inbound receipt (`from` instead of outbound `to`)
        ParsedReceiptStanza? parsedLiveRetry = RetryAndMediaRetryStanzas.ParseIncomingReceipt(new Node(
            "receipt",
            new Dictionary<string, NodeValue> { ["id"] = liveRetry.GetAttr("id")!, ["from"] = Jid.Parse(liveRetry.GetAttr("to")!), ["type"] = "retry" },
            liveRetry.Content));
        Assert.IsNotNull(parsedLiveRetry);
        Assert.AreEqual(2, parsedLiveRetry.RetryCount);
        Assert.AreEqual(9001u, parsedLiveRetry.RegistrationId);

        Node liveMediaErr = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString(
            "00f8080708fc034d2d3111fa000304ed91f802f802cbf802f802fc0b656e635f7061796c6f6164fc03010203f802ee3afc0c040404040404040404040404f805fc03726d720cfaff8615552220000f03fc0766726f6d5f6d65ec0f"))).ReadNode();
        Assert.AreEqual("receipt", liveMediaErr.Tag);
        Assert.AreEqual("server-error", liveMediaErr.GetAttr("type"));
        Assert.AreEqual("s.whatsapp.net", liveMediaErr.GetAttr("to"));
        ParsedMediaRetryNotification? parsedLiveMediaErr = RetryAndMediaRetryStanzas.ParseMediaRetryNotification(new Node(
            "notification",
            new Dictionary<string, NodeValue> { ["id"] = liveMediaErr.GetAttr("id")!, ["from"] = Jid.Parse(liveMediaErr.GetAttr("to")!), ["type"] = "mediaretry" },
            liveMediaErr.Content));
        Assert.IsNotNull(parsedLiveMediaErr);
        Assert.AreEqual("15552220000@s.whatsapp.net", parsedLiveMediaErr.RmrJid?.ToString());
        Assert.AreEqual(3, parsedLiveMediaErr.EncPayload.Length);
        Assert.AreEqual(12, parsedLiveMediaErr.EncIv12.Length);
    }

    [TestMethod]
    public void InteractiveMessages_ParseSerializeClassifyAndBuildResponses_Work()
    {
        var listMsg = new Message
        {
            ListMessage = new Message.Types.ListMessage
            {
                Description = "How would you like to receive your bill?",
                ButtonText = "Options",
                ListType = Message.Types.ListMessage.Types.ListType.SingleSelect,
                Sections =
                {
                    new Message.Types.ListMessage.Types.Section
                    {
                        Rows =
                        {
                            new Message.Types.ListMessage.Types.Row { RowId = "Soft Copy (Digital Bill)", Title = "Soft Copy (Digital Bill)" },
                            new Message.Types.ListMessage.Types.Row { RowId = "Hard Copy (Printed Bill)", Title = "Hard Copy (Printed Bill)" }
                        }
                    }
                }
            }
        };

        InteractiveMessageContent? parsedList = InteractiveMessageParser.Parse(listMsg);
        Assert.IsNotNull(parsedList);
        Assert.AreEqual("list", parsedList.Kind);
        Assert.AreEqual("Options", parsedList.ButtonText);
        Assert.AreEqual(2, parsedList.Sections![0].Rows.Count);
        Assert.AreEqual("Soft Copy (Digital Bill)", parsedList.Sections[0].Rows[0].Id);
        InteractiveMessageContent? roundTripped = InteractiveMessageParser.TryFromJson(InteractiveMessageParser.Serialize(parsedList));
        Assert.AreEqual(2, roundTripped?.Sections?[0].Rows.Count);
        Assert.AreEqual("media", MessageStanzas.ClassifyStanzaType(listMsg));
        Assert.AreEqual("list", MessageStanzas.ClassifyMediaType(listMsg));

        var ctaMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                Header = new Message.Types.InteractiveMessage.Types.Header
                {
                    HasMediaAttachment = true,
                    DocumentMessage = new Message.Types.DocumentMessage
                    {
                        Title = "153471427_NOV-26",
                        Mimetype = "application/pdf",
                        DirectPath = "/v/doc/123"
                    }
                },
                Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Adani Electricity Bill" },
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "cta_url",
                            ButtonParamsJson = """{"display_text":"Pay Now","url":"https://example.com/pay"}"""
                        }
                    }
                }
            }
        };

        InteractiveMessageContent? parsedCta = InteractiveMessageParser.Parse(ctaMsg);
        Assert.IsNotNull(parsedCta);
        Assert.AreEqual("interactive", parsedCta.Kind);
        Assert.AreEqual("Pay Now", parsedCta.Buttons![0].DisplayText);
        Assert.AreEqual("https://example.com/pay", parsedCta.Buttons[0].Url);
        Assert.IsNotNull(InteractiveMessageParser.ExtractEmbeddedMediaHeader(ctaMsg)?.DocumentMessage);

        var tplMsg = new Message
        {
            TemplateMessage = new Message.Types.TemplateMessage
            {
                HydratedFourRowTemplate = new Message.Types.TemplateMessage.Types.HydratedFourRowTemplate
                {
                    HydratedContentText = "Confirm?",
                    HydratedButtons =
                    {
                        new HydratedTemplateButton
                        {
                            Index = 0,
                            QuickReplyButton = new HydratedTemplateButton.Types.HydratedQuickReplyButton { Id = "qr_yes", DisplayText = "Yes" }
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedTpl = InteractiveMessageParser.Parse(tplMsg);
        Assert.AreEqual("template", parsedTpl?.Kind);
        Assert.AreEqual("qr_yes", parsedTpl?.Buttons?[0].Id);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(tplMsg));

        ContextInfo quoteCtx = RichMessageBuilders.BuildQuoteContextInfo(new MessageKey { Id = "LIST-1" }, listMsg)!;
        Message lr = RichMessageBuilders.BuildListResponseMessage("Soft Copy (Digital Bill)", "Soft Copy (Digital Bill)", contextInfo: quoteCtx);
        Assert.AreEqual("media", MessageStanzas.ClassifyStanzaType(lr));
        Assert.AreEqual("list_response", MessageStanzas.ClassifyMediaType(lr));
        Assert.AreEqual("LIST-1", lr.ListResponseMessage.ContextInfo.StanzaId);

        Message br = RichMessageBuilders.BuildButtonsResponseMessage("btn_1", "Yes", quoteCtx);
        Assert.AreEqual("buttons_response", MessageStanzas.ClassifyMediaType(br));

        Message tbr = RichMessageBuilders.BuildTemplateButtonReplyMessage("qr_yes", "Yes", 0, contextInfo: quoteCtx);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(tbr));
        Assert.IsNull(MessageStanzas.ClassifyMediaType(tbr));

        Message nfr = RichMessageBuilders.BuildInteractiveNativeFlowResponseMessage("galaxy_message", """{"flow_token":"t1"}""", "Submitted", contextInfo: quoteCtx);
        Assert.AreEqual("native_flow_response", MessageStanzas.ClassifyMediaType(nfr));
        Node? bizNode = MessageStanzas.InferBizNode(nfr);
        Assert.IsNotNull(bizNode);
        Assert.AreEqual("galaxy_message", bizNode.GetAttr("native_flow_name"));

        // CarouselMessage + cta_copy + native_flow quick_reply + order review_and_pay + FourRowTemplate
        var carousel = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Festival Deals" },
                CarouselMessage = new Message.Types.InteractiveMessage.Types.CarouselMessage
                {
                    Cards =
                    {
                        new Message.Types.InteractiveMessage
                        {
                            Header = new Message.Types.InteractiveMessage.Types.Header
                            {
                                ImageMessage = new Message.Types.ImageMessage { DirectPath = "/v/card1.jpg", Mimetype = "image/jpeg" }
                            },
                            Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Card 1" },
                            NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                            {
                                Buttons =
                                {
                                    new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                                    {
                                        Name = "cta_copy",
                                        ButtonParamsJson = """{"display_text":"Copy offer code","copy_code":"LPTROUSEREDIT10"}"""
                                    }
                                }
                            }
                        },
                        new Message.Types.InteractiveMessage
                        {
                            Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Card 2" },
                            NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                            {
                                Buttons =
                                {
                                    new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                                    {
                                        Name = "quick_reply",
                                        ButtonParamsJson = """{"display_text":"STOP","id":"0"}"""
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedCarousel = InteractiveMessageParser.Parse(carousel);
        Assert.AreEqual("carousel", parsedCarousel?.Kind);
        Assert.AreEqual(2, parsedCarousel?.Cards?.Count);
        Assert.AreEqual("LPTROUSEREDIT10", parsedCarousel?.Buttons?[0].CopyCode);
        Assert.AreEqual("0", parsedCarousel?.Buttons?[1].Id);
        Assert.AreEqual(1u, parsedCarousel?.Buttons?[1].CardIndex);
        Assert.IsNotNull(InteractiveMessageParser.ExtractEmbeddedMediaHeader(carousel)?.ImageMessage);

        var orderMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Mumbai Metro Payment" },
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "review_and_pay",
                            ButtonParamsJson = """{"currency":"INR","total_amount":{"value":2000,"offset":100},"reference_id":"EZV123","order":{"status":"completed","description":"Your Payment is received."},"payment_settings":[{"type":"upi_intent_link","upi_intent_link":{"merchant_details":{"intent_link":"upi://pay?pa=mmmocl@icici"}}}]}"""
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedOrder = InteractiveMessageParser.Parse(orderMsg);
        Assert.AreEqual("EZV123", parsedOrder?.Order?.ReferenceId);
        Assert.AreEqual("INR", parsedOrder?.Order?.Currency);
        Assert.AreEqual(20m, parsedOrder?.Order?.TotalAmount);
        Assert.AreEqual("completed", parsedOrder?.Order?.Status);
        Assert.AreEqual("upi://pay?pa=mmmocl@icici", parsedOrder?.Order?.PaymentUrl);
        Assert.AreEqual("View details", parsedOrder?.Buttons?[0].DisplayText);

        var fourRow = new Message
        {
            TemplateMessage = new Message.Types.TemplateMessage
            {
                FourRowTemplate = new Message.Types.TemplateMessage.Types.FourRowTemplate
                {
                    Content = new Message.Types.HighlyStructuredMessage { Params = { "Hello!", "Your bill is ready." } },
                    Buttons =
                    {
                        new TemplateButton
                        {
                            Index = 0,
                            QuickReplyButton = new TemplateButton.Types.QuickReplyButton
                            {
                                Id = "qr_ok",
                                DisplayText = new Message.Types.HighlyStructuredMessage { Params = { "OK" } }
                            }
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedFourRow = InteractiveMessageParser.Parse(fourRow);
        Assert.AreEqual("template", parsedFourRow?.Kind);
        Assert.AreEqual("Hello! Your bill is ready.", parsedFourRow?.Body);
        Assert.AreEqual("qr_ok", parsedFourRow?.Buttons?[0].Id);
        Assert.AreEqual("OK", parsedFourRow?.Buttons?[0].DisplayText);
    }
}
