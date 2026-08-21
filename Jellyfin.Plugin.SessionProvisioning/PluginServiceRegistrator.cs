using Jellyfin.Plugin.SessionProvisioning.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SessionProvisioning;

/// <summary>
/// Registers this plugin's services with Jellyfin's container.
/// </summary>
/// <remarks>
/// All are singletons for different reasons: <see cref="MintRateLimiter"/> must share
/// one window across every request, and <see cref="ProvisioningSecretSource"/> is
/// stateless (it re-reads the environment per call), so one instance is enough, and
/// <see cref="MintSerializer"/> is only a gate if every request shares it.
/// Registering them here rather than holding them in static fields keeps the controller
/// injectable, and therefore testable.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ProvisioningSecretSource>();
        serviceCollection.AddSingleton<MintRateLimiter>();
        serviceCollection.AddSingleton<MintSerializer>();
    }
}
