using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.SessionProvisioning;

/// <summary>
/// The main plugin.
/// </summary>
/// <remarks>
/// Deliberately stateless: no plugin configuration, no dashboard page, no stored
/// secrets. The provisioning secret hash is supplied by the deployment environment
/// (see <see cref="Security.ProvisioningSecretSource"/>), so the secret is never
/// typed into, displayed by, or persisted through Jellyfin's web UI.
/// </remarks>
public class Plugin : BasePlugin
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <remarks>
    /// <see cref="BasePlugin"/> leaves <c>Version</c> and the assembly paths unset
    /// until <c>SetAttributes</c> is called; Jellyfin's own <c>BasePlugin{T}</c> does
    /// that in its constructor. Since this plugin has no configuration and therefore
    /// does not derive from the generic base, it does the same work here — without it,
    /// <c>PluginManager.CreatePluginInstance</c> throws on <c>instance.Version</c> and
    /// disables the plugin.
    /// </remarks>
    public Plugin(IApplicationPaths applicationPaths)
    {
        var assembly = GetType().Assembly;
        var assemblyFilePath = assembly.Location;
        var dataFolderPath = Path.Combine(
            applicationPaths.PluginsPath,
            Path.GetFileNameWithoutExtension(assemblyFilePath));

        SetAttributes(assemblyFilePath, dataFolderPath, assembly.GetName().Version!);
    }

    /// <inheritdoc />
    public override string Name => "Session Provisioning";

    /// <inheritdoc />
    public override string Description => "Admin-authorized session provisioning for Jellyfin users.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8d4bcbe8-ddd2-4c3a-ba8f-a7b500943e6b");
}
