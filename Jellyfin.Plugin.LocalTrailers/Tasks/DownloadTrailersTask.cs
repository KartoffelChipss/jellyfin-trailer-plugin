using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalTrailers.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalTrailers.Tasks;

public sealed class DownloadTrailersTask : IScheduledTask
{
    private readonly TrailerDownloader _downloader;
    private readonly ILogger<DownloadTrailersTask> _logger;

    public DownloadTrailersTask(TrailerDownloader downloader, ILogger<DownloadTrailersTask> logger)
    {
        _downloader = downloader;
        _logger = logger;
    }

    public string Name => "Download local trailers";

    public string Key => "LocalTrailersDownload";

    public string Description =>
        "Scans the library for movies and series with YouTube trailer URLs and downloads them as local trailer files next to each media item.";

    public string Category => "Local Trailers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[LocalTrailers] Starting trailer download task");
        await _downloader.DownloadAllAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
