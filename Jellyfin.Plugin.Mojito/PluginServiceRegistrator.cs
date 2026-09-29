using System;
using System.IO;
using Jellyfin.Plugin.Mojito.Channel;
using Jellyfin.Plugin.Mojito.Providers;
using Jellyfin.Plugin.Mojito.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Mojito;

/// <summary>
/// Registers the Mojito services with the Jellyfin DI container. Jellyfin calls
/// this automatically when loading the plugin assembly.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers the plugin services with the Jellyfin DI container.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="applicationHost">The server application host.</param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MojitoHttp>();
        serviceCollection.AddSingleton<RadarrClient>();
        serviceCollection.AddSingleton<SonarrClient>();
        serviceCollection.AddSingleton<QbitClient>();

        // Same folder BasePlugin uses for the plugin data (PluginConfigurationsPath/AssemblyName),
        // resolved lazily because the Plugin instance may not exist yet at registration time.
        serviceCollection.AddSingleton<StreamSessionStore>(sp => new StreamSessionStore(
            Path.Combine(
                sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath,
                "Jellyfin.Plugin.Mojito")));
        serviceCollection.AddSingleton<MojitoSessionManager>();

        serviceCollection.AddSingleton<SessionOrchestrator>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SessionOrchestrator>());

        serviceCollection.AddSingleton<PlaybackEventsConsumer>();
        serviceCollection.AddSingleton<IEventConsumer<PlaybackStartEventArgs>>(sp => sp.GetRequiredService<PlaybackEventsConsumer>());
        serviceCollection.AddSingleton<IEventConsumer<PlaybackStopEventArgs>>(sp => sp.GetRequiredService<PlaybackEventsConsumer>());

        serviceCollection.AddSingleton<IChannel, MojitoChannel>();
    }
}
