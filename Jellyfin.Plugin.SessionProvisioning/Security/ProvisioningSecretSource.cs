using System;
using System.IO;

namespace Jellyfin.Plugin.SessionProvisioning.Security;

/// <summary>
/// Supplies the configured provisioning secret hash from the deployment environment.
/// </summary>
/// <remarks>
/// The hash is read from an environment variable or a file the operator controls
/// (root-owned, or a mounted secret), never from Jellyfin's plugin configuration. That
/// keeps the plugin stateless and keeps the secret out of the web UI entirely.
/// Values are read on each request, so rotating the file or restarting with a new
/// environment value takes effect without any plugin action.
/// </remarks>
public sealed class ProvisioningSecretSource
{
    /// <summary>
    /// Environment variable holding the hex-encoded SHA-256 hash of the secret.
    /// </summary>
    /// <remarks>
    /// Deliberately not prefixed <c>JELLYFIN_</c>: Jellyfin logs every environment
    /// variable starting with JELLYFIN_, DOTNET_, or ASPNETCORE_ at startup
    /// (<c>StartupHelpers.LogEnvironmentInfo</c>), which would print this value into
    /// the server log on every boot.
    /// </remarks>
    public const string HashVariable = "SESSION_PROVISIONING_SECRET_HASH";

    /// <summary>
    /// Environment variable holding a path to a file containing that hash.
    /// </summary>
    /// <remarks>
    /// Takes precedence over <see cref="HashVariable"/>: a file is the more careful
    /// way to supply the value, so it wins if an operator has configured both.
    /// </remarks>
    public const string HashFileVariable = "SESSION_PROVISIONING_SECRET_HASH_FILE";

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, string> _readAllText;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProvisioningSecretSource"/> class.
    /// </summary>
    public ProvisioningSecretSource()
        : this(Environment.GetEnvironmentVariable, File.ReadAllText)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProvisioningSecretSource"/> class
    /// with explicit environment and file accessors, for testing.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <param name="readAllText">Reads the full contents of a file.</param>
    public ProvisioningSecretSource(Func<string, string?> getEnvironmentVariable, Func<string, string> readAllText)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _readAllText = readAllText;
    }

    /// <summary>
    /// Gets the configured hash, or <c>null</c> when none is usable.
    /// </summary>
    /// <remarks>
    /// Fails closed: an unreadable file, an unset variable, or a blank value all yield
    /// <c>null</c>, which disables minting. Callers must not distinguish these cases in
    /// responses.
    /// </remarks>
    /// <returns>The hex-encoded hash, or <c>null</c>.</returns>
    public string? GetConfiguredHash()
    {
        var path = _getEnvironmentVariable(HashFileVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var fromFile = _readAllText(path).Trim();
                return string.IsNullOrEmpty(fromFile) ? null : fromFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        var fromEnvironment = _getEnvironmentVariable(HashVariable)?.Trim();
        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
    }
}
