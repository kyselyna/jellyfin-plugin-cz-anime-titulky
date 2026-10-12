using Jellyfin.Plugin.CzAnimeTitulky.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CzAnimeTitulky;

/// <summary>
/// CZ Anime Titulky – Czech/Slovak fansub subtitle provider for anime.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "CZ Anime Titulky";

    public override Guid Id => Guid.Parse("0b105cb3-606c-41f3-9a2b-a5c759d4d959");

    public override string Description =>
        "Stahuje české (případně slovenské) fansub titulky k anime z hiyori.cz, hns.sk, kamui-subs.cz a wosir.cz a automaticky srovná jejich časování s videem.";

    /// <summary>Folder for the plugin's caches (hiyori/AniList mappings).</summary>
    public string CacheFolder => Path.Combine(DataFolderPath, "cache");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "CzAnimeTitulky",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }
}
