using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalTrailers.Services;

public sealed class TrailerDownloader
{
    private static readonly Regex VideoIdPattern = new(
        @"(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/embed/)([A-Za-z0-9_-]{11})",
        RegexOptions.Compiled);

    private readonly ILogger<TrailerDownloader> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly YtDlpManager _ytDlp;

    public TrailerDownloader(
        ILogger<TrailerDownloader> logger,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        YtDlpManager ytDlp)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _ytDlp = ytDlp;
    }

    public async Task DownloadAllAsync(IProgress<double> progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return;
        }

        var ytDlpPath = _ytDlp.Resolve();
        if (ytDlpPath is null)
        {
            _logger.LogError("[LocalTrailers] No usable yt-dlp binary found");
            return;
        }

        var ffmpegPath = ResolveFfmpegPath(cfg);

        var items = GetItemsWithTrailers();
        if (items.Count == 0)
        {
            _logger.LogInformation("[LocalTrailers] No items with YouTube trailer URLs found");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("[LocalTrailers] Found {Count} item(s) with YouTube trailer URLs to process", items.Count);

        var semaphore = new SemaphoreSlim(Math.Clamp(cfg.MaxConcurrentDownloads, 1, 8));
        var completed = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;

        var tasks = items.Select(item => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await ProcessItemAsync(item.Item, item.VideoId, ytDlpPath, ffmpegPath, cfg, ct).ConfigureAwait(false);
                switch (result)
                {
                    case DownloadResult.Downloaded: Interlocked.Increment(ref downloaded); break;
                    case DownloadResult.Skipped:    Interlocked.Increment(ref skipped); break;
                    case DownloadResult.Failed:     Interlocked.Increment(ref failed); break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LocalTrailers] Failed processing {Name}", item.Item.Name);
                Interlocked.Increment(ref failed);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                progress.Report((double)done / items.Count * 100);
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation(
            "[LocalTrailers] Finished: {Downloaded} downloaded, {Skipped} already existed, {Failed} failed",
            downloaded, skipped, failed);
    }

    private List<(BaseItem Item, string VideoId)> GetItemsWithTrailers()
    {
        var results = new List<(BaseItem, string)>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
        };

        var items = _libraryManager.GetItemList(query);

        foreach (var item in items)
        {
            if (item.RemoteTrailers is null || item.RemoteTrailers.Count == 0)
            {
                continue;
            }

            if (string.IsNullOrEmpty(item.Path) || !Directory.Exists(Path.GetDirectoryName(item.Path)))
            {
                continue;
            }

            foreach (var trailer in item.RemoteTrailers)
            {
                var match = VideoIdPattern.Match(trailer.Url ?? string.Empty);
                if (match.Success)
                {
                    results.Add((item, match.Groups[1].Value));
                    break;
                }
            }
        }

        return results;
    }

    private enum DownloadResult { Downloaded, Skipped, Failed }

    private async Task<DownloadResult> ProcessItemAsync(
        BaseItem item,
        string videoId,
        string ytDlpPath,
        string? ffmpegPath,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        var mediaDir = Path.GetDirectoryName(item.Path)!;

        string trailerDir = item is Series
            ? Path.Combine(item.Path, "trailers")
            : Path.Combine(mediaDir, "trailers");

        var outputPath = Path.Combine(trailerDir, $"{videoId}-trailer.mp4");

        if (!cfg.OverwriteExisting && File.Exists(outputPath))
        {
            return DownloadResult.Skipped;
        }

        try
        {
            Directory.CreateDirectory(trailerDir);

            var success = await DownloadTrailerAsync(
                videoId, outputPath, ytDlpPath, ffmpegPath, cfg, ct).ConfigureAwait(false);

            if (success)
            {
                _logger.LogInformation("[LocalTrailers] Downloaded trailer {VideoId} for {Name}",
                    videoId, item.Name);
                return DownloadResult.Downloaded;
            }

            return DownloadResult.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LocalTrailers] Failed to download {VideoId} for {Name}",
                videoId, item.Name);
            return DownloadResult.Failed;
        }
    }

    private async Task<bool> DownloadTrailerAsync(
        string videoId,
        string outputPath,
        string ytDlpPath,
        string? ffmpegPath,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(cfg.FormatSelector);
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add("mp4");
            // yt-dlp manages its own temp files during download/merge and
            // renames to the final path atomically on success.
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputPath);

            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(ffmpegPath);
            }

            if (!string.IsNullOrWhiteSpace(cfg.Proxy))
            {
                psi.ArgumentList.Add("--proxy");
                psi.ArgumentList.Add(cfg.Proxy);
            }

            foreach (var arg in SplitArgs(cfg.YtDlpArguments))
            {
                psi.ArgumentList.Add(arg);
            }

            psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.ResolveTimeoutSeconds, 10, 600)));

            var (exit, _, stderr) = await RunProcessAsync(psi, timeoutCts.Token).ConfigureAwait(false);

            if (exit != 0)
            {
                _logger.LogWarning("[LocalTrailers] yt-dlp exit {Exit} for {VideoId}: {Err}",
                    exit, videoId, stderr.Trim());
                TryDelete(outputPath);
                return false;
            }

            if (!File.Exists(outputPath))
            {
                _logger.LogWarning("[LocalTrailers] yt-dlp produced no output for {VideoId}", videoId);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            _logger.LogError(ex, "[LocalTrailers] Download failed for {VideoId}", videoId);
            return false;
        }
    }

    private string? ResolveFfmpegPath(PluginConfiguration cfg)
    {
        string?[] candidates =
        {
            string.IsNullOrWhiteSpace(cfg.FfmpegPath) ? null : cfg.FfmpegPath,
            _mediaEncoder.EncoderPath,
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && c.Contains('/') && File.Exists(c))
            {
                return c;
            }
        }

        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
            {
                return c;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            yield break;
        }

        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }
}
