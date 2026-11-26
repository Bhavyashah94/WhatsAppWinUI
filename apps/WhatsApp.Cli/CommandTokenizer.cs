using System.Text;

namespace WhatsApp.Cli;

public static class CommandTokenizer
{
    public static IReadOnlyList<string> Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';
        bool hasTokenContent = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < input.Length && (input[i + 1] == quoteChar || input[i + 1] == '\\'))
                {
                    current.Append(input[++i]);
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
                hasTokenContent = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasTokenContent || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasTokenContent = false;
                }
            }
            else
            {
                current.Append(c);
                hasTokenContent = true;
            }
        }

        if (inQuotes)
        {
            throw new FormatException("Unterminated quoted string in command.");
        }

        if (hasTokenContent || current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
