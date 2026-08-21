using Jellyfin.Plugin.SessionProvisioning.Security;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

public static class ProvisioningSecretSourceTests
{
    private const string Hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static ProvisioningSecretSource Source(
        string? hashVariable = null,
        string? hashFileVariable = null,
        Func<string, string>? readAllText = null)
        => new(
            name => name switch
            {
                ProvisioningSecretSource.HashVariable => hashVariable,
                ProvisioningSecretSource.HashFileVariable => hashFileVariable,
                _ => null
            },
            readAllText ?? (_ => throw new FileNotFoundException()));

    [Fact]
    public static void GetConfiguredHash_FromEnvironmentVariable()
    {
        Assert.Equal(Hash, Source(hashVariable: Hash).GetConfiguredHash());
    }

    [Fact]
    public static void GetConfiguredHash_FromFile()
    {
        var source = Source(hashFileVariable: "/run/secrets/hash", readAllText: _ => Hash + "\n");

        Assert.Equal(Hash, source.GetConfiguredHash());
    }

    [Fact]
    public static void GetConfiguredHash_FilePathWins_WhenBothAreSet()
    {
        var source = Source(
            hashVariable: "0000000000000000000000000000000000000000000000000000000000000000",
            hashFileVariable: "/run/secrets/hash",
            readAllText: _ => Hash);

        Assert.Equal(Hash, source.GetConfiguredHash());
    }

    [Fact]
    public static void GetConfiguredHash_TrimsSurroundingWhitespace()
    {
        Assert.Equal(Hash, Source(hashVariable: "  " + Hash + "  ").GetConfiguredHash());
        Assert.Equal(Hash, Source(hashFileVariable: "/f", readAllText: _ => "\n " + Hash + " \n").GetConfiguredHash());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public static void GetConfiguredHash_NothingConfigured_IsNull(string? value)
    {
        Assert.Null(Source(hashVariable: value).GetConfiguredHash());
    }

    [Fact]
    public static void GetConfiguredHash_EmptyFile_IsNull()
    {
        Assert.Null(Source(hashFileVariable: "/f", readAllText: _ => "\n  \n").GetConfiguredHash());
    }

    // Fail closed: an unreadable secret file must never fall back to some other value.
    [Fact]
    public static void GetConfiguredHash_UnreadableFile_IsNull()
    {
        Assert.Null(Source(hashFileVariable: "/f", readAllText: _ => throw new UnauthorizedAccessException()).GetConfiguredHash());
        Assert.Null(Source(hashFileVariable: "/f", readAllText: _ => throw new FileNotFoundException()).GetConfiguredHash());
        Assert.Null(Source(hashFileVariable: "/f", readAllText: _ => throw new DirectoryNotFoundException()).GetConfiguredHash());
    }

    [Fact]
    public static void GetConfiguredHash_UnreadableFile_DoesNotFallBackToEnvironmentVariable()
    {
        var source = Source(
            hashVariable: Hash,
            hashFileVariable: "/run/secrets/missing",
            readAllText: _ => throw new FileNotFoundException());

        Assert.Null(source.GetConfiguredHash());
    }

    [Fact]
    public static void VariableNames_AreStable()
    {
        // Deployments depend on these exact names.
        Assert.Equal("SESSION_PROVISIONING_SECRET_HASH", ProvisioningSecretSource.HashVariable);
        Assert.Equal("SESSION_PROVISIONING_SECRET_HASH_FILE", ProvisioningSecretSource.HashFileVariable);
    }

    [Fact]
    public static void EndToEnd_EnvironmentSuppliedHash_VerifiesTheSecret()
    {
        const string Secret = "a-random-256-bit-secret-stand-in";
        var source = Source(hashVariable: ProvisioningSecretVerifier.ComputeHashHex(Secret));

        Assert.True(ProvisioningSecretVerifier.Verify(source.GetConfiguredHash(), Secret));
        Assert.False(ProvisioningSecretVerifier.Verify(source.GetConfiguredHash(), "wrong"));
    }
}
