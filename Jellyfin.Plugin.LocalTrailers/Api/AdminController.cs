using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalTrailers.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LocalTrailers.Api;

[ApiController]
[Route("LocalTrailers/admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AdminController : ControllerBase
{
    private readonly YtDlpManager _ytDlp;

    public AdminController(YtDlpManager ytDlp)
    {
        _ytDlp = ytDlp;
    }

    [HttpPost("ytdlp/update")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> UpdateYtDlp(CancellationToken ct)
    {
        var (ok, message) = await _ytDlp.DownloadAsync(ct).ConfigureAwait(false);
        return ok ? Ok(message) : StatusCode(500, message);
    }

    [HttpGet("ytdlp/version")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> YtDlpVersion(CancellationToken ct)
    {
        var version = await _ytDlp.VersionAsync(ct).ConfigureAwait(false);
        return Ok(version);
    }
}
