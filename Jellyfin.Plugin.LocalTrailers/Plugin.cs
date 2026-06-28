using Jellyfin.Plugin.LocalTrailers.Services;
using Jellyfin.Plugin.LocalTrailers.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LocalTrailers;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool ManageYtDlp { get; set; } = true;

    public string YtDlpPath { get; set; } = string.Empty;

    public string FfmpegPath { get; set; } = string.Empty;

    public string FormatSelector { get; set; } =
        "bestvideo[height<=1080][vcodec^=avc1]+bestaudio[acodec^=mp4a]/best[ext=mp4]/best";

    public string Proxy { get; set; } = string.Empty;

    public string YtDlpArguments { get; set; } = string.Empty;

    public int ResolveTimeoutSeconds { get; set; } = 120;

    public int MaxConcurrentDownloads { get; set; } = 2;

    public bool OverwriteExisting { get; set; }
}

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Local Trailers";

    public override Guid Id => new("fc250b1c-5cb4-4eae-a41b-8fb63b259959");

    public override string Description =>
        "Downloads YouTube trailers to disk as local trailer files next to your media, so every Jellyfin client can play them natively without streaming workarounds.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        }
    ];
}

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<YtDlpManager>();
        services.AddSingleton<TrailerDownloader>();
        services.AddSingleton<DownloadTrailersTask>();
        services.AddHostedService<YtDlpBootstrapService>();
    }
}
