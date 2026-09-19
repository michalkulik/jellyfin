using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Scans the folders of a single library or series for new and removed files.
/// </summary>
/// <remarks>
/// This task is intentionally internal: it needs the item to scan, so it cannot be created by
/// the dependency injection container like the other scheduled tasks. It is queued directly by
/// the library manager instead.
/// </remarks>
internal sealed class RefreshItemLibraryTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly BaseItem _item;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshItemLibraryTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="item">The item whose folders should be scanned.</param>
    public RefreshItemLibraryTask(ILibraryManager libraryManager, ILocalizationManager localization, BaseItem item)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _item = item;
    }

    /// <inheritdoc />
    public string Name => string.Format(
        CultureInfo.InvariantCulture,
        _localization.GetLocalizedString("TaskRefreshItemLibrary"),
        _item.Name ?? _item.Path ?? string.Empty);

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskRefreshItemLibraryDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public string Key => "RefreshItemLibrary";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(0);

        await _libraryManager.ValidateItemLibrary(_item, progress, cancellationToken).ConfigureAwait(false);
    }
}
