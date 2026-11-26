using WhatsApp.Api;

namespace WhatsApp.Cli;

public sealed record CliOptions
{
    public string Profile { get; init; } = "default";
    public string? DatabasePath { get; init; }
    public int DeviceId { get; init; } = 1;
    public string PushName { get; init; } = "WhatsApp CLI";
    public bool JsonEvents { get; init; }
    public bool QuietEvents { get; init; }
    public bool TraceStanzas { get; init; }
    public string? SingleCommand { get; init; }
    public Uri Endpoint { get; init; } = ConnectionOrchestratorOptions.DefaultChatWebSocketUri;

    public string ResolveDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(DatabasePath))
        {
            return Path.GetFullPath(DatabasePath);
        }

        string safeProfile = string.Concat(Profile.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (string.IsNullOrWhiteSpace(safeProfile))
        {
            safeProfile = "default";
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhatsAppWinUI",
            "profiles",
            safeProfile);
        return Path.Combine(root, "wa.db");
    }

    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string profile = "default";
        string? dbPath = null;
        int deviceId = 1;
        string pushName = "WhatsApp CLI";
        bool jsonEvents = false;
        bool quietEvents = false;
        bool traceStanzas = false;
        string? singleCmd = null;
        Uri endpoint = ConnectionOrchestratorOptions.DefaultChatWebSocketUri;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--profile":
                    profile = RequireValue(args, ref i, "--profile");
                    break;
                case "--db":
                    dbPath = RequireValue(args, ref i, "--db");
                    break;
                case "--device-id":
                    string rawId = RequireValue(args, ref i, "--device-id");
                    if (!int.TryParse(rawId, out deviceId) || deviceId <= 0)
                    {
                        throw new ArgumentException($"Invalid --device-id '{rawId}'.");
                    }
                    break;
                case "--push-name":
                    pushName = RequireValue(args, ref i, "--push-name");
                    break;
                case "--json-events":
                    jsonEvents = true;
                    break;
                case "--quiet-events":
                    quietEvents = true;
                    break;
                case "--trace-stanzas":
                    traceStanzas = true;
                    break;
                case "--cmd":
                    singleCmd = RequireValue(args, ref i, "--cmd");
                    break;
                case "--endpoint":
                    string rawUri = RequireValue(args, ref i, "--endpoint");
                    if (!Uri.TryCreate(rawUri, UriKind.Absolute, out Uri? parsed))
                    {
                        throw new ArgumentException($"Invalid --endpoint URI '{rawUri}'.");
                    }
                    endpoint = parsed;
                    break;
                case "-h":
                case "--help":
                    singleCmd = "help";
                    break;
                default:
                    // Treat trailing unflagged arguments as a direct one-shot command.
                    singleCmd = string.Join(' ', args.Skip(i).Select(QuoteIfNeeded));
                    i = args.Length;
                    break;
            }
        }

        return new CliOptions
        {
            Profile = profile,
            DatabasePath = dbPath,
            DeviceId = deviceId,
            PushName = pushName,
            JsonEvents = jsonEvents,
            QuietEvents = quietEvents,
            TraceStanzas = traceStanzas,
            SingleCommand = singleCmd,
            Endpoint = endpoint,
        };
    }

    private static string RequireValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for '{flag}'.");
        }
        return args[++index];
    }

    private static string QuoteIfNeeded(string arg) =>
        arg.Contains(' ') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
