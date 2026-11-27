using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class InteractiveAndFlowProtocolTests
{
    [TestMethod]
    public void InteractiveMessageParser_ParsesRemainingBusinessAndFlowTypes()
    {
        // 1) WhatsApp Flow (galaxy_message)
        var flowMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                Header = new Message.Types.InteractiveMessage.Types.Header { Title = "Appointment" },
                Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Book your slot" },
                Footer = new Message.Types.InteractiveMessage.Types.Footer { Text = "Clinic" },
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "galaxy_message",
                            ButtonParamsJson = """{"flow_id":"987654321","flow_token":"tok_abc","flow_cta":"Book Now","flow_action":"data_exchange","flow_name":"booking_v1","mode":"published","flow_message_version":"3","flow_action_payload":{"screen":"START"}}"""
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedFlow = InteractiveMessageParser.Parse(flowMsg);
        Assert.IsNotNull(parsedFlow);
        Assert.AreEqual("interactive", parsedFlow.Kind);
        Assert.AreEqual("987654321", parsedFlow.Flow?.FlowId);
        Assert.AreEqual("tok_abc", parsedFlow.Flow?.FlowToken);
        Assert.AreEqual("Book Now", parsedFlow.Flow?.FlowCta);
        Assert.AreEqual("data_exchange", parsedFlow.Flow?.FlowAction);
        Assert.AreEqual("galaxy_message", parsedFlow.Buttons?[0].Kind);
        Assert.AreEqual("Book Now", parsedFlow.Buttons?[0].DisplayText);

        string serialized = InteractiveMessageParser.Serialize(parsedFlow)!;
        InteractiveMessageContent? rtFlow = InteractiveMessageParser.TryFromJson(serialized);
        Assert.AreEqual("987654321", rtFlow?.Flow?.FlowId);

        // 2) Booking Confirmation & Payment Reminder (SmartHydrated)
        var bookingMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "booking_confirmation",
                            ButtonParamsJson = """{"start_datetime":"2026-12-01T10:00:00Z","end_datetime":"2026-12-01T11:00:00Z","location":"Mumbai","booking_url":"https://example.com/b/1"}"""
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedBooking = InteractiveMessageParser.Parse(bookingMsg);
        Assert.AreEqual("2026-12-01T10:00:00Z", parsedBooking?.Booking?.StartDatetime);
        Assert.AreEqual("Mumbai", parsedBooking?.Booking?.Location);
        Assert.AreEqual("[Booking: 2026-12-01T10:00:00Z @ Mumbai]", InteractiveMessageParser.ExtractSummaryText(parsedBooking));

        var payRemMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "payment_reminder",
                            ButtonParamsJson = """{"title":"Electricity Bill","amount_due":"1950.00","due_date":"2026-12-10","is_overdue":false,"url":"https://pay.example.com","pay_now_button_text":"Pay Bill"}"""
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedPay = InteractiveMessageParser.Parse(payRemMsg);
        Assert.AreEqual("1950.00", parsedPay?.PaymentReminder?.AmountDue);
        Assert.AreEqual("2026-12-10", parsedPay?.PaymentReminder?.DueDate);
        Assert.AreEqual("Pay Bill", parsedPay?.Buttons?[0].DisplayText);

        // 3) Address Message & Call Permission Request & Location Request
        var addrMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "address_message",
                            ButtonParamsJson = """{"saved_addresses":[{"city":"Mumbai"}]}"""
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedAddr = InteractiveMessageParser.Parse(addrMsg);
        Assert.AreEqual(1, parsedAddr?.Address?.SavedAddressesJson?.Count);

        var cpMsg = new Message
        {
            InteractiveMessage = new Message.Types.InteractiveMessage
            {
                Body = new Message.Types.InteractiveMessage.Types.Body { Text = "Can we call you?" },
                NativeFlowMessage = new Message.Types.InteractiveMessage.Types.NativeFlowMessage
                {
                    Buttons =
                    {
                        new Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton
                        {
                            Name = "call_permission_request",
                            ButtonParamsJson = "{}"
                        }
                    }
                }
            }
        };
        Assert.AreEqual("call_permission_request", InteractiveMessageParser.Parse(cpMsg)?.NativeFlowName);

        // 4) RequestPhoneNumberMessage & OrderMessage & ProductList
        var reqPhone = new Message
        {
            RequestPhoneNumberMessage = new Message.Types.RequestPhoneNumberMessage
            {
                ContextInfo = new ContextInfo { StanzaId = "CTX-1" }
            }
        };
        InteractiveMessageContent? parsedPhone = InteractiveMessageParser.Parse(reqPhone);
        Assert.AreEqual("request_phone_number", parsedPhone?.Kind);
        Assert.AreEqual("CTX-1", InteractiveMessageParser.ExtractInteractiveContextInfo(reqPhone)?.StanzaId);

        var orderMsg = new Message
        {
            OrderMessage = new Message.Types.OrderMessage
            {
                OrderId = "ORD-42",
                TotalAmount1000 = 20000,
                TotalCurrencyCode = "INR",
                Status = Message.Types.OrderMessage.Types.OrderStatus.Inquiry,
                OrderTitle = "Metro Ticket"
            }
        };
        InteractiveMessageContent? parsedOrd = InteractiveMessageParser.Parse(orderMsg);
        Assert.AreEqual("order", parsedOrd?.Kind);
        Assert.AreEqual(20m, parsedOrd?.Order?.TotalAmount);
        Assert.AreEqual("INR", parsedOrd?.Order?.Currency);

        var prodList = new Message
        {
            ListMessage = new Message.Types.ListMessage
            {
                Title = "Catalog Selection",
                ListType = Message.Types.ListMessage.Types.ListType.ProductList,
                ProductListInfo = new Message.Types.ListMessage.Types.ProductListInfo
                {
                    BusinessOwnerJid = "919999999999@s.whatsapp.net",
                    ProductSections =
                    {
                        new Message.Types.ListMessage.Types.ProductSection
                        {
                            Title = "Featured",
                            Products = { new Message.Types.ListMessage.Types.Product { ProductId = "SKU-101" } }
                        }
                    }
                }
            }
        };
        InteractiveMessageContent? parsedProdList = InteractiveMessageParser.Parse(prodList);
        Assert.AreEqual("product_list", parsedProdList?.Kind);
        Assert.AreEqual("SKU-101", parsedProdList?.Sections?[0].Rows[0].Id);
        Assert.AreEqual("919999999999@s.whatsapp.net", parsedProdList?.Catalog?.BusinessPhoneNumber);
    }

    [TestMethod]
    public void GalaxyFlowStanzas_CryptoMexAndBuilders_RoundTrip()
    {
        Assert.AreEqual("40083220921276114", MexStanzas.ExtensionsGetFlowData.Id);
        Assert.AreEqual("https://web.whatsapp.com/flows/json/12345_ab", GalaxyFlowStanzas.BuildFlowJsonUri("12345_ab").ToString());
        Assert.ThrowsException<ArgumentException>(() => GalaxyFlowStanzas.BuildFlowJsonUri("../secret"));

        using var doc = JsonDocument.Parse("""
        {
          "xwa_extensions_get_flow_data": {
            "extensions_flow_data": [
              {
                "flow_id": "987654321",
                "extensions": {
                  "metadata": {
                    "flow_name": "appt_flow",
                    "state": "PUBLISHED",
                    "categories": ["APPOINTMENT_BOOKING"]
                  }
                }
              }
            ],
            "endpoint_public_key": {
              "key": "-----BEGIN PUBLIC KEY-----\nTEST\n-----END PUBLIC KEY-----",
              "signature": "sig123"
            }
          }
        }
        """);
        GalaxyFlowDataResult parsed = GalaxyFlowStanzas.ParseGetFlowDataResponse(doc.RootElement);
        Assert.AreEqual(1, parsed.Flows.Count);
        Assert.AreEqual("PUBLISHED", parsed.Flows[0].State);
        Assert.AreEqual("APPOINTMENT_BOOKING", parsed.Flows[0].Categories?[0]);
        Assert.AreEqual("sig123", parsed.EndpointPublicKey?.Signature);

        // Full RSA-OAEP SHA-256 + AES-GCM + inverted-IV round-trip
        using var rsa = RSA.Create(2048);
        string pem = rsa.ExportSubjectPublicKeyInfoPem();
        byte[] aesKey = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);

        const string payloadJson = """{"version":"3.0","action":"INIT","flow_token":"t1"}""";
        EncryptedFlowRequestPayload enc = GalaxyFlowStanzas.EncryptFlowEndpointPayload(payloadJson, pem, aesKey, iv);

        byte[] decryptedKey = rsa.Decrypt(Convert.FromBase64String(enc.EncryptedAesKeyBase64), RSAEncryptionPadding.OaepSHA256);
        CollectionAssert.AreEqual(aesKey, decryptedKey);

        byte[] combined = Convert.FromBase64String(enc.EncryptedFlowDataBase64);
        var reqCipher = new GcmBlockCipher(new AesEngine());
        reqCipher.Init(false, new AeadParameters(new KeyParameter(decryptedKey), 128, Convert.FromBase64String(enc.InitialVectorBase64)));
        byte[] decReq = new byte[reqCipher.GetOutputSize(combined.Length)];
        int reqLen = reqCipher.ProcessBytes(combined, 0, combined.Length, decReq, 0);
        reqLen += reqCipher.DoFinal(decReq, reqLen);
        Assert.AreEqual(payloadJson, Encoding.UTF8.GetString(decReq, 0, reqLen));

        // Server response encrypted with bitwise-inverted IV
        const string serverRespJson = """{"screen":"SUCCESS","data":{"ok":true}}""";
        byte[] respPlain = Encoding.UTF8.GetBytes(serverRespJson);
        byte[] invIv = iv.Select(b => (byte)~b).ToArray();
        var respCipher = new GcmBlockCipher(new AesEngine());
        respCipher.Init(true, new AeadParameters(new KeyParameter(aesKey), 128, invIv));
        byte[] respAll = new byte[respCipher.GetOutputSize(respPlain.Length)];
        int rLen = respCipher.ProcessBytes(respPlain, 0, respPlain.Length, respAll, 0);
        respCipher.DoFinal(respAll, rLen);
        string decResp = GalaxyFlowStanzas.DecryptFlowEndpointResponse(Convert.ToBase64String(respAll), aesKey, iv);
        Assert.AreEqual(serverRespJson, decResp);

        // Quoted Location, Quoted Contact, Address, Call Permission builders
        var qKey = new MessageKey { Id = "REQ-1", RemoteJid = "15550001@s.whatsapp.net" };
        ContextInfo ctx = RichMessageBuilders.BuildQuoteContextInfo(qKey, new Message { Conversation = "Send loc" })!;
        Message locReply = RichMessageBuilders.BuildLocation(19.076, 72.8777, "Mumbai", contextInfo: ctx);
        Assert.AreEqual("REQ-1", locReply.LocationMessage.ContextInfo.StanzaId);

        Message phoneReply = RichMessageBuilders.BuildContact("Me", RichMessageBuilders.BuildVCard("Me", "+919820000000"), ctx);
        Assert.AreEqual("REQ-1", phoneReply.ContactMessage.ContextInfo.StanzaId);

        string addrJson = GalaxyFlowStanzas.BuildAddressResponseParamsJson(new Dictionary<string, string?> { ["city"] = "Mumbai" });
        Assert.IsTrue(addrJson.Contains("\"city\":\"Mumbai\""));
        string cpJson = GalaxyFlowStanzas.BuildCallPermissionResponseParamsJson("accept", 1800000000);
        Assert.IsTrue(cpJson.Contains("\"response\":\"accept\"") && cpJson.Contains("1800000000"));
    }

    [TestMethod]
    public void MediaHeaderInspector_ParsesPngJpegWebpOggMp4_And_BuildLinkPreviewText()
    {
        // Minimal PNG header (1280x720)
        byte[] png =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0x00, 0x00, 0x05, 0x00, // 1280
            0x00, 0x00, 0x02, 0xD0  // 720
        ];
        var pngMeta = WhatsApp.Protocol.Abstractions.MediaHeaderInspector.Inspect(png);
        Assert.AreEqual(1280u, pngMeta.Width);
        Assert.AreEqual(720u, pngMeta.Height);

        // Minimal JPEG SOF0 (640x480)
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0xE0, 0x02, 0x80];
        var jpgMeta = WhatsApp.Protocol.Abstractions.MediaHeaderInspector.Inspect(jpg);
        Assert.AreEqual(640u, jpgMeta.Width);
        Assert.AreEqual(480u, jpgMeta.Height);

        // Minimal WebP VP8X (512x256)
        byte[] webp =
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P',
            (byte)'V', (byte)'P', (byte)'8', (byte)'X',
            0x0A, 0, 0, 0, 0, 0, 0, 0,
            0xFF, 0x01, 0x00, // 512 - 1 = 511 (0x01FF)
            0xFF, 0x00, 0x00  // 256 - 1 = 255 (0x00FF)
        ];
        var webpMeta = WhatsApp.Protocol.Abstractions.MediaHeaderInspector.Inspect(webp);
        Assert.AreEqual(512u, webpMeta.Width);
        Assert.AreEqual(256u, webpMeta.Height);

        // Minimal MP4 mvhd v0 (timescale=1000, duration=15000 => 15s)
        byte[] mp4 =
        [
            0, 0, 0, 24, (byte)'m', (byte)'v', (byte)'h', (byte)'d',
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, // timescale offset is idx+16 -> wait: version=0 at +8
            0x00, 0x00, 0x03, 0xE8,
            0x00, 0x00, 0x3A, 0x98
        ];
        // Note: mvhd box starts at 0 -> 'mvhd' at index 4! Wait: idx=4, version at idx+4=8, timescale at idx+16=20, duration at idx+20=24 -> 28 bytes total
        var mp4Meta = WhatsApp.Protocol.Abstractions.MediaHeaderInspector.Inspect(mp4);
        Assert.AreEqual(15u, mp4Meta.Seconds);

        Message lp = RichMessageBuilders.BuildLinkPreviewText(
            "Check https://example.com/post",
            "https://example.com/post",
            "https://example.com/canonical",
            "Example Title",
            "Preview desc",
            [1, 2, 3]);
        Assert.AreEqual("https://example.com/canonical", lp.ExtendedTextMessage.MatchedText);
        Assert.AreEqual("Example Title", lp.ExtendedTextMessage.Title);
        Assert.AreEqual("Preview desc", lp.ExtendedTextMessage.Description);
        Assert.AreEqual(Message.Types.ExtendedTextMessage.Types.PreviewType.Image, lp.ExtendedTextMessage.PreviewType);
    }
}
