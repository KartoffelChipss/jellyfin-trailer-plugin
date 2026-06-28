using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalTrailers.Services;

public sealed class YtDlpBootstrapService : IHostedService
{
    private readonly YtDlpManager _ytDlp;
    private readonly ILogger<YtDlpBootstrapService> _logger;

    public YtDlpBootstrapService(YtDlpManager ytDlp, ILogger<YtDlpBootstrapService> logger)
    {
        _ytDlp = ytDlp;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _ytDlp.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LocalTrailers] yt-dlp bootstrap failed");
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
