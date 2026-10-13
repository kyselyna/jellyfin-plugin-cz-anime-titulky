using Jellyfin.Plugin.CzAnimeTitulky.Matching;
using Jellyfin.Plugin.CzAnimeTitulky.Providers;
using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using Jellyfin.Plugin.CzAnimeTitulky.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CzAnimeTitulky;

/// <summary>Registers the plugin's services (one session per site for the whole server).</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HiyoriClient>();
        serviceCollection.AddSingleton<HnsClient>();
        serviceCollection.AddSingleton<KamuiClient>();
        serviceCollection.AddSingleton<WosirClient>();
        serviceCollection.AddSingleton<DirectClient>();
        serviceCollection.AddSingleton<AngeClient>();
        serviceCollection.AddSingleton<HannyaClient>();
        serviceCollection.AddSingleton<TeamNsClient>();
        serviceCollection.AddSingleton<LocalClient>();
        serviceCollection.AddSingleton<AniListClient>();
        serviceCollection.AddSingleton<HiyoriResolver>();
        serviceCollection.AddSingleton<MediaTools>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<Translation.AiBackend>();
        serviceCollection.AddSingleton<Translation.SubtitleTranslator>();
        serviceCollection.AddSingleton<Translation.ClaudeSetup>();
        serviceCollection.AddSingleton<ISubtitleProvider, CzAnimeSubtitleProvider>();
    }
}
