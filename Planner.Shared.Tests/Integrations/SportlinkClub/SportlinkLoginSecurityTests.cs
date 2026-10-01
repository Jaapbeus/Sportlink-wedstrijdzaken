using System.Security.Cryptography;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public sealed class SportlinkLoginSecurityTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Generate_matches_rfc6238_sha1_vectors(long timestamp, string expected)
    {
        // RFC 6238 Appendix B test key for SHA1, encoded as Base32.
        var actual = SportlinkTotp.Generate("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTimeOffset.FromUnixTimeSeconds(timestamp), digits: 8);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("SHA256", 59, "46119246")]
    [InlineData("SHA256", 1111111109, "68084774")]
    [InlineData("SHA256", 1111111111, "67062674")]
    [InlineData("SHA256", 1234567890, "91819424")]
    [InlineData("SHA256", 2000000000, "90698825")]
    [InlineData("SHA256", 20000000000, "77737706")]
    [InlineData("SHA512", 59, "90693936")]
    [InlineData("SHA512", 1111111109, "25091201")]
    [InlineData("SHA512", 1111111111, "99943326")]
    [InlineData("SHA512", 1234567890, "93441116")]
    [InlineData("SHA512", 2000000000, "38618901")]
    [InlineData("SHA512", 20000000000, "47863826")]
    public void Generate_matches_rfc6238_sha256_and_sha512_vectors(string algorithm, long timestamp, string expected)
    {
        // RFC 6238 Appendix B keys are public test vectors, encoded here to exercise Base32 input.
        var asciiKey = algorithm == "SHA256" ? "12345678901234567890123456789012" : "1234567890123456789012345678901234567890123456789012345678901234";
        var encodedKey = EncodeBase32(System.Text.Encoding.ASCII.GetBytes(asciiKey));
        var actual = SportlinkTotp.Generate(encodedKey, DateTimeOffset.FromUnixTimeSeconds(timestamp), algorithm, digits: 8);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("sha1", 6)]
    [InlineData("SHA256", 6)]
    [InlineData("SHA512", 8)]
    public void Generate_returns_requested_width_and_accepts_normalized_secret(string algorithm, int digits)
    {
        var code = SportlinkTotp.Generate("  gezd gnbv gy3t qojq gezdgnbvgy3tqojq  ", DateTimeOffset.FromUnixTimeSeconds(59), algorithm, digits);
        Assert.Equal(digits, code.Length);
        Assert.All(code, character => Assert.True(char.IsAsciiDigit(character)));
    }

    [Theory]
    [InlineData(5, 30, "SHA1")]
    [InlineData(6, 0, "SHA1")]
    [InlineData(6, 30, "MD5")]
    public void Generate_rejects_invalid_parameters(int digits, int period, string algorithm)
    {
        Assert.ThrowsAny<ArgumentException>(() => SportlinkTotp.Generate("JBSWY3DPEHPK3PXP", DateTimeOffset.UtcNow, algorithm, digits, period));
    }

    [Theory]
    [InlineData("not base32!")]
    [InlineData("A")]
    [InlineData("MZ======")]
    public void Generate_rejects_invalid_base32(string secret)
    {
        Assert.Throws<ArgumentException>(() => SportlinkTotp.Generate(secret, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Credentials_never_reveal_secrets_in_ToString()
    {
        var credentials = new SportlinkLoginCredentials { Username = "user-value", Password = "password-value", TotpSecret = "totp-value" };
        var text = credentials.ToString();
        Assert.DoesNotContain(credentials.Username, text);
        Assert.DoesNotContain(credentials.Password, text);
        Assert.DoesNotContain(credentials.TotpSecret, text);
    }

    [Fact]
    public void Protector_round_trips_and_binds_ciphertext_to_context()
    {
        var protector = new SportlinkCredentialProtector(RandomNumberGenerator.GetBytes(32));
        var credentials = new SportlinkLoginCredentials { Username = "test-user", Password = "test-password", TotpSecret = "JBSWY3DPEHPK3PXP" };
        var encrypted = protector.Protect(credentials, "club-a", "admin");

        var restored = protector.Unprotect(encrypted, "club-a", "admin");
        Assert.Equal(credentials.Username, restored.Username);
        Assert.Equal(credentials.Password, restored.Password);
        Assert.Equal(credentials.TotpSecret, restored.TotpSecret);
        Assert.Equal("SHA1", restored.TotpAlgorithm);
        Assert.Equal(6, restored.TotpDigits);
        Assert.Equal(30, restored.TotpPeriodSeconds);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(encrypted, "club-b", "admin"));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(encrypted, "club-a", "planner"));

        var refresh = protector.ProtectSecret("opaque-refresh-token", "club-a", "admin", "refresh-token");
        Assert.Equal("opaque-refresh-token", protector.UnprotectSecret(refresh, "club-a", "admin", "refresh-token"));
        Assert.Throws<CryptographicException>(() => protector.UnprotectSecret(refresh, "club-a", "admin", "credentials"));
    }

    [Fact]
    public void Protector_rejects_tampering_and_wrong_key_with_same_safe_error()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var protector = new SportlinkCredentialProtector(key);
        var encrypted = protector.Protect(new SportlinkLoginCredentials { Password = "test-password" }, "club-a", "admin");
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 1;
        var tampered = Convert.ToBase64String(bytes);

        var tamperError = Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered, "club-a", "admin"));
        var wrongKeyError = Assert.Throws<CryptographicException>(() => new SportlinkCredentialProtector(RandomNumberGenerator.GetBytes(32)).Unprotect(encrypted, "club-a", "admin"));
        Assert.Equal(tamperError.Message, wrongKeyError.Message);
    }

    [Fact]
    public void Protector_requires_exactly_32_byte_key()
    {
        Assert.Throws<ArgumentException>(() => new SportlinkCredentialProtector(new byte[31]));
    }

    [Fact]
    public void Protector_rejects_opaque_secrets_over_32_kibibytes()
    {
        var protector = new SportlinkCredentialProtector(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<ArgumentException>(() => protector.ProtectSecret(new string('x', 32 * 1024 + 1), "club-a", "admin", "refresh-token"));
    }

    private static string EncodeBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
                buffer &= (1 << bits) - 1;
            }
        }
        if (bits > 0)
            output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}
