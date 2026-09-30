using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Protects credentials with AES-256-GCM and binds ciphertext to its club and role.</summary>
public sealed class SportlinkCredentialProtector
{
    private const byte EnvelopeVersion = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaxCiphertextLength = 32 * 1024;
    private readonly byte[] _key;

    /// <param name="key">Externally supplied 32-byte key. Store separately from the credential database.</param>
    public SportlinkCredentialProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException("The credential protection key must be 32 bytes.", nameof(key));
        _key = key.ToArray();
    }

    public string Protect(SportlinkLoginCredentials credentials, string clubCode, string role)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            return ProtectBytes(plaintext, clubCode, role, "credentials");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Protects an opaque secret with a purpose-specific authenticated context.</summary>
    public string ProtectSecret(string secret, string clubCode, string role, string purpose)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            return ProtectBytes(plaintext, clubCode, role, purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Decrypts an opaque secret only for the same club, role, and purpose.</summary>
    public string UnprotectSecret(string ciphertext, string clubCode, string role, string purpose)
    {
        var plaintext = UnprotectBytes(ciphertext, clubCode, role, purpose);
        try
        {
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (DecoderFallbackException)
        {
            throw new CryptographicException("Credential data could not be decrypted.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public SportlinkLoginCredentials Unprotect(string ciphertext, string clubCode, string role)
    {
        var plaintext = UnprotectBytes(ciphertext, clubCode, role, "credentials");
        try
        {
            return JsonSerializer.Deserialize<SportlinkLoginCredentials>(plaintext)
                   ?? throw new CryptographicException("Credential data could not be decrypted.");
        }
        catch (JsonException)
        {
            throw new CryptographicException("Credential data could not be decrypted.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string ProtectBytes(byte[] plaintext, string clubCode, string role, string purpose)
    {
        if (plaintext.Length > MaxCiphertextLength)
            throw new ArgumentException("Secret data exceeds the supported size.");
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(clubCode, role, purpose));
        var envelope = new byte[1 + NonceLength + TagLength + ciphertext.Length];
        envelope[0] = EnvelopeVersion;
        nonce.CopyTo(envelope, 1);
        tag.CopyTo(envelope, 1 + NonceLength);
        ciphertext.CopyTo(envelope, 1 + NonceLength + TagLength);
        return Convert.ToBase64String(envelope);
    }

    private byte[] UnprotectBytes(string ciphertext, string clubCode, string role, string purpose)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ciphertext) || ciphertext.Length > ((MaxCiphertextLength + 29 + 2) / 3 * 4))
                throw new CryptographicException();
            var envelope = Convert.FromBase64String(ciphertext);
            if (envelope.Length < 1 + NonceLength + TagLength || envelope.Length > MaxCiphertextLength + 1 + NonceLength + TagLength || envelope[0] != EnvelopeVersion)
                throw new CryptographicException();
            var plaintext = new byte[envelope.Length - 1 - NonceLength - TagLength];
            using var aes = new AesGcm(_key, TagLength);
            aes.Decrypt(envelope.AsSpan(1, NonceLength), envelope.AsSpan(1 + NonceLength + TagLength),
                envelope.AsSpan(1 + NonceLength, TagLength), plaintext, AssociatedData(clubCode, role, purpose));
            return plaintext;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or JsonException)
        {
            // Uniform failure prevents callers from distinguishing malformed input from authentication failure.
            throw new CryptographicException("Credential data could not be decrypted.");
        }
    }

    private static byte[] AssociatedData(string clubCode, string role, string purpose)
    {
        ArgumentNullException.ThrowIfNull(clubCode);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(purpose);
        var club = Encoding.UTF8.GetBytes(clubCode);
        var roleBytes = Encoding.UTF8.GetBytes(role);
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        if (club.Length > 1024 || roleBytes.Length > 1024 || purposeBytes.Length is 0 or > 128)
            throw new ArgumentException("Credential context exceeds the supported size.");
        // Length-prefix fields so distinct (club, role) pairs cannot share an ambiguous encoding.
        var data = new byte[1 + 4 + club.Length + 4 + roleBytes.Length + 4 + purposeBytes.Length];
        data[0] = EnvelopeVersion;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(1, 4), club.Length);
        club.CopyTo(data, 5);
        var roleOffset = 5 + club.Length;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(roleOffset, 4), roleBytes.Length);
        roleBytes.CopyTo(data, roleOffset + 4);
        var purposeOffset = roleOffset + 4 + roleBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(purposeOffset, 4), purposeBytes.Length);
        purposeBytes.CopyTo(data, purposeOffset + 4);
        return data;
    }
}
