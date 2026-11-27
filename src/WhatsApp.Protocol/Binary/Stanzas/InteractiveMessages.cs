using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whatsapp;

namespace WhatsApp.Protocol.Binary.Stanzas;

public sealed record InteractiveRow(
    string Id,
    string Title,
    string? Description = null);

public sealed record InteractiveSection(
    string? Title,
    IReadOnlyList<InteractiveRow> Rows);

public sealed record InteractiveOrderInfo(
    string? ReferenceId = null,
    string? Currency = null,
    decimal? TotalAmount = null,
    string? Status = null,
    string? Description = null,
    string? PaymentUrl = null,
    string? PaymentStatus = null,
    string? SellerJid = null);

public sealed record InteractiveFlowInfo(
    string? FlowId = null,
    string? FlowToken = null,
    string? FlowCta = null,
    string? FlowAction = null,
    string? FlowName = null,
    string? Mode = null,
    string? FlowMessageVersion = null,
    string? FlowDataEndpoint = null,
    string? FlowActionPayloadJson = null,
    long? ExpireTime = null);

public sealed record InteractiveBookingInfo(
    string? StartDatetime = null,
    string? EndDatetime = null,
    string? Location = null,
    string? BookingUrl = null,
    string? PhoneNumber = null,
    string? BookingManagementUrl = null,
    string? Description = null,
    string? Email = null);

public sealed record InteractivePaymentReminderInfo(
    string? Title = null,
    string? AmountDue = null,
    string? AmountDueLabel = null,
    string? DueDate = null,
    string? DueDateLabel = null,
    string? Currency = null,
    bool IsOverdue = false,
    int? DaysOverdue = null,
    string? AccountCard = null,
    string? Url = null,
    string? PayNowButtonText = null);

public sealed record InteractiveAddressInfo(
    string? SavedValuesJson = null,
    IReadOnlyList<string>? SavedAddressesJson = null,
    string? ValidationErrorsJson = null);

public sealed record InteractiveCatalogInfo(
    string? BusinessPhoneNumber = null,
    string? ThumbnailProductRetailerId = null,
    string? ShopId = null,
    string? CollectionId = null);

public sealed record InteractiveButton(
    string? Id,
    string DisplayText,
    string Kind,
    string? Url = null,
    string? PhoneNumber = null,
    string? NativeFlowName = null,
    string? NativeFlowParamsJson = null,
    uint? Index = null,
    string? CopyCode = null,
    uint? CardIndex = null,
    InteractiveFlowInfo? Flow = null);

public sealed record InteractiveResponseSummary(
    string? SelectedId = null,
    string? SelectedDisplayText = null,
    string? Description = null,
    string? NativeFlowName = null,
    string? NativeFlowParamsJson = null);

public sealed record InteractiveMessageContent(
    string Kind,
    string? Title = null,
    string? Body = null,
    string? Footer = null,
    string? ButtonText = null,
    string? NativeFlowName = null,
    IReadOnlyList<InteractiveSection>? Sections = null,
    IReadOnlyList<InteractiveButton>? Buttons = null,
    InteractiveResponseSummary? Response = null,
    IReadOnlyList<InteractiveMessageContent>? Cards = null,
    InteractiveOrderInfo? Order = null,
    InteractiveFlowInfo? Flow = null,
    InteractiveBookingInfo? Booking = null,
    InteractivePaymentReminderInfo? PaymentReminder = null,
    InteractiveAddressInfo? Address = null,
    InteractiveCatalogInfo? Catalog = null);

