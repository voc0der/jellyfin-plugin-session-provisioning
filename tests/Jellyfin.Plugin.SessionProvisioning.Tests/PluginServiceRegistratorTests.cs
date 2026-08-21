using Jellyfin.Plugin.SessionProvisioning;
using Jellyfin.Plugin.SessionProvisioning.Api;
using Jellyfin.Plugin.SessionProvisioning.Security;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

public static class PluginServiceRegistratorTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public static void RegisterServices_RegistersTheControllerDependencies()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<ProvisioningSecretSource>());
        Assert.NotNull(provider.GetService<MintRateLimiter>());
    }

    // The window has to be shared across requests, or the limit means nothing.
    [Fact]
    public static void RegisterServices_RateLimiterIsASingleton()
    {
        using var provider = BuildProvider();

        Assert.Same(provider.GetRequiredService<MintRateLimiter>(), provider.GetRequiredService<MintRateLimiter>());
    }

    /// <summary>
    /// Guards the wiring itself: if the controller gains a constructor parameter that
    /// nothing registers, Jellyfin fails to build it and every request 500s. This fails
    /// at build-a-controller time instead, in a unit test.
    /// </summary>
    [Fact]
    public static void Controller_IsConstructibleFromTheContainer()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

        // Stand-ins for the services Jellyfin itself provides.
        services.AddSingleton(Substitute.For<ISessionManager>());
        services.AddSingleton(Substitute.For<IUserManager>());
        services.AddSingleton(Substitute.For<IPluginManager>());
        services.AddSingleton<ILogger<SessionProvisioningController>>(NullLogger<SessionProvisioningController>.Instance);

        using var provider = services.BuildServiceProvider();

        var controller = ActivatorUtilities.CreateInstance<SessionProvisioningController>(provider);

        Assert.NotNull(controller);
    }
}
