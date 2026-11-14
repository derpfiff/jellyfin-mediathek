using Jellyfin.Plugin.Mediathek.Ard;
using Jellyfin.Plugin.Mediathek.Channel;
using Jellyfin.Plugin.Mediathek.Live;
using MediaBrowser.Controller.Channels;
using Jellyfin.Plugin.Mediathek.Mvw;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Mediathek;

/// <summary>Meldet die Dienste des Plugins an.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public const string HttpClientName = "Mediathek";

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(40);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Mediathek/0.2");
        });
        serviceCollection.AddSingleton<MvwClient>();
        serviceCollection.AddSingleton<ArdClient>();
        serviceCollection.AddSingleton<LiveService>();
        serviceCollection.AddSingleton<LiveProxy>();
        serviceCollection.AddHostedService<TunerRepair>();
        serviceCollection.AddSingleton<LiveWarmup>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<LiveWarmup>());
        // Jellyfin sammelt die Kanäle aus den angemeldeten Diensten ein.
        serviceCollection.AddSingleton<MediathekChannel>();
        serviceCollection.AddSingleton<IChannel>(provider => provider.GetRequiredService<MediathekChannel>());
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.Add<Web.ViaServerFilter>());
    }
}
