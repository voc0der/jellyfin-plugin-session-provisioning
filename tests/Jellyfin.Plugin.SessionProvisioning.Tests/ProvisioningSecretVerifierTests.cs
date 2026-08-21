using Jellyfin.Plugin.SessionProvisioning.Security;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

public static class ProvisioningSecretVerifierTests
{
    private const string Secret = "aGVsbG8td29ybGQtdGhpcy1pcy1hLXRlc3Qtc2VjcmV0";

    private static string SecretHash => ProvisioningSecretVerifier.ComputeHashHex(Secret);

    [Fact]
    public static void Verify_CorrectSecret_Succeeds()
    {
        Assert.True(ProvisioningSecretVerifier.Verify(SecretHash, Secret));
    }

    [Fact]
    public static void Verify_UppercaseConfiguredHash_Succeeds()
    {
        Assert.True(ProvisioningSecretVerifier.Verify(SecretHash.ToUpperInvariant(), Secret));
    }

    [Fact]
    public static void Verify_SurroundingWhitespaceInConfiguredHash_Succeeds()
    {
        Assert.True(ProvisioningSecretVerifier.Verify("  " + SecretHash + "\n", Secret));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" ")]
    public static void Verify_WrongOrMissingSecret_Fails(string? presented)
    {
        Assert.False(ProvisioningSecretVerifier.Verify(SecretHash, presented));
    }

    [Fact]
    public static void Verify_SecretDifferingByOneCharacter_Fails()
    {
        Assert.False(ProvisioningSecretVerifier.Verify(SecretHash, Secret[..^1] + "X"));
    }

    [Fact]
    public static void Verify_SecretPrefix_Fails()
    {
        Assert.False(ProvisioningSecretVerifier.Verify(SecretHash, Secret[..10]));
    }

    // Fail-closed: an unusable configured hash must never authorise anything.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-hex-at-all")]
    [InlineData("deadbeef")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public static void Verify_UnusableConfiguredHash_Fails(string? configuredHash)
    {
        Assert.False(ProvisioningSecretVerifier.Verify(configuredHash, Secret));
        Assert.False(ProvisioningSecretVerifier.IsConfigured(configuredHash));
    }

    [Fact]
    public static void Verify_HashTooLongByOneByte_Fails()
    {
        Assert.False(ProvisioningSecretVerifier.Verify(SecretHash + "ab", Secret));
    }

    [Fact]
    public static void Verify_HashOfDifferentSecret_Fails()
    {
        var otherHash = ProvisioningSecretVerifier.ComputeHashHex("a-completely-different-secret");

        Assert.False(ProvisioningSecretVerifier.Verify(otherHash, Secret));
    }

    [Fact]
    public static void IsConfigured_WellFormedHash_IsTrue()
    {
        Assert.True(ProvisioningSecretVerifier.IsConfigured(SecretHash));
    }

    [Fact]
    public static void ComputeHashHex_MatchesKnownSha256Vector()
    {
        // echo -n "abc" | sha256sum
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ProvisioningSecretVerifier.ComputeHashHex("abc"));
    }

    [Fact]
    public static void ComputeHashHex_IsLowercaseHex()
    {
        var hash = ProvisioningSecretVerifier.ComputeHashHex(Secret);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public static void HeaderName_IsStable()
    {
        // The provisioning service depends on this exact header name.
        Assert.Equal("X-Session-Provisioning-Secret", ProvisioningSecretVerifier.HeaderName);
    }
}
