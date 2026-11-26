using System.Text;
using QRCoder;

namespace WhatsApp.Cli;

/// <summary>
/// Renders WhatsApp MD pairing payloads into terminal-scannable half-block or ASCII QR matrices
/// and redacts cryptographic secrets in CLI diagnostics.
/// </summary>
public static class QrConsoleRenderer
{
    public static string RenderUnicodeHalfBlocks(string payload, int quietZoneModules = 2)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        using var gen = new QRCodeGenerator();
        using QRCodeData data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        var rows = data.ModuleMatrix;
        int rawSize = rows.Count;
        // Pad by quietZoneModules on all sides (white background in QR terminology; modules are dark = true).
        int size = rawSize;
        bool GetDark(int r, int c) =>
            r >= 0 && r < size && c >= 0 && c < size && Convert.ToBoolean(rows[r][c]);

        var sb = new StringBuilder((size / 2 + 4) * (size + quietZoneModules * 2 + 2));
        int min = -quietZoneModules;
        int max = size + quietZoneModules;
        for (int r = min; r < max; r += 2)
        {
            for (int c = min; c < max; c++)
            {
                bool topDark = GetDark(r, c);
                bool botDark = (r + 1 < max) && GetDark(r + 1, c);
                // Invert so dark terminal background stays dark where dark module = false, white light modules = filled!
                // Wait: phones scan dark modules on light background cleanly using bright foreground blocks for light modules.
                bool topLight = !topDark;
                bool botLight = !botDark;
                char ch = (topLight, botLight) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    (false, false) => ' ',
                };
                sb.Append(ch);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static byte[] RenderPngBytes(string payload, int pixelsPerModule = 10)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        if (pixelsPerModule <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerModule));
        }

        using var gen = new QRCodeGenerator();
        using QRCodeData data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    public static string RedactSecret(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? "<none>" : $"<redacted:{bytes.Length}B>";

    public static string RedactString(string? value, int visibleTail = 4)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<none>";
        }
        if (value.Length <= visibleTail)
        {
            return new string('*', value.Length);
        }
        return string.Concat("***", value.AsSpan(value.Length - visibleTail));
    }
}
