using System.Reflection;
using System.Text.Json;

namespace WhatsApp.Protocol.Binary;

public static class TokenDictionary
{
    private static readonly string[] SingleTokens;
    private static readonly string[][] DoubleTokens;
    private static readonly Dictionary<string, (byte dict, byte index)> TokenLookup;

    static TokenDictionary()
    {
        var assembly = typeof(TokenDictionary).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("tokens.json", StringComparison.OrdinalIgnoreCase));

        using Stream? stream = resourceName != null
            ? assembly.GetManifestResourceStream(resourceName)
            : File.Exists("tokens.json") ? File.OpenRead("tokens.json") : null;

        if (stream == null)
        {
            throw new InvalidOperationException("Could not find embedded or local tokens.json");
        }

        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var singleList = new List<string>();
        foreach (var item in root.GetProperty("single_byte").EnumerateArray())
        {
            singleList.Add(item.GetString() ?? string.Empty);
        }
        SingleTokens = [.. singleList];

        var doubleList = new List<string[]>();
        foreach (var table in root.GetProperty("double_byte").EnumerateArray())
        {
            var subList = new List<string>();
            foreach (var item in table.EnumerateArray())
            {
                subList.Add(item.GetString() ?? string.Empty);
            }
            doubleList.Add([.. subList]);
        }
        DoubleTokens = [.. doubleList];

        TokenLookup = new Dictionary<string, (byte dict, byte index)>(StringComparer.Ordinal);

        // Index single byte tokens (dict = 0xFF indicates single byte)
        for (int i = 0; i < SingleTokens.Length; i++)
        {
            var tok = SingleTokens[i];
            if (!string.IsNullOrEmpty(tok) && !TokenLookup.ContainsKey(tok))
            {
                TokenLookup[tok] = (0xFF, (byte)i);
            }
        }

        // Index double byte tokens
        for (byte d = 0; d < DoubleTokens.Length; d++)
        {
            var table = DoubleTokens[d];
            for (int i = 0; i < table.Length; i++)
            {
                var tok = table[i];
                if (!string.IsNullOrEmpty(tok) && !TokenLookup.ContainsKey(tok))
                {
                    TokenLookup[tok] = (d, (byte)i);
                }
            }
        }
    }

    public static string GetSingleToken(int index)
    {
        if (index >= 0 && index < SingleTokens.Length)
        {
            return SingleTokens[index];
        }
        throw new ArgumentOutOfRangeException(nameof(index), $"Invalid single token index: {index}");
    }

    public static string GetDoubleToken(int dictIndex, int tokenIndex)
    {
        if (dictIndex >= 0 && dictIndex < DoubleTokens.Length)
        {
            var table = DoubleTokens[dictIndex];
            if (tokenIndex >= 0 && tokenIndex < table.Length)
            {
                return table[tokenIndex];
            }
        }
        throw new ArgumentOutOfRangeException($"Invalid double token dict: {dictIndex}, index: {tokenIndex}");
    }

    public static bool TryGetToken(string token, out byte dict, out byte index)
    {
        if (TokenLookup.TryGetValue(token, out var pair))
        {
            dict = pair.dict;
            index = pair.index;
            return true;
        }

        dict = 0;
        index = 0;
        return false;
    }
}
