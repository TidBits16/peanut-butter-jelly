using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PeanutButterJelly;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TaggerEngine>();
        serviceCollection.AddSingleton<DeezerClient>();
        serviceCollection.AddSingleton<LrcLibClient>();
        serviceCollection.AddSingleton<BioClient>();
        serviceCollection.AddSingleton<HttpCache>();
    }
}
