using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalTrailers.Services;

public sealed class YtDlpManager
{
    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YtDlpManager> _logger;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public YtDlpManager(IApplicationPaths appPaths, IHttpClientFactory httpClientFactory, ILogger<YtDlpManager> logger)
    {
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string BinDir => Path.Combine(_appPaths.DataPath, "local-trailers");

    public string ManagedPath => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    public string? Resolve()
    {
        var configured = Plugin.Instance?.Configuration.YtDlpPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return File.Exists(ManagedPath) ? ManagedPath : null;
    }

    public bool HasUsable => Resolve() is not null;

    public bool UsingConfigured
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.YtDlpPath;
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured);
        }
    }

    private static string AssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "yt-dlp_linux_aarch64",
            Architecture.Arm => "yt-dlp_linux_armv7l",
            _ => "yt-dlp_linux",
        };
    }

    public async Task<(bool Ok, string Message)> DownloadAsync(CancellationToken ct)
    {
        if (UsingConfigured)
        {
            return (true, "Using the configured yt-dlp path; managed download skipped.");
        }

        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(BinDir);
            var asset = AssetName();
            var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}";
            var tmp = ManagedPath + ".tmp";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(tmp, ManagedPath, overwrite: true);

            var version = await RunVersionAsync(ManagedPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(version))
            {
                try { File.Delete(ManagedPath); } catch { }

                _logger.LogError(
                    "[LocalTrailers] Managed yt-dlp ({Asset}) downloaded but won't run on this system. "
                    + "On Alpine/musl Linux, install yt-dlp via your package manager and set its path in the plugin config.",
                    asset);
                return (false, $"{asset} downloaded but won't run here. Install yt-dlp via your package manager and set its path.");
            }

            _logger.LogInformation("[LocalTrailers] Installed managed yt-dlp ({Asset}) {Version}", asset, version);
            return (true, $"Installed {asset} ({version}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LocalTrailers] yt-dlp download failed");
            try
            {
                var tmp = ManagedPath + ".tmp";
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch { }

            return (false, ex.Message);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        var path = Resolve();
        if (path is null)
        {
            return "not found";
        }

        var version = await RunVersionAsync(path, ct).ConfigureAwait(false);
        return version ?? "error";
    }

    private static async Task<string?> RunVersionAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await stdoutTask.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task EnsureAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.ManageYtDlp || HasUsable)
        {
            return;
        }

        _logger.LogInformation("[LocalTrailers] No yt-dlp found — downloading managed binary...");
        await DownloadAsync(ct).ConfigureAwait(false);
    }
}