/// <summary>
/// Parses WhatsApp business interactive/list/button/template messages and their user responses into
/// structured <see cref="InteractiveMessageContent"/> payloads shared by live receive and history sync.
/// </summary>
public static class InteractiveMessageParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static InteractiveMessageContent? Parse(Message? root)
    {
        if (root is null)
        {
            return null;
        }

        Message m = MessageStanzas.UnwrapClassificationMessage(root);

        if (m.ListMessage is { } list)
        {
            var secList = list.Sections.Select(s => new InteractiveSection(
                s.HasTitle ? s.Title : null,
                s.Rows.Select(r => new InteractiveRow(
                    r.HasRowId ? r.RowId : string.Empty,
                    r.HasTitle ? r.Title : (r.HasRowId ? r.RowId : string.Empty),
                    r.HasDescription ? r.Description : null)).ToArray())).ToList();

            InteractiveCatalogInfo? catalog = null;
            if (list.ProductListInfo is { } pli)
            {
                foreach (var ps in pli.ProductSections)
                {
                    var rows = ps.Products.Select(p => new InteractiveRow(
                        p.HasProductId ? p.ProductId : string.Empty,
                        p.HasProductId ? p.ProductId : string.Empty)).ToArray();
                    if (rows.Length > 0)
                    {
                        secList.Add(new InteractiveSection(ps.HasTitle ? ps.Title : null, rows));
                    }
                }
                catalog = new InteractiveCatalogInfo(
                    BusinessPhoneNumber: pli.HasBusinessOwnerJid ? pli.BusinessOwnerJid : null,
                    ThumbnailProductRetailerId: pli.HeaderImage?.HasProductId == true ? pli.HeaderImage.ProductId : null);
            }

            return new InteractiveMessageContent(
                Kind: list.ListType == Message.Types.ListMessage.Types.ListType.ProductList ? "product_list" : "list",
                Title: list.HasTitle ? list.Title : null,
                Body: list.HasDescription ? list.Description : null,
                Footer: list.HasFooterText ? list.FooterText : null,
                ButtonText: list.HasButtonText ? list.ButtonText : null,
                Sections: secList.Count > 0 ? secList.ToArray() : null,
                Catalog: catalog);
        }

        if (m.ListResponseMessage is { } lr)
        {
            string? selectedId = lr.SingleSelectReply is { HasSelectedRowId: true }
                ? lr.SingleSelectReply.SelectedRowId
                : null;
            return new InteractiveMessageContent(
                Kind: "list_response",
                Title: lr.HasTitle ? lr.Title : null,
                Body: lr.HasDescription ? lr.Description : null,
                Response: new InteractiveResponseSummary(
                    SelectedId: selectedId,
                    SelectedDisplayText: lr.HasTitle ? lr.Title : null,
                    Description: lr.HasDescription ? lr.Description : null));
        }

        if (m.ButtonsMessage is { } bm)
        {
            var buttons = bm.Buttons.Select((b, idx) => new InteractiveButton(
                Id: b.HasButtonId ? b.ButtonId : null,
                DisplayText: b.ButtonText?.HasDisplayText == true ? b.ButtonText.DisplayText : (b.HasButtonId ? b.ButtonId : $"Button {idx + 1}"),
                Kind: b.HasType ? b.Type.ToString().ToLowerInvariant() : "response",
                NativeFlowName: b.NativeFlowInfo?.HasName == true ? b.NativeFlowInfo.Name : null,
                NativeFlowParamsJson: b.NativeFlowInfo?.HasParamsJson == true ? b.NativeFlowInfo.ParamsJson : null,
                Index: (uint)idx)).ToArray();

            return new InteractiveMessageContent(
                Kind: "buttons",
                Title: bm.HasText ? bm.Text : null,
                Body: bm.HasContentText ? bm.ContentText : null,
                Footer: bm.HasFooterText ? bm.FooterText : null,
                Buttons: buttons);
        }

        if (m.ButtonsResponseMessage is { } br)
        {
            return new InteractiveMessageContent(
                Kind: "buttons_response",
                Response: new InteractiveResponseSummary(
                    SelectedId: br.HasSelectedButtonId ? br.SelectedButtonId : null,
                    SelectedDisplayText: br.HasSelectedDisplayText ? br.SelectedDisplayText : null));
        }

        if (m.InteractiveMessage is { } im)
        {
            return ParseInteractive(im);
        }

        if (m.InteractiveResponseMessage is { } irm)
        {
            string? nfmName = irm.NativeFlowResponseMessage?.HasName == true ? irm.NativeFlowResponseMessage.Name : null;
            string? nfmParams = irm.NativeFlowResponseMessage?.HasParamsJson == true ? irm.NativeFlowResponseMessage.ParamsJson : null;
            string? body = irm.Body?.HasText == true ? irm.Body.Text : null;
            return new InteractiveMessageContent(
                Kind: "interactive_response",
                Body: body,
                NativeFlowName: nfmName,
                Response: new InteractiveResponseSummary(
                    SelectedDisplayText: body,
                    NativeFlowName: nfmName,
                    NativeFlowParamsJson: nfmParams));
        }

        if (m.TemplateMessage is { } tm)
        {
            return ParseTemplate(tm);
        }

        if (m.TemplateButtonReplyMessage is { } tbr)
        {
            return new InteractiveMessageContent(
                Kind: "template_button_reply",
                Response: new InteractiveResponseSummary(
                    SelectedId: tbr.HasSelectedId ? tbr.SelectedId : null,
                    SelectedDisplayText: tbr.HasSelectedDisplayText ? tbr.SelectedDisplayText : null));
        }

        if (m.RequestPhoneNumberMessage is not null)
        {
            return new InteractiveMessageContent(
                Kind: "request_phone_number",
                Title: "Request phone number");
        }

        if (m.OrderMessage is { } om)
        {
            decimal? amt = om.HasTotalAmount1000 ? om.TotalAmount1000 / 1000m : null;
            return new InteractiveMessageContent(
                Kind: "order",
                Title: om.HasOrderTitle ? om.OrderTitle : (om.HasOrderId ? $"Order #{om.OrderId}" : "Order"),
                Body: om.HasMessage ? om.Message : null,
                Order: new InteractiveOrderInfo(
                    ReferenceId: om.HasOrderId ? om.OrderId : null,
                    Currency: om.HasTotalCurrencyCode ? om.TotalCurrencyCode : null,
                    TotalAmount: amt,
                    Status: om.HasStatus ? om.Status.ToString().ToLowerInvariant() : null,
                    Description: om.HasMessage ? om.Message : null,
                    SellerJid: om.HasSellerJid ? om.SellerJid : null));
        }

        if (m.ProductMessage is { } pm)
        {
            return new InteractiveMessageContent(
                Kind: "product",
                Title: pm.Product?.HasTitle == true ? pm.Product.Title : null,
                Body: pm.HasBody ? pm.Body : null,
                Footer: pm.HasFooter ? pm.Footer : null,
                Catalog: new InteractiveCatalogInfo(
                    BusinessPhoneNumber: pm.HasBusinessOwnerJid ? pm.BusinessOwnerJid : null,
                    ThumbnailProductRetailerId: pm.Product?.HasProductId == true ? pm.Product.ProductId : null));
        }

        if (m.PaymentReminderMessage is { } pr)
        {
            string? amtStr = pr.Amount is { HasValue: true }
                ? $"{(decimal)pr.Amount.Value / (pr.Amount.HasOffset && pr.Amount.Offset > 0 ? pr.Amount.Offset : 1000):0.##}"
                : null;
            return new InteractiveMessageContent(
                Kind: "payment_reminder",
                Body: pr.HasDescription ? pr.Description : null,
                PaymentReminder: new InteractivePaymentReminderInfo(
                    Title: pr.HasDescription ? pr.Description : null,
                    AmountDue: amtStr,
                    Currency: pr.Amount?.HasCurrencyCode == true ? pr.Amount.CurrencyCode : null,
                    IsOverdue: pr.HasStatus && pr.Status == Message.Types.PaymentReminderMessage.Types.ReminderStatus.Expired,
                    AccountCard: pr.HasPayeeVpa ? pr.PayeeVpa : null));
        }

        return null;
    }

    public static string? Serialize(InteractiveMessageContent? content) =>
        content is null ? null : JsonSerializer.Serialize(content, JsonOptions);

    public static InteractiveMessageContent? TryFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<InteractiveMessageContent>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractSummaryText(InteractiveMessageContent? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content.Response is { } resp)
        {
            string? label = resp.SelectedDisplayText ?? resp.SelectedId ?? content.Body ?? content.Title;
            if (!string.IsNullOrWhiteSpace(resp.Description))
            {
                return string.IsNullOrWhiteSpace(label) ? resp.Description : $"{label}\n{resp.Description}";
            }
            if (!string.IsNullOrWhiteSpace(label))
            {
                return label;
            }
            if (!string.IsNullOrWhiteSpace(resp.NativeFlowName))
            {
                return $"[{resp.NativeFlowName}]";
            }
        }

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(content.Title)) parts.Add(content.Title);
        if (!string.IsNullOrWhiteSpace(content.Body)) parts.Add(content.Body);
        if (!string.IsNullOrWhiteSpace(content.Footer)) parts.Add(content.Footer);

        if (parts.Count > 0)
        {
            return string.Join("\n", parts);
        }

        if (!string.IsNullOrWhiteSpace(content.ButtonText))
        {
            return $"[{content.Kind}: {content.ButtonText}]";
        }
        if (content.Flow is { } fl)
        {
            string label = fl.FlowCta ?? fl.FlowName ?? fl.FlowId ?? "Flow";
            return $"[Flow: {label}]";
        }
        if (content.Booking is { } bk && !string.IsNullOrWhiteSpace(bk.StartDatetime))
        {
            string loc = !string.IsNullOrWhiteSpace(bk.Location) ? $" @ {bk.Location}" : string.Empty;
            return $"[Booking: {bk.StartDatetime}{loc}]";
        }
        if (content.PaymentReminder is { } prm)
        {
            string due = !string.IsNullOrWhiteSpace(prm.DueDate) ? $" due {prm.DueDate}" : string.Empty;
            string amt = !string.IsNullOrWhiteSpace(prm.AmountDue) ? $" {prm.Currency}{prm.AmountDue}" : string.Empty;
            return $"[Payment Reminder{amt}{due}]";
        }
        if (content.Order is { } ord && !string.IsNullOrWhiteSpace(ord.ReferenceId))
        {
            string amt = ord.TotalAmount.HasValue ? $" {ord.Currency}{ord.TotalAmount:0.##}" : string.Empty;
            string st = !string.IsNullOrWhiteSpace(ord.Status) ? $" ({ord.Status})" : string.Empty;
            return $"[Order #{ord.ReferenceId}{amt}{st}]";
        }
        if (content.Cards is { Count: > 0 })
        {
            string first = content.Cards.Select(ExtractSummaryText).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? $"{content.Cards.Count} cards";
            return $"[Carousel ({content.Cards.Count})]: {first}";
        }
        if (content.Buttons is { Count: > 0 })
        {
            return string.Join(" | ", content.Buttons.Select(b => b.DisplayText));
        }
        return $"[{content.Kind}]";
    }

    public static ContextInfo? ExtractInteractiveContextInfo(Message m)
    {
        Message cur = MessageStanzas.UnwrapClassificationMessage(m);
        return cur.ListMessage?.ContextInfo
            ?? cur.ListResponseMessage?.ContextInfo
            ?? cur.ButtonsMessage?.ContextInfo
            ?? cur.ButtonsResponseMessage?.ContextInfo
            ?? cur.InteractiveMessage?.ContextInfo
            ?? cur.InteractiveResponseMessage?.ContextInfo
            ?? cur.TemplateMessage?.ContextInfo
            ?? cur.TemplateButtonReplyMessage?.ContextInfo
            ?? cur.RequestPhoneNumberMessage?.ContextInfo
            ?? cur.OrderMessage?.ContextInfo
            ?? cur.ProductMessage?.ContextInfo;
    }

    /// <summary>
    /// Extracts an embedded media header (PDF/Image/Video) from <c>ButtonsMessage</c>,
    /// <c>InteractiveMessage</c>, or <c>TemplateMessage</c> so bill PDFs etc. remain downloadable.
    /// </summary>
    public static Message? ExtractEmbeddedMediaHeader(Message? root)
    {
        if (root is null)
        {
            return null;
        }
        Message m = MessageStanzas.UnwrapClassificationMessage(root);

        if (m.InteractiveMessage?.Header is { } imHdr)
        {
            if (ExtractHeaderMedia(imHdr) is { } hdrMedia) return hdrMedia;
        }
        if (m.InteractiveMessage?.CarouselMessage?.Cards is { Count: > 0 } cards)
        {
            foreach (Message.Types.InteractiveMessage card in cards)
            {
                if (card.Header is { } cHdr && ExtractHeaderMedia(cHdr) is { } cardMedia)
                {
                    return cardMedia;
                }
            }
        }

        if (m.ButtonsMessage is { } bm)
        {
            if (bm.DocumentMessage is { } doc) return new Message { DocumentMessage = doc };
            if (bm.ImageMessage is { } img) return new Message { ImageMessage = img };
            if (bm.VideoMessage is { } vid) return new Message { VideoMessage = vid };
        }

        if (m.TemplateMessage is { } tm)
        {
            if ((tm.HydratedFourRowTemplate ?? tm.HydratedTemplate) is { } hyd)
            {
                if (hyd.DocumentMessage is { } doc) return new Message { DocumentMessage = doc };
                if (hyd.ImageMessage is { } img) return new Message { ImageMessage = img };
                if (hyd.VideoMessage is { } vid) return new Message { VideoMessage = vid };
            }
            if (tm.FourRowTemplate is { } four)
            {
                if (four.DocumentMessage is { } doc) return new Message { DocumentMessage = doc };
                if (four.ImageMessage is { } img) return new Message { ImageMessage = img };
                if (four.VideoMessage is { } vid) return new Message { VideoMessage = vid };
            }
            if (tm.InteractiveMessageTemplate?.Header is { } itHdr && ExtractHeaderMedia(itHdr) is { } tmMedia)
            {
                return tmMedia;
            }
        }

        return null;
    }

    private static Message? ExtractHeaderMedia(Message.Types.InteractiveMessage.Types.Header hdr)
    {
        if (hdr.DocumentMessage is { } doc) return new Message { DocumentMessage = doc };
        if (hdr.ImageMessage is { } img) return new Message { ImageMessage = img };
        if (hdr.VideoMessage is { } vid) return new Message { VideoMessage = vid };
        return null;
    }

    private static InteractiveMessageContent ParseInteractive(Message.Types.InteractiveMessage im, uint? cardIndex = null)
    {
        string? title = im.Header?.HasTitle == true ? im.Header.Title : null;
        string? body = im.Body?.HasText == true ? im.Body.Text : null;
        string? footer = im.Footer?.HasText == true ? im.Footer.Text : null;

        if (im.CarouselMessage is { Cards.Count: > 0 } carousel)
        {
            InteractiveMessageContent[] cards = carousel.Cards
                .Select((card, idx) => ParseInteractive(card, (uint)idx))
                .ToArray();
            InteractiveButton[] allCardButtons = cards
                .Where(c => c.Buttons is not null)
                .SelectMany(c => c.Buttons!)
                .ToArray();

            return new InteractiveMessageContent(
                Kind: "carousel",
                Title: title,
                Body: body,
                Footer: footer,
                Buttons: allCardButtons.Length > 0 ? allCardButtons : null,
                Cards: cards);
        }

        InteractiveButton[]? buttons = null;
        InteractiveSection[]? sections = null;
        InteractiveOrderInfo? order = null;
        InteractiveFlowInfo? flow = null;
        InteractiveBookingInfo? booking = null;
        InteractivePaymentReminderInfo? paymentReminder = null;
        InteractiveAddressInfo? address = null;
        InteractiveCatalogInfo? catalog = null;
        string? buttonText = null;
        string? nativeFlowName = null;

        if (im.NativeFlowMessage is { } nfm)
        {
            var buttonList = new List<InteractiveButton>(nfm.Buttons.Count);
            for (int idx = 0; idx < nfm.Buttons.Count; idx++)
            {
                Message.Types.InteractiveMessage.Types.NativeFlowMessage.Types.NativeFlowButton b = nfm.Buttons[idx];
                string? bName = b.HasName ? b.Name : null;
                string? bParams = b.HasButtonParamsJson ? b.ButtonParamsJson : null;
                var parsed = ExtractNativeFlowParams(bName, bParams);
                if (parsed.Sections is { Length: > 0 })
                {
                    sections = parsed.Sections;
                    buttonText ??= parsed.DisplayText;
                }
                order ??= parsed.Order;
                flow ??= parsed.Flow;
                booking ??= parsed.Booking;
                paymentReminder ??= parsed.PaymentReminder;
                address ??= parsed.Address;
                catalog ??= parsed.Catalog;
                buttonList.Add(new InteractiveButton(
                    Id: parsed.Id ?? bName,
                    DisplayText: parsed.DisplayText ?? bName ?? $"Button {idx + 1}",
                    Kind: MapNativeFlowButtonKind(bName),
                    Url: parsed.Url,
                    PhoneNumber: parsed.Phone,
                    NativeFlowName: bName,
                    NativeFlowParamsJson: bParams,
                    Index: (uint)idx,
                    CopyCode: parsed.CopyCode,
                    CardIndex: cardIndex,
                    Flow: parsed.Flow));
            }
            buttons = buttonList.ToArray();
            if (buttons.Length > 0)
            {
                nativeFlowName = buttons[0].NativeFlowName;
            }
        }
        else if (im.ShopStorefrontMessage is { } shop)
        {
            nativeFlowName = "shop_storefront";
            title ??= shop.HasId ? $"Shop {shop.Id}" : "Shop";
            catalog = new InteractiveCatalogInfo(ShopId: shop.HasId ? shop.Id : null);
        }
        else if (im.CollectionMessage is { } coll)
        {
            nativeFlowName = "collection";
            title ??= coll.HasId ? $"Collection {coll.Id}" : "Collection";
            catalog = new InteractiveCatalogInfo(
                BusinessPhoneNumber: coll.HasBizJid ? coll.BizJid : null,
                CollectionId: coll.HasId ? coll.Id : null);
        }

        return new InteractiveMessageContent(
            Kind: "interactive",
            Title: title,
            Body: body,
            Footer: footer,
            ButtonText: buttonText,
            NativeFlowName: nativeFlowName,
            Sections: sections,
            Buttons: buttons,
            Order: order,
            Flow: flow,
            Booking: booking,
            PaymentReminder: paymentReminder,
            Address: address,
            Catalog: catalog);
    }

    private static string MapNativeFlowButtonKind(string? bName) => bName switch
    {
        "quick_reply" => "quick_reply",
        "cta_copy" => "cta_copy",
        "cta_url" => "url",
        "cta_call" => "call",
        "send_location" => "send_location",
        "galaxy_message" => "galaxy_message",
        "address_message" => "address_message",
        "call_permission_request" => "call_permission_request",
        "booking_confirmation" or "booking_status" => "booking_confirmation",
        "payment_reminder" => "payment_reminder",
        "cta_catalog" or "catalog_message" => "catalog",
        "review_and_pay" or "review_order" or "order_status" => "order",
        _ => "native_flow"
    };

    private static InteractiveMessageContent ParseTemplate(Message.Types.TemplateMessage tm)
    {
        if (tm.InteractiveMessageTemplate is { } im)
        {
            return ParseInteractive(im);
        }

        Message.Types.TemplateMessage.Types.HydratedFourRowTemplate? hyd = tm.HydratedFourRowTemplate ?? tm.HydratedTemplate;
        if (hyd is not null)
        {
            var buttons = hyd.HydratedButtons.Select((hb, idx) =>
            {
                uint btnIdx = hb.HasIndex ? hb.Index : (uint)idx;
                if (hb.UrlButton is { } url)
                {
                    return new InteractiveButton(
                        Id: null,
                        DisplayText: url.HasDisplayText ? url.DisplayText : "Open link",
                        Kind: "url",
                        Url: url.HasUrl ? url.Url : null,
                        Index: btnIdx);
                }
                if (hb.CallButton is { } call)
                {
                    return new InteractiveButton(
                        Id: null,
                        DisplayText: call.HasDisplayText ? call.DisplayText : "Call",
                        Kind: "call",
                        PhoneNumber: call.HasPhoneNumber ? call.PhoneNumber : null,
                        Index: btnIdx);
                }
                if (hb.QuickReplyButton is { } qr)
                {
                    return new InteractiveButton(
                        Id: qr.HasId ? qr.Id : null,
                        DisplayText: qr.HasDisplayText ? qr.DisplayText : (qr.HasId ? qr.Id : $"Reply {idx + 1}"),
                        Kind: "quick_reply",
                        Index: btnIdx);
                }
                return new InteractiveButton(null, $"Button {idx + 1}", "unknown", Index: btnIdx);
            }).ToArray();

            return new InteractiveMessageContent(
                Kind: "template",
                Title: hyd.HasHydratedTitleText ? hyd.HydratedTitleText : null,
                Body: hyd.HasHydratedContentText ? hyd.HydratedContentText : null,
                Footer: hyd.HasHydratedFooterText ? hyd.HydratedFooterText : null,
                Buttons: buttons.Length > 0 ? buttons : null);
        }

        if (tm.FourRowTemplate is { } four)
        {
            if (four.Content?.HydratedHsm is { } hydHsm)
            {
                return ParseTemplate(hydHsm);
            }
            var buttons = four.Buttons.Select((b, idx) =>
            {
                uint btnIdx = b.HasIndex ? b.Index : (uint)idx;
                if (b.UrlButton is { } url)
                {
                    return new InteractiveButton(null, FormatHsmText(url.DisplayText) ?? "Open link", "url", Url: FormatHsmText(url.Url), Index: btnIdx);
                }
                if (b.CallButton is { } call)
                {
                    return new InteractiveButton(null, FormatHsmText(call.DisplayText) ?? "Call", "call", PhoneNumber: FormatHsmText(call.PhoneNumber), Index: btnIdx);
                }
                if (b.QuickReplyButton is { } qr)
                {
                    string? qrId = qr.HasId ? qr.Id : null;
                    return new InteractiveButton(qrId, FormatHsmText(qr.DisplayText) ?? qrId ?? $"Reply {idx + 1}", "quick_reply", Index: btnIdx);
                }
                return new InteractiveButton(null, $"Button {idx + 1}", "unknown", Index: btnIdx);
            }).ToArray();

            return new InteractiveMessageContent(
                Kind: "template",
                Title: FormatHsmText(four.HighlyStructuredMessage),
                Body: FormatHsmText(four.Content),
                Footer: FormatHsmText(four.Footer),
                Buttons: buttons.Length > 0 ? buttons : null);
        }

        return new InteractiveMessageContent(Kind: "template");
    }

    private static string? FormatHsmText(Message.Types.HighlyStructuredMessage? hsm)
    {
        if (hsm is null) return null;
        if (hsm.Params.Count > 0) return string.Join(" ", hsm.Params);
        if (hsm.LocalizableParams.Count > 0)
        {
            var vals = hsm.LocalizableParams.Where(p => p.HasDefault).Select(p => p.Default);
            string joined = string.Join(" ", vals);
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }
        return hsm.HasElementName ? $"[{hsm.ElementName}]" : null;
    }

    private sealed record NativeFlowParsedParams(
        string? Id,
        string? DisplayText,
        string? Url,
        string? Phone,
        string? CopyCode,
        InteractiveSection[]? Sections,
        InteractiveOrderInfo? Order,
        InteractiveFlowInfo? Flow,
        InteractiveBookingInfo? Booking,
        InteractivePaymentReminderInfo? PaymentReminder,
        InteractiveAddressInfo? Address,
        InteractiveCatalogInfo? Catalog);

    private static NativeFlowParsedParams ExtractNativeFlowParams(string? name, string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            string? fallbackLabel = name switch
            {
                "review_and_pay" => "View details",
                "review_order" or "order_status" => "Order status",
                "send_location" => "Send location",
                "address_message" => "Provide address",
                "call_permission_request" => "Respond to call request",
                "cta_catalog" or "catalog_message" => "View catalog",
                _ => null
            };
            return new NativeFlowParsedParams(null, fallbackLabel, null, null, null, null, null, null, null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            JsonElement root = doc.RootElement;
            string? id = TryGetStringProp(root, "id");
            string? display = TryGetStringProp(root, "display_text")
                ?? TryGetStringProp(root, "pay_now_button_text")
                ?? TryGetStringProp(root, "flow_cta")
                ?? TryGetStringProp(root, "title");
            string? url = TryGetStringProp(root, "url")
                ?? TryGetStringProp(root, "merchant_url")
                ?? TryGetStringProp(root, "cta_action_url")
                ?? TryGetStringProp(root, "booking_url");
            string? phone = TryGetStringProp(root, "phone_number");
            string? copyCode = TryGetStringProp(root, "copy_code");

            InteractiveSection[]? sections = null;
            if (root.TryGetProperty("sections", out JsonElement secEl) && secEl.ValueKind == JsonValueKind.Array)
            {
                var secList = new List<InteractiveSection>();
                foreach (JsonElement s in secEl.EnumerateArray())
                {
                    string? secTitle = TryGetStringProp(s, "title");
                    var rows = new List<InteractiveRow>();
                    if (s.TryGetProperty("rows", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement r in rEl.EnumerateArray())
                        {
                            string rId = TryGetStringProp(r, "id") ?? TryGetStringProp(r, "rowId") ?? string.Empty;
                            string rTitle = TryGetStringProp(r, "title") ?? rId;
                            rows.Add(new InteractiveRow(rId, rTitle, TryGetStringProp(r, "description")));
                        }
                    }
                    if (rows.Count > 0) secList.Add(new InteractiveSection(secTitle, rows.ToArray()));
                }
                if (secList.Count > 0) sections = secList.ToArray();
            }

            InteractiveOrderInfo? order = null;
            if (string.Equals(name, "review_and_pay", StringComparison.Ordinal)
                || string.Equals(name, "review_order", StringComparison.Ordinal)
                || string.Equals(name, "order_status", StringComparison.Ordinal)
                || string.Equals(name, "payment_info", StringComparison.Ordinal)
                || root.TryGetProperty("reference_id", out _))
            {
                string? refId = TryGetStringProp(root, "reference_id");
                string? currency = TryGetStringProp(root, "currency");
                decimal? totalAmt = TryReadMoneyAmount(root, "total_amount");
                string? status = null;
                string? desc = null;
                if (root.TryGetProperty("order", out JsonElement ordEl) && ordEl.ValueKind == JsonValueKind.Object)
                {
                    status = TryGetStringProp(ordEl, "status");
                    desc = TryGetStringProp(ordEl, "description");
                }
                string? payStatus = root.TryGetProperty("payment", out JsonElement payEl) && payEl.ValueKind == JsonValueKind.Object
                    ? TryGetStringProp(payEl, "status")
                    : null;
                string? payUrl = null;
                if (root.TryGetProperty("payment_settings", out JsonElement psEl) && psEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement ps in psEl.EnumerateArray())
                    {
                        if (ps.TryGetProperty("upi_intent_link", out var upi)
                            && upi.TryGetProperty("merchant_details", out var md)
                            && TryGetStringProp(md, "intent_link") is { Length: > 0 } intent)
                        {
                            payUrl = intent;
                            break;
                        }
                    }
                }
                if (refId is not null || totalAmt.HasValue || status is not null || payStatus is not null || payUrl is not null)
                {
                    order = new InteractiveOrderInfo(refId, currency, totalAmt, status, desc, payUrl, payStatus);
                }
                display ??= name == "review_and_pay" ? "View details" : ((name is "review_order" or "order_status") ? "Order status" : null);
            }

            InteractiveFlowInfo? flow = null;
            if (string.Equals(name, "galaxy_message", StringComparison.Ordinal) || root.TryGetProperty("flow_id", out _))
            {
                string? actionPayloadJson = root.TryGetProperty("flow_action_payload", out JsonElement fap) && fap.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? fap.GetRawText()
                    : TryGetStringProp(root, "flow_action_payload");
                long? expTime = root.TryGetProperty("expire_time", out JsonElement expEl) && expEl.TryGetInt64(out long et) ? et : null;
                flow = new InteractiveFlowInfo(
                    FlowId: TryGetStringProp(root, "flow_id"),
                    FlowToken: TryGetStringProp(root, "flow_token"),
                    FlowCta: TryGetStringProp(root, "flow_cta"),
                    FlowAction: TryGetStringProp(root, "flow_action"),
                    FlowName: TryGetStringProp(root, "flow_name"),
                    Mode: TryGetStringProp(root, "mode"),
                    FlowMessageVersion: TryGetStringProp(root, "flow_message_version"),
                    FlowDataEndpoint: TryGetStringProp(root, "flow_data_endpoint"),
                    FlowActionPayloadJson: actionPayloadJson,
                    ExpireTime: expTime);
                display ??= flow.FlowCta ?? "Open Flow";
            }

            InteractiveBookingInfo? booking = null;
            if (string.Equals(name, "booking_confirmation", StringComparison.Ordinal)
                || string.Equals(name, "booking_status", StringComparison.Ordinal)
                || root.TryGetProperty("start_datetime", out _))
            {
                booking = new InteractiveBookingInfo(
                    StartDatetime: TryGetStringProp(root, "start_datetime"),
                    EndDatetime: TryGetStringProp(root, "end_datetime"),
                    Location: TryGetStringProp(root, "location"),
                    BookingUrl: TryGetStringProp(root, "booking_url"),
                    PhoneNumber: TryGetStringProp(root, "phone_number"),
                    BookingManagementUrl: TryGetStringProp(root, "booking_management_url"),
                    Description: TryGetStringProp(root, "description"),
                    Email: TryGetStringProp(root, "email"));
                display ??= "View booking";
            }

            InteractivePaymentReminderInfo? paymentReminder = null;
            if (string.Equals(name, "payment_reminder", StringComparison.Ordinal))
            {
                bool isOverdue = root.TryGetProperty("is_overdue", out JsonElement ovEl) && ovEl.ValueKind == JsonValueKind.True;
                int? daysOverdue = root.TryGetProperty("days_overdue", out JsonElement doEl) && doEl.TryGetInt32(out int dov) ? dov : null;
                string? amtStr = TryGetStringProp(root, "amount_due");
                if (amtStr is null && TryReadMoneyAmount(root, "amount_due") is decimal decAmt)
                {
                    amtStr = decAmt.ToString("0.##");
                }
                string? dueDate = TryGetStringProp(root, "due_date");
                if (dueDate is null && root.TryGetProperty("due_date", out JsonElement ddEl) && ddEl.TryGetInt64(out long ddUnix))
                {
                    dueDate = DateTimeOffset.FromUnixTimeSeconds(ddUnix).UtcDateTime.ToString("yyyy-MM-dd");
                }
                string? acct = TryGetStringProp(root, "account_card")
                    ?? (TryGetStringProp(root, "account_name") is { } an && TryGetStringProp(root, "card_number_last_4_digits") is { } last4
                        ? $"{an} ••••{last4}"
                        : TryGetStringProp(root, "account_name"));
                paymentReminder = new InteractivePaymentReminderInfo(
                    Title: TryGetStringProp(root, "title"),
                    AmountDue: amtStr,
                    AmountDueLabel: TryGetStringProp(root, "amount_due_label"),
                    DueDate: dueDate,
                    DueDateLabel: TryGetStringProp(root, "due_date_label"),
                    Currency: TryGetStringProp(root, "amount_due_currency"),
                    IsOverdue: isOverdue,
                    DaysOverdue: daysOverdue,
                    AccountCard: acct,
                    Url: TryGetStringProp(root, "url") ?? TryGetStringProp(root, "cta_action_url"),
                    PayNowButtonText: TryGetStringProp(root, "pay_now_button_text"));
                display ??= paymentReminder.PayNowButtonText ?? "Pay now";
            }

            InteractiveAddressInfo? address = null;
            if (string.Equals(name, "address_message", StringComparison.Ordinal))
            {
                string? valsJson = root.TryGetProperty("values", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.Object ? vEl.GetRawText() : null;
                string? valErrsJson = root.TryGetProperty("validation_errors", out JsonElement veEl) && veEl.ValueKind == JsonValueKind.Object ? veEl.GetRawText() : null;
                List<string>? savedList = null;
                if (root.TryGetProperty("saved_addresses", out JsonElement saEl) && saEl.ValueKind == JsonValueKind.Array)
                {
                    savedList = [];
                    foreach (JsonElement item in saEl.EnumerateArray())
                    {
                        savedList.Add(item.GetRawText());
                    }
                }
                address = new InteractiveAddressInfo(valsJson, savedList?.ToArray(), valErrsJson);
                display ??= "Provide address";
            }

            InteractiveCatalogInfo? catalog = null;
            if (string.Equals(name, "cta_catalog", StringComparison.Ordinal)
                || string.Equals(name, "catalog_message", StringComparison.Ordinal))
            {
                catalog = new InteractiveCatalogInfo(
                    BusinessPhoneNumber: TryGetStringProp(root, "business_phone_number"),
                    ThumbnailProductRetailerId: TryGetStringProp(root, "thumbnail_product_retailer_id"));
                display ??= "View catalog";
            }

            if (string.Equals(name, "send_location", StringComparison.Ordinal))
            {
                display ??= "Send location";
            }
            else if (string.Equals(name, "call_permission_request", StringComparison.Ordinal))
            {
                display ??= "Allow calls";
            }

            return new NativeFlowParsedParams(
                id, display, url, phone, copyCode, sections, order, flow, booking, paymentReminder, address, catalog);
        }
        catch
        {
            return new NativeFlowParsedParams(null, null, null, null, null, null, null, null, null, null, null, null);
        }
    }

    private static decimal? TryReadMoneyAmount(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out JsonElement amtEl) || amtEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        long val = amtEl.TryGetProperty("value", out var ve) && ve.TryGetInt64(out long v) ? v : 0;
        int offset = amtEl.TryGetProperty("offset", out var oe) && oe.TryGetInt32(out int o) && o > 0 ? o : 100;
        return val > 0 ? (decimal)val / offset : null;
    }

    private static string? TryGetStringProp(JsonElement root, string prop) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(prop, out JsonElement el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
