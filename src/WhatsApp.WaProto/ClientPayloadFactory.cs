using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.WaProto;

/// <summary>
/// Builds Noise handshake registration and login ClientPayload frames using protoc-generated schemas.
/// </summary>
public static class ClientPayloadFactory
{
    public static readonly ClientPayload.Types.UserAgent.Types.AppVersion DefaultWebVersion = new()
    {
        Primary = 2,
        Secondary = 3000,
        Tertiary = 1054751086
    };

    public static ClientPayload.Types.UserAgent CreateDefaultUserAgent() => new()
    {
        Platform = ClientPayload.Types.UserAgent.Types.Platform.Web,
        AppVersion = DefaultWebVersion.Clone(),
        Mcc = "000",
        Mnc = "000",
        OsVersion = "0.1.0",
        Manufacturer = string.Empty,
        Device = "Desktop",
        OsBuildNumber = "0.1.0",
        ReleaseChannel = ClientPayload.Types.UserAgent.Types.ReleaseChannel.Release,
        LocaleLanguageIso6391 = "en",
        LocaleCountryIso31661Alpha2 = "US"
    };

    public static byte[] BuildRegistrationPayload(
        uint registrationId,
        byte[] identityPublicKey,
        uint signedPreKeyId,
        byte[] signedPreKeyPublic,
        byte[] signedPreKeySignature,
        DeviceProps? deviceProps = null,
        string? pushName = null)
    {
        var userAgent = CreateDefaultUserAgent();
        string versionStr = $"{userAgent.AppVersion.Primary}.{userAgent.AppVersion.Secondary}.{userAgent.AppVersion.Tertiary}";
        byte[] buildHash = MD5.HashData(Encoding.UTF8.GetBytes(versionStr));

        deviceProps ??= new DeviceProps
        {
            Os = "Windows",
            PlatformType = DeviceProps.Types.PlatformType.Chrome,
            RequireFullSync = false
        };

        byte[] regIdBytes = [
            (byte)(registrationId >> 24),
            (byte)(registrationId >> 16),
            (byte)(registrationId >> 8),
            (byte)(registrationId & 0xFF)
        ];

        byte[] skeyIdBytes = [
            (byte)((signedPreKeyId >> 16) & 0xFF),
            (byte)((signedPreKeyId >> 8) & 0xFF),
            (byte)(signedPreKeyId & 0xFF)
        ];

        var regData = new ClientPayload.Types.DevicePairingRegistrationData
        {
            ERegid = ByteString.CopyFrom(regIdBytes),
            EKeytype = ByteString.CopyFrom(5),
            EIdent = ByteString.CopyFrom(identityPublicKey),
            ESkeyId = ByteString.CopyFrom(skeyIdBytes),
            ESkeyVal = ByteString.CopyFrom(signedPreKeyPublic),
            ESkeySig = ByteString.CopyFrom(signedPreKeySignature),
            BuildHash = ByteString.CopyFrom(buildHash),
            DeviceProps = deviceProps.ToByteString()
        };

        var payload = new ClientPayload
        {
            Passive = false,
            Pull = false,
            ConnectType = ClientPayload.Types.ConnectType.WifiUnknown,
            ConnectReason = ClientPayload.Types.ConnectReason.UserActivated,
            UserAgent = userAgent,
            WebInfo = new ClientPayload.Types.WebInfo
            {
                WebSubPlatform = ClientPayload.Types.WebInfo.Types.WebSubPlatform.WebBrowser
            },
            DevicePairingData = regData
        };

        if (!string.IsNullOrEmpty(pushName))
        {
            payload.PushName = pushName;
        }

        return payload.ToByteArray();
    }

    public static byte[] BuildLoginPayload(
        ulong username,
        uint deviceId,
        int loginCounter = 0)
    {
        var payload = new ClientPayload
        {
            Username = username,
            Device = deviceId,
            Passive = false,
            Pull = true,
            ConnectType = ClientPayload.Types.ConnectType.WifiUnknown,
            ConnectReason = ClientPayload.Types.ConnectReason.UserActivated,
            Lc = loginCounter,
            LidDbMigrated = false,
            UserAgent = CreateDefaultUserAgent(),
            WebInfo = new ClientPayload.Types.WebInfo
            {
                WebSubPlatform = ClientPayload.Types.WebInfo.Types.WebSubPlatform.WebBrowser
            }
        };

        return payload.ToByteArray();
    }
}
