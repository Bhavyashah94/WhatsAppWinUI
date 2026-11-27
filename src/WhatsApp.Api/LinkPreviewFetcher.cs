using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Api;

public sealed record LinkPreviewData(
    string MatchedUrl,
    string? CanonicalUrl,
    string? Title,
    string? Description,
    byte[]? ThumbnailBytes);

/// <summary>
/// Extracts the first HTTP/HTTPS URL in a message and fetches OpenGraph/HTML metadata
/// (plus optional preview image bytes) over <see cref="IHttpClient"/> for outbound link previews.
/// </summary>
public static partial class LinkPreviewFetcher
{
    private const long MaxHtmlBytes = 512 * 1024;
    private const long MaxImageBytes = 1024 * 1024;

    [GeneratedRegex(@"https?://[^\s<>""'()\[\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex(@"\b([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AttrRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleTagRegex();

    public static string? ExtractFirstUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match m = UrlRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }

        string raw = m.Value.TrimEnd('.', ',', ';', '!', '?');
        return Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            ? raw
            : null;
    }

    public static async Task<LinkPreviewData?> FetchAsync(
        IHttpClient httpClient,
        string text,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        string? urlStr = ExtractFirstUrl(text);
        if (urlStr is null || !Uri.TryCreate(urlStr, UriKind.Absolute, out Uri? url))
        {
            return null;
        }

        try
        {
            byte[] htmlBytes = await httpClient.GetBytesAsync(url, MaxHtmlBytes, ct).ConfigureAwait(false);
            string html = Encoding.UTF8.GetString(htmlBytes);
            LinkPreviewData parsed = ParseHtml(urlStr, html);

            byte[]? thumb = null;
            if (!string.IsNullOrWhiteSpace(parsed.CanonicalUrl)
                && TryFindImageUrl(html, url) is Uri imgUri)
            {
                try
                {
                    thumb = await httpClient.GetBytesAsync(imgUri, MaxImageBytes, ct).ConfigureAwait(false);
                }
                catch
                {
                    thumb = null;
                }
            }

            return parsed with { ThumbnailBytes = thumb };
        }
        catch
        {
            return null;
        }
    }

    internal static LinkPreviewData ParseHtml(string matchedUrl, string html)
    {
        string? ogTitle = null;
        string? ogDesc = null;
        string? ogUrl = null;
        string? metaDesc = null;

        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            string? key = null;
            string? content = null;
            foreach (Match attr in AttrRegex().Matches(tag.Value))
            {
                string name = attr.Groups[1].Value.ToLowerInvariant();
                string val = attr.Groups[2].Success ? attr.Groups[2].Value : attr.Groups[3].Value;
                if (name is "property" or "name")
                {
                    key = val.Trim().ToLowerInvariant();
                }
                else if (name == "content")
                {
                    content = WebUtility.HtmlDecode(val).Trim();
                }
            }

            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            switch (key)
            {
                case "og:title": ogTitle ??= content; break;
                case "og:description": ogDesc ??= content; break;
                case "og:url": ogUrl ??= content; break;
                case "description": metaDesc ??= content; break;
            }
        }

        if (ogTitle is null)
        {
            Match tMatch = TitleTagRegex().Match(html);
            if (tMatch.Success)
            {
                string t = WebUtility.HtmlDecode(tMatch.Groups[1].Value).Trim();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    ogTitle = t;
                }
            }
        }

        return new LinkPreviewData(
            matchedUrl,
            ogUrl ?? matchedUrl,
            ogTitle,
            ogDesc ?? metaDesc,
            ThumbnailBytes: null);
    }

    private static Uri? TryFindImageUrl(string html, Uri baseUri)
    {
        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            string? key = null;
            string? content = null;
            foreach (Match attr in AttrRegex().Matches(tag.Value))
            {
                string name = attr.Groups[1].Value.ToLowerInvariant();
                string val = attr.Groups[2].Success ? attr.Groups[2].Value : attr.Groups[3].Value;
                if (name is "property" or "name") key = val.Trim().ToLowerInvariant();
                else if (name == "content") content = WebUtility.HtmlDecode(val).Trim();
            }
            if (key == "og:image" && !string.IsNullOrWhiteSpace(content))
            {
                if (Uri.TryCreate(baseUri, content, out Uri? abs) && abs.Scheme is "http" or "https")
                {
                    return abs;
                }
            }
        }
        return null;
    }
}
