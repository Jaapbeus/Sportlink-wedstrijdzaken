using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>RFC 6238 TOTP generation using standard Base32 encoded shared secrets.</summary>
public static class SportlinkTotp
{
    private const int MaxSecretLength = 1024;

    public static string Generate(string secret, DateTimeOffset utcNow, string algorithm = "SHA1", int digits = 6, int periodSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length is 0 or > MaxSecretLength)
            throw new ArgumentException("The TOTP secret length is invalid.", nameof(secret));
        if (digits is not (6 or 8))
            throw new ArgumentOutOfRangeException(nameof(digits), "TOTP supports 6 or 8 digits.");
        if (periodSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));

        var hashAlgorithm = algorithm?.Trim().ToUpperInvariant() switch
        {
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException("The TOTP algorithm is unsupported.", nameof(algorithm))
        };

        var key = DecodeBase32(secret);
        try
        {
            long unixSeconds = utcNow.ToUnixTimeSeconds();
            if (unixSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(utcNow), "TOTP time must be at or after the Unix epoch.");
            var counter = (ulong)(unixSeconds / periodSeconds);
            Span<byte> counterBytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);
            var hash = hashAlgorithm switch
            {
                { } name when name == HashAlgorithmName.SHA1 => HMACSHA1.HashData(key, counterBytes),
                { } name when name == HashAlgorithmName.SHA256 => HMACSHA256.HashData(key, counterBytes),
                _ => HMACSHA512.HashData(key, counterBytes)
            };
            try
            {
                var offset = hash[^1] & 0x0f;
                uint binary = (uint)(((hash[offset] & 0x7f) << 24) |
                                     (hash[offset + 1] << 16) |
                                     (hash[offset + 2] << 8) |
                                     hash[offset + 3]);
                uint modulus = digits == 6 ? 1_000_000u : 100_000_000u;
                return (binary % modulus).ToString(digits == 6 ? "D6" : "D8", System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(counterBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecodeBase32(string value)
    {
        // Whitespace is commonly introduced when a user copies a secret from a settings screen.
        var withoutWhitespace = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        int firstPadding = withoutWhitespace.IndexOf('=');
        var normalized = firstPadding < 0 ? withoutWhitespace : withoutWhitespace[..firstPadding];
        int suppliedPadding = withoutWhitespace.Length - normalized.Length;
        if (normalized.Length == 0 || normalized.Length > MaxSecretLength || normalized.Length % 8 is 1 or 3 or 6)
            throw new ArgumentException("The TOTP secret is not valid Base32.", nameof(value));
        if (firstPadding >= 0)
        {
            int expectedPadding = (8 - normalized.Length % 8) % 8;
            if (withoutWhitespace.AsSpan(firstPadding).IndexOfAnyExcept('=') >= 0 || suppliedPadding != expectedPadding || normalized.Length % 8 is not (0 or 2 or 4 or 5 or 7))
                throw new ArgumentException("The TOTP secret has invalid Base32 padding.", nameof(value));
        }

        var output = new byte[normalized.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var character in normalized)
        {
            int digit = character is >= 'A' and <= 'Z' ? character - 'A'
                : character is >= 'a' and <= 'z' ? character - 'a'
                : character is >= '2' and <= '7' ? character - '2' + 26
                : -1;
            if (digit < 0)
                throw new ArgumentException("The TOTP secret is not valid Base32.", nameof(value));
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }
        if (bits > 0 && buffer != 0)
            throw new ArgumentException("The TOTP secret has invalid Base32 padding bits.", nameof(value));
        return output;
    }
}
