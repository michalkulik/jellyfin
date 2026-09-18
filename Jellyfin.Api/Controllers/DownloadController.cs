using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Downloads;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Downloads;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Creates and serves server side "convert then download" jobs.
/// </summary>
[Route("")]
public class DownloadController : BaseJellyfinApiController
{
    private const string DefaultVideoCodec = "h264";
    private const string DefaultAudioCodec = "aac";
    private const string DefaultContainer = "mp4";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly MediaBrowser.Controller.Library.IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly EncodingHelper _encodingHelper;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ITranscodeDownloadManager _downloadManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="encodingHelper">The encoding helper.</param>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="downloadManager">The download manager.</param>
    public DownloadController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IServerConfigurationManager serverConfigurationManager,
        MediaBrowser.Controller.Library.IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        EncodingHelper encodingHelper,
        ITranscodeManager transcodeManager,
        ITranscodeDownloadManager downloadManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _encodingHelper = encodingHelper;
        _transcodeManager = transcodeManager;
        _downloadManager = downloadManager;
    }

    /// <summary>
    /// Creates a download job. The server converts the item in the background when a bitrate is requested.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="request">The download options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="201">The download job was created.</response>
    /// <returns>The created download job.</returns>
    [HttpPost("Items/{itemId}/Download")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<DownloadJobInfo>> CreateDownload(
        [FromRoute] Guid itemId,
        [FromBody] CreateDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId.IsEmpty())
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!item.CanDownload(user))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var jobId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        // No bitrate requested: serve the original file without conversion.
        if (request.Original || request.MaxBitrate is null or <= 0)
        {
            var originalPath = GetOriginalFilePath(item);
            if (originalPath is null)
            {
                return NotFound();
            }

            var originalInfo = _downloadManager.EnqueueOriginal(jobId, itemId, originalPath, Path.GetFileName(originalPath));
            return StatusCode(StatusCodes.Status201Created, originalInfo);
        }

        var container = string.IsNullOrWhiteSpace(request.Container) ? DefaultContainer : request.Container.TrimStart('.');

        var videoRequest = new VideoRequestDto
        {
            Id = itemId,
            MediaSourceId = request.MediaSourceId!,
            DeviceId = request.DeviceId,
            PlaySessionId = jobId,
            Container = container,
            VideoCodec = DefaultVideoCodec,
            AudioCodec = DefaultAudioCodec,
            VideoBitRate = request.MaxBitrate,
            MaxHeight = request.MaxHeight,
            AudioStreamIndex = request.AudioStreamIndex,
            VideoStreamIndex = request.VideoStreamIndex,
            StartTimeTicks = 0,
            Static = false,
            Context = EncodingContext.Static,
            EnableAutoStreamCopy = false,
            AllowVideoStreamCopy = false,
            AllowAudioStreamCopy = false
        };

        var state = await StreamingHelpers.GetStreamingState(
            videoRequest,
            HttpContext,
            _mediaSourceManager,
            _userManager,
            _libraryManager,
            _serverConfigurationManager,
            _mediaEncoder,
            _encodingHelper,
            _transcodeManager,
            TranscodingJobType.Progressive,
            cancellationToken).ConfigureAwait(false);

        var encodingOptions = _serverConfigurationManager.GetEncodingOptions();
        var commandLine = _encodingHelper.GetProgressiveVideoFullCommandLine(state, encodingOptions, EncoderPreset.veryfast);

        var fileName = BuildFileName(item, container);
        var jobInfo = _downloadManager.Enqueue(jobId, state, commandLine, itemId, userId, fileName);

        return StatusCode(StatusCodes.Status201Created, jobInfo);
    }

    /// <summary>
    /// Gets the status of a download job.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="jobId">The job id.</param>
    /// <response code="200">The download job status was returned.</response>
    /// <returns>The download job status.</returns>
    [HttpGet("Items/{itemId}/Download/{jobId}")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DownloadJobInfo> GetDownloadStatus([FromRoute] Guid itemId, [FromRoute] string jobId)
    {
        var job = _downloadManager.GetJob(jobId);
        if (job is null || !job.ItemId.Equals(itemId))
        {
            return NotFound();
        }

        return job;
    }

    /// <summary>
    /// Downloads the converted file of a finished job.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="jobId">The job id.</param>
    /// <response code="200">The file is ready and was returned.</response>
    /// <response code="404">The job does not exist or is not ready yet.</response>
    /// <returns>The converted file.</returns>
    [HttpGet("Items/{itemId}/Download/{jobId}/File")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetDownloadFile([FromRoute] Guid itemId, [FromRoute] string jobId)
    {
        var job = _downloadManager.GetJob(jobId);
        if (job is null || !job.ItemId.Equals(itemId))
        {
            return NotFound();
        }

        if (!_downloadManager.TryGetReadyFile(jobId, out var path, out var fileName) || path is null)
        {
            return NotFound();
        }

        return PhysicalFile(path, MimeTypes.GetMimeType(path), fileName ?? Path.GetFileName(path), enableRangeProcessing: true);
    }

    /// <summary>
    /// Cancels a download job and deletes its partial output.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="jobId">The job id.</param>
    /// <response code="204">The job was cancelled.</response>
    /// <returns>A task representing the cancellation.</returns>
    [HttpDelete("Items/{itemId}/Download/{jobId}")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelDownload([FromRoute] Guid itemId, [FromRoute] string jobId)
    {
        var job = _downloadManager.GetJob(jobId);
        if (job is null || !job.ItemId.Equals(itemId))
        {
            return NotFound();
        }

        _downloadManager.Cancel(jobId);
        return NoContent();
    }

    private static string? GetOriginalFilePath(BaseItem item)
    {
        var path = item.Path;
        return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? path : null;
    }

    private static string BuildFileName(BaseItem item, string container)
    {
        var name = item.Name ?? item.Id.ToString("N", CultureInfo.InvariantCulture);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + "." + container;
    }
}
