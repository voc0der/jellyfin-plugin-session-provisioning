using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SessionProvisioning.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the hex-encoded SHA-256 hash of the provisioning secret.
    /// </summary>
    /// <remarks>
    /// The plaintext secret is deliberately never stored, displayed, or recoverable.
    /// While this is unset or malformed, minting is disabled entirely.
    /// </remarks>
    public string? ProvisioningSecretHash { get; set; }
}
