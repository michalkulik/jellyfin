using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Guide;

/// <summary>
/// Shrinks the locally cached Live TV images so they do not bloat the metadata folder.
/// </summary>
public class ShrinkLiveTvImagesScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILiveTvManager _liveTvManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IConfigurationManager _config;
    private readonly ILogger<ShrinkLiveTvImagesScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShrinkLiveTvImagesScheduledTask"/> class.
    /// </summary>
    /// <param name="liveTvManager">The live tv manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="config">The configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public ShrinkLiveTvImagesScheduledTask(
        ILiveTvManager liveTvManager,
        ILibraryManager libraryManager,
        IImageProcessor imageProcessor,
        IConfigurationManager config,
        ILogger<ShrinkLiveTvImagesScheduledTask> logger)
    {
        _liveTvManager = liveTvManager;
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Shrink Live TV images";

    /// <inheritdoc />
    public string Description => "Reduces the size of the cached Live TV artwork to save disk space.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public bool IsHidden => _liveTvManager.Services.Count == 1 && _config.GetLiveTvConfiguration().TunerHosts.Length == 0;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public string Key => "ShrinkLiveTvImages";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram, BaseItemKind.LiveTvChannel],
            DtoOptions = new DtoOptions(false)
        });

        var numComplete = 0;
        var numReduced = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var image in item.ImageInfos)
            {
                if (!image.IsLocalFile || string.IsNullOrEmpty(image.Path))
                {
                    continue;
                }

                try
                {
                    if (await ImageSizeReducer.ReduceAsync(
                            _imageProcessor,
                            item,
                            image.Path,
                            ImageSizeReducer.DefaultMaxSizeBytes,
                            cancellationToken).ConfigureAwait(false))
                    {
                        numReduced++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to shrink Live TV image {Path}", image.Path);
                }
            }

            numComplete++;
            progress.Report(numComplete / (double)items.Count);
        }

        progress.Report(100);
        _logger.LogInformation("Shrunk {Count} Live TV images", numReduced);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}
