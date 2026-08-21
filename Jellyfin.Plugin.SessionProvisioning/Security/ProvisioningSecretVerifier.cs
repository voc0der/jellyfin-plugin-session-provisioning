using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SessionProvisioning.Security;

/// <summary>
/// Verifies the secondary provisioning secret that gates the mint endpoint.
/// </summary>
/// <remarks>
/// This is the plugin's independent second gate, on top of Jellyfin's own elevation
/// policy. It fails closed: any missing, malformed, or mismatched value is a rejection.
/// See <c>docs/SECURITY.md</c> for why plain SHA-256 is sufficient here (the secret is
/// a uniformly random 256-bit machine value, not a human passphrase).
/// </remarks>
public static class ProvisioningSecretVerifier
{
    /// <summary>
    /// The request header carrying the provisioning secret.
    /// </summary>
    public const string HeaderName = "X-Session-Provisioning-Secret";

    /// <summary>
    /// Length in bytes of a SHA-256 digest.
    /// </summary>
    private const int Sha256ByteLength = 32;

    /// <summary>
    /// Verifies a presented secret against the configured hash.
    /// </summary>
    /// <param name="configuredHashHex">
    /// The configured SHA-256 hash of the provisioning secret, hex-encoded. A null,
    /// blank, or malformed value disables minting entirely.
    /// </param>
    /// <param name="presentedSecret">The secret presented by the caller.</param>
    /// <returns><c>true</c> only if the presented secret matches the configured hash.</returns>
    public static bool Verify(string? configuredHashHex, string? presentedSecret)
    {
        if (string.IsNullOrEmpty(presentedSecret))
        {
            return false;
        }

        if (!TryParseConfiguredHash(configuredHashHex, out var configuredHash))
        {
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[Sha256ByteLength];
        SHA256.HashData(Encoding.UTF8.GetBytes(presentedSecret), presentedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash);
    }

    /// <summary>
    /// Indicates whether a usable provisioning secret hash is configured.
    /// </summary>
    /// <param name="configuredHashHex">The configured hash, hex-encoded.</param>
    /// <returns><c>true</c> if the configured value is a well-formed SHA-256 hash.</returns>
    public static bool IsConfigured(string? configuredHashHex)
        => TryParseConfiguredHash(configuredHashHex, out _);

    /// <summary>
    /// Computes the hex-encoded SHA-256 hash of a secret, in the form this plugin stores.
    /// </summary>
    /// <param name="secret">The plaintext secret.</param>
    /// <returns>The lowercase hex-encoded SHA-256 hash.</returns>
    public static string ComputeHashHex(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private static bool TryParseConfiguredHash(string? configuredHashHex, out byte[] hash)
    {
        hash = [];

        if (string.IsNullOrWhiteSpace(configuredHashHex))
        {
            return false;
        }

        var trimmed = configuredHashHex.Trim();
        if (trimmed.Length != Sha256ByteLength * 2)
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(trimmed);
        }
        catch (FormatException)
        {
            return false;
        }

        return true;
    }
}
