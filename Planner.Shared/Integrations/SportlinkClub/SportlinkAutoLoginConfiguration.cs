using System.Security.Cryptography;

namespace Planner.Shared.Integrations.SportlinkClub;

public static class SportlinkAutoLoginConfiguration
{
    public const string KeySetting = "SportlinkAutoLoginEncryptionKey";

    /// <summary>Alleen een expliciete, geldige hostsleutel schakelt de nieuwe opslag in.</summary>
    public static SportlinkCredentialProtector? CreateProtector(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) return null;
        byte[]? bytes = null;
        try
        {
            bytes = Convert.FromBase64String(base64Key);
            return bytes.Length == 32 ? new SportlinkCredentialProtector(bytes) : null;
        }
        catch (FormatException) { return null; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
