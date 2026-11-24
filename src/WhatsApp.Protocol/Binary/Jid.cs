namespace WhatsApp.Protocol.Binary;

public enum Server : byte
{
    Pn = 0,
    Lid = 1,
    Group = 2,
    Broadcast = 3,
    Newsletter = 4,
    Hosted = 5,
    HostedLid = 6,
    Messenger = 7,
    Interop = 8,
    Bot = 9,
    Call = 11
}

public static class ServerExtensions
{
    public static string ToServerString(this Server server) => server switch
    {
        Server.Pn => "s.whatsapp.net",
        Server.Lid => "lid",
        Server.Group => "g.us",
        Server.Broadcast => "broadcast",
        Server.Newsletter => "newsletter",
        Server.Hosted => "hosted",
        Server.HostedLid => "hosted.lid",
        Server.Messenger => "msgr",
        Server.Interop => "interop",
        Server.Bot => "bot",
        Server.Call => "call",
        _ => "s.whatsapp.net"
    };

    public static bool TryParseServer(string s, out Server server)
    {
        switch (s)
        {
            case "s.whatsapp.net":
            case "c.us":
                server = Server.Pn;
                return true;
            case "lid":
                server = Server.Lid;
                return true;
            case "g.us":
                server = Server.Group;
                return true;
            case "broadcast":
                server = Server.Broadcast;
                return true;
            case "newsletter":
                server = Server.Newsletter;
                return true;
            case "hosted":
                server = Server.Hosted;
                return true;
            case "hosted.lid":
                server = Server.HostedLid;
                return true;
            case "msgr":
                server = Server.Messenger;
                return true;
            case "interop":
                server = Server.Interop;
                return true;
            case "bot":
                server = Server.Bot;
                return true;
            case "call":
                server = Server.Call;
                return true;
            default:
                server = Server.Pn;
                return false;
        }
    }

    public static bool RendersAgent(this Server server) =>
        server is not (Server.Pn or Server.Lid or Server.Hosted or Server.HostedLid);

    public static bool SupportsAdJid(this Server server) =>
        server is Server.Pn or Server.Lid or Server.Hosted or Server.HostedLid;

    public static byte ToDomainType(this Server server) => server switch
    {
        Server.Pn => 0,
        Server.Lid => 1,
        Server.Hosted => 128,
        Server.HostedLid => 129,
        _ => 0
    };
}

public readonly record struct Jid(string User, Server Server, byte Agent = 0, ushort Device = 0, ushort Integrator = 0)
{
    public static Jid Create(string user, Server server, byte agent = 0, ushort device = 0) =>
        new(user, server, agent, device);

    public static Jid Pn(string user) => new(user, Server.Pn);
    public static Jid PnDevice(string user, ushort device) => new(user, Server.Pn, 0, device);
    public static Jid Lid(string user) => new(user, Server.Lid);
    public static Jid LidDevice(string user, ushort device) => new(user, Server.Lid, 0, device);

    public static Jid Parse(string s)
    {
        if (TryParse(s, out var jid))
        {
            return jid;
        }
        throw new FormatException($"Invalid JID string format: '{s}'");
    }

    public static bool TryParse(string s, out Jid jid)
    {
        jid = default;
        if (string.IsNullOrEmpty(s)) return false;

        int atIndex = s.IndexOf('@');
        if (atIndex < 0)
        {
            if (ServerExtensions.TryParseServer(s, out var srv))
            {
                jid = new Jid(string.Empty, srv);
                return true;
            }
            return false;
        }

        string userPart = s[..atIndex];
        string serverPart = s[(atIndex + 1)..];

        if (!ServerExtensions.TryParseServer(serverPart, out var server))
        {
            return false;
        }

        // LID user part can contain dots which are not agent separators
        if (server == Server.Lid)
        {
            ushort lidDevice = 0;
            string lidUser = userPart;
            int colonIdx = userPart.LastIndexOf(':');
            if (colonIdx >= 0 && ushort.TryParse(userPart[(colonIdx + 1)..], out lidDevice))
            {
                lidUser = userPart[..colonIdx];
            }
            jid = new Jid(lidUser, Server.Lid, 0, lidDevice);
            return true;
        }

        ushort device = 0;
        string userBeforeColon = userPart;
        int colon = userPart.LastIndexOf(':');
        if (colon >= 0 && ushort.TryParse(userPart[(colon + 1)..], out var parsedDev))
        {
            device = parsedDev;
            userBeforeColon = userPart[..colon];
        }

        byte agent = 0;
        string finalUser = userBeforeColon;
        int dot = userBeforeColon.LastIndexOf('.');
        if (dot >= 0 && byte.TryParse(userBeforeColon[(dot + 1)..], out var parsedAgent))
        {
            agent = parsedAgent;
            finalUser = userBeforeColon[..dot];
        }

        jid = new Jid(finalUser, server, agent, device);
        return true;
    }

    public override string ToString()
    {
        if (string.IsNullOrEmpty(User))
        {
            return Server.ToServerString();
        }

        var sb = new System.Text.StringBuilder(User);
        if (Agent > 0 && Server.RendersAgent())
        {
            sb.Append('.').Append(Agent);
        }
        if (Device > 0)
        {
            sb.Append(':').Append(Device);
        }
        sb.Append('@').Append(Server.ToServerString());
        return sb.ToString();
    }

    public Jid ToNonAd() => new(User, Server);
    public string ToNonAdString() => $"{User}@{Server.ToServerString()}";
}
