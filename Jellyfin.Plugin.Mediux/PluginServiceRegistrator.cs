using Jellyfin.Plugin.Mediux.Client;
using Jellyfin.Plugin.Mediux.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Mediux;

/// <summary>
/// Registers plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<MediuxAuthHandler>();
        serviceCollection.AddHttpClient(Plugin.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(Plugin.ApiBaseUrl);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.Mediux/1.0");
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddHttpMessageHandler<MediuxAuthHandler>();

        serviceCollection.AddSingleton<MediuxApiClient>();
        serviceCollection.AddSingleton<MediuxSetBindingStore>();
        serviceCollection.AddSingleton<MediuxArtworkService>();
        serviceCollection.AddSingleton<MediuxPreviewService>();
        serviceCollection.AddSingleton<IScheduledTask, FileTransformationStartupService>();
    }
}
