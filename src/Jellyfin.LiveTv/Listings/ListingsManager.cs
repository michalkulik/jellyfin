using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.Guide;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Listings;

/// <inheritdoc />
public class ListingsManager : IListingsManager
{
    private const int TunerChannelsCacheMinutes = 5;

    private readonly ILogger<ListingsManager> _logger;
    private readonly IConfigurationManager _config;
    private readonly ITaskManager _taskManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly IListingsProvider[] _listingsProviders;

    private readonly ConcurrentDictionary<string, EpgChannelData> _epgChannels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Caches the raw provider channel list per listings provider id so that UI screens such as
    /// the channel mapping dialog don't re-download/re-parse the (potentially huge) XMLTV file on
    /// every open. Invalidated via <see cref="InvalidateListingsProviderCache"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<ChannelInfo>> _providerChannelsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Short-lived cache of tuner channels per listings provider id. Opening the mapping dialog or
    /// saving a single manual mapping must not re-download/re-parse the tuner playlist (which is
    /// often a remote M3U URL) on every request. A short TTL keeps tuner edits reasonably fresh.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, List<ChannelInfo> Channels)> _tunerChannelsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ListingsManager"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{TCategoryName}"/>.</param>
    /// <param name="config">The <see cref="IConfigurationManager"/>.</param>
    /// <param name="taskManager">The <see cref="ITaskManager"/>.</param>
    /// <param name="tunerHostManager">The <see cref="ITunerHostManager"/>.</param>
    /// <param name="listingsProviders">The <see cref="IListingsProvider"/>.</param>
    public ListingsManager(
        ILogger<ListingsManager> logger,
        IConfigurationManager config,
        ITaskManager taskManager,
        ITunerHostManager tunerHostManager,
        IEnumerable<IListingsProvider> listingsProviders)
    {
        _logger = logger;
        _config = config;
        _taskManager = taskManager;
        _tunerHostManager = tunerHostManager;
        _listingsProviders = listingsProviders.ToArray();
    }

    /// <inheritdoc />
    public async Task<ListingsProviderInfo> SaveListingProvider(ListingsProviderInfo info, bool validateLogin, bool validateListings)
    {
        ArgumentNullException.ThrowIfNull(info);

        var provider = GetProvider(info.Type);
        await provider.Validate(info, validateLogin, validateListings).ConfigureAwait(false);

        var config = _config.GetLiveTvConfiguration();

        var list = config.ListingProviders;
        int index = Array.FindIndex(list, i => string.Equals(i.Id, info.Id, StringComparison.OrdinalIgnoreCase));

        if (index == -1 || string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            config.ListingProviders = [.. list, info];
        }
        else
        {
            config.ListingProviders[index] = info;
        }

        _config.SaveConfiguration("livetv", config);

        InvalidateListingsProviderCache(info.Id);

        _taskManager.CancelIfRunningAndQueue<RefreshGuideScheduledTask>();

        return info;
    }

    /// <inheritdoc />
    public void DeleteListingsProvider(string? id)
    {
        var config = _config.GetLiveTvConfiguration();

        config.ListingProviders = config.ListingProviders.Where(i => !string.Equals(id, i.Id, StringComparison.OrdinalIgnoreCase)).ToArray();

        _config.SaveConfiguration("livetv", config);

        if (!string.IsNullOrEmpty(id))
        {
            InvalidateListingsProviderCache(id);
        }

        _taskManager.CancelIfRunningAndQueue<RefreshGuideScheduledTask>();
    }

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(string? providerType, string? providerId, string? country, string? location)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return GetProvider(providerType).GetLineups(null, country, location);
        }

        var info = _config.GetLiveTvConfiguration().ListingProviders
            .FirstOrDefault(i => string.Equals(i.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ResourceNotFoundException();

        return GetProvider(info.Type).GetLineups(info, country, location);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ChannelInfo channel,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        foreach (var (provider, providerInfo) in GetListingProviders())
        {
            if (!IsListingProviderEnabledForTuner(providerInfo, channel.TunerHostId))
            {
                _logger.LogDebug(
                    "Skipping getting programs for channel {0}-{1} from {2}-{3}, because it's not enabled for this tuner.",
                    channel.Number,
                    channel.Name,
                    provider.Name,
                    providerInfo.ListingsId ?? string.Empty);
                continue;
            }

            _logger.LogDebug(
                "Getting programs for channel {0}-{1} from {2}-{3}",
                channel.Number,
                channel.Name,
                provider.Name,
                providerInfo.ListingsId ?? string.Empty);

            var epgChannels = await GetEpgChannels(provider, providerInfo, true, cancellationToken).ConfigureAwait(false);

            var epgChannel = GetEpgChannelFromTunerChannel(providerInfo.ChannelMappings, channel, epgChannels);
            if (epgChannel is null)
            {
                _logger.LogDebug("EPG channel not found for tuner channel {0}-{1} from {2}-{3}", channel.Number, channel.Name, provider.Name, providerInfo.ListingsId ?? string.Empty);
                continue;
            }

            var programs = (await provider
                .GetProgramsAsync(providerInfo, epgChannel.Id, startDateUtc, endDateUtc, cancellationToken).ConfigureAwait(false))
                .ToList();

            // Replace the value that came from the provider with a normalized value
            foreach (var program in programs)
            {
                program.ChannelId = channel.Id;
                program.Id += "_" + channel.Id;
            }

            if (programs.Count > 0)
            {
                return programs;
            }
        }

        return Enumerable.Empty<ProgramInfo>();
    }

    /// <inheritdoc />
    public async Task AddProviderMetadata(IList<ChannelInfo> channels, bool enableCache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);

        foreach (var (provider, providerInfo) in GetListingProviders())
        {
            var enabledChannels = channels
                .Where(i => IsListingProviderEnabledForTuner(providerInfo, i.TunerHostId))
                .ToList();

            if (enabledChannels.Count == 0)
            {
                continue;
            }

            try
            {
                await AddMetadata(provider, providerInfo, enabledChannels, enableCache, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding metadata");
            }
        }
    }

    /// <inheritdoc />
    public async Task<ChannelMappingOptionsDto> GetChannelMappingOptions(string? providerId)
    {
        var listingsProviderInfo = _config.GetLiveTvConfiguration().ListingProviders
            .First(info => string.Equals(providerId, info.Id, StringComparison.OrdinalIgnoreCase));

        var provider = GetProvider(listingsProviderInfo.Type);

        var tunerChannels = await GetChannelsForListingsProvider(listingsProviderInfo, CancellationToken.None)
            .ConfigureAwait(false);

        var providerChannels = await GetProviderChannels(provider, listingsProviderInfo, default)
            .ConfigureAwait(false);

        var mappings = listingsProviderInfo.ChannelMappings;

        // Build the EPG lookup once and reuse it for every tuner channel; rebuilding it per channel
        // is O(channels x display-names) and makes the dialog very slow on large XMLTV sources.
        var epgChannelData = new EpgChannelData(providerChannels);

        return new ChannelMappingOptionsDto
        {
            TunerChannels = tunerChannels.Select(i => GetTunerChannelMapping(i, mappings, epgChannelData)).ToList(),
            ProviderChannels = providerChannels.Select(i => new NameIdPair
            {
                Name = i.Name,
                Id = i.Id
            }).ToList(),
            Mappings = mappings,
            ProviderName = provider.Name
        };
    }

    /// <inheritdoc />
    public async Task<TunerChannelMapping> SetChannelMapping(string providerId, string tunerChannelNumber, string providerChannelNumber)
    {
        var config = _config.GetLiveTvConfiguration();

        var listingsProviderInfo = config.ListingProviders
            .First(info => string.Equals(providerId, info.Id, StringComparison.OrdinalIgnoreCase));

        var tunerChannels = await GetChannelsForListingsProvider(listingsProviderInfo, CancellationToken.None)
            .ConfigureAwait(false);

        var tunerChannel = tunerChannels.FirstOrDefault(i => string.Equals(i.Id, tunerChannelNumber, StringComparison.OrdinalIgnoreCase))
            ?? tunerChannels.FirstOrDefault(i => string.Equals(i.TunerChannelId, tunerChannelNumber, StringComparison.OrdinalIgnoreCase))
            ?? tunerChannels.FirstOrDefault(i => string.Equals(i.Number, tunerChannelNumber, StringComparison.OrdinalIgnoreCase));

        // M3U (and similar) channel ids are derived from the tuner/playlist URL and change whenever
        // that URL or its token changes, which silently orphaned previously saved mappings. Store the
        // mapping under the channel's stable provider id (tvg-id/Number) instead.
        var mappingKey = tunerChannel is not null && !string.IsNullOrWhiteSpace(tunerChannel.TunerChannelId)
            ? tunerChannel.TunerChannelId
            : tunerChannel is not null && !string.IsNullOrWhiteSpace(tunerChannel.Number)
                ? tunerChannel.Number
                : tunerChannelNumber;

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mappingKey, tunerChannelNumber };
        if (tunerChannel is not null)
        {
            if (!string.IsNullOrWhiteSpace(tunerChannel.Id))
            {
                identifiers.Add(tunerChannel.Id);
            }

            if (!string.IsNullOrWhiteSpace(tunerChannel.TunerChannelId))
            {
                identifiers.Add(tunerChannel.TunerChannelId);
            }

            if (!string.IsNullOrWhiteSpace(tunerChannel.Number))
            {
                identifiers.Add(tunerChannel.Number);
            }
        }

        // Drop any previous mapping for this tuner channel, including legacy id-keyed entries.
        listingsProviderInfo.ChannelMappings = listingsProviderInfo.ChannelMappings
            .Where(pair => !identifiers.Contains(pair.Name)).ToArray();

        if (!string.IsNullOrWhiteSpace(providerChannelNumber))
        {
            listingsProviderInfo.ChannelMappings =
            [
                .. listingsProviderInfo.ChannelMappings,
                new NameValuePair
                {
                    Name = mappingKey,
                    Value = providerChannelNumber
                }
            ];
        }

        _config.SaveConfiguration("livetv", config);

        if (tunerChannel is null)
        {
            throw new ResourceNotFoundException($"Couldn't find tuner channel {tunerChannelNumber}");
        }

        var providerChannels = await GetProviderChannels(GetProvider(listingsProviderInfo.Type), listingsProviderInfo, default)
            .ConfigureAwait(false);

        // Only the requested channel is needed; avoid rebuilding mappings (and the EPG lookup) for
        // every tuner channel on each manual change.
        return GetTunerChannelMapping(tunerChannel, listingsProviderInfo.ChannelMappings, new EpgChannelData(providerChannels));
    }

    private List<(IListingsProvider Provider, ListingsProviderInfo ProviderInfo)> GetListingProviders()
        => _config.GetLiveTvConfiguration().ListingProviders
            .Select(info => (
                Provider: _listingsProviders.FirstOrDefault(l
                    => string.Equals(l.Type, info.Type, StringComparison.OrdinalIgnoreCase)),
                ProviderInfo: info))
            .Where(i => i.Provider is not null)
            .ToList()!; // Already filtered out null

    private async Task AddMetadata(
        IListingsProvider provider,
        ListingsProviderInfo info,
        IEnumerable<ChannelInfo> tunerChannels,
        bool enableCache,
        CancellationToken cancellationToken)
    {
        var epgChannels = await GetEpgChannels(provider, info, enableCache, cancellationToken).ConfigureAwait(false);

        foreach (var tunerChannel in tunerChannels)
        {
            var epgChannel = GetEpgChannelFromTunerChannel(info.ChannelMappings, tunerChannel, epgChannels);
            if (epgChannel is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(epgChannel.ImageUrl))
            {
                tunerChannel.ImageUrl = epgChannel.ImageUrl;
            }
        }
    }

    private static bool IsListingProviderEnabledForTuner(ListingsProviderInfo info, string tunerHostId)
    {
        if (info.EnableAllTuners)
        {
            return true;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tunerHostId);

        return info.EnabledTuners.Contains(tunerHostId, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetMappedChannel(string channelId, NameValuePair[] mappings)
    {
        foreach (NameValuePair mapping in mappings)
        {
            if (string.Equals(mapping.Name, channelId, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.Value;
            }
        }

        return channelId;
    }

    private void InvalidateListingsProviderCache(string providerId)
    {
        // Clear in-memory EPG channel cache for this provider
        _epgChannels.TryRemove(providerId, out _);
        _providerChannelsCache.TryRemove(providerId, out _);
        _tunerChannelsCache.TryRemove(providerId, out _);

        // Provider IDs are generated as Guid.NewGuid().ToString("N")
        // reject anything else so we never use untrusted input in a path or log entry.
        if (!Guid.TryParseExact(providerId, "N", out var providerGuid))
        {
            return;
        }

        // Delete the cached XMLTV file so a fresh copy is downloaded
        var cachePath = _config.CommonApplicationPaths?.CachePath;
        if (!string.IsNullOrEmpty(cachePath))
        {
            var safeId = providerGuid.ToString("N", CultureInfo.InvariantCulture);
            var xmltvCacheFile = Path.Combine(cachePath, "xmltv", safeId + ".xml");
            try
            {
                if (File.Exists(xmltvCacheFile))
                {
                    File.Delete(xmltvCacheFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Error deleting XMLTV cache file for provider {ProviderId}", safeId);
            }
        }
    }

    private async Task<EpgChannelData> GetEpgChannels(
        IListingsProvider provider,
        ListingsProviderInfo info,
        bool enableCache,
        CancellationToken cancellationToken)
    {
        if (enableCache && _epgChannels.TryGetValue(info.Id, out var result))
        {
            return result;
        }

        var channels = await provider.GetChannels(info, cancellationToken).ConfigureAwait(false);
        foreach (var channel in channels)
        {
            _logger.LogInformation("Found epg channel in {0} {1} {2} {3}", provider.Name, info.ListingsId, channel.Name, channel.Id);
        }

        result = new EpgChannelData(channels);
        _epgChannels.AddOrUpdate(info.Id, result, (_, _) => result);

        // Cache the raw provider channels too so that UI screens (e.g. the channel mapping dialog)
        // don't need to re-read/re-parse the XMLTV file.
        _providerChannelsCache[info.Id] = channels.ToList();

        return result;
    }

    /// <summary>
    /// Returns the provider channel list for a listing provider, serving from the in-memory cache
    /// populated during guide refreshes when possible to avoid re-parsing slow/large XMLTV sources.
    /// </summary>
    private async Task<IReadOnlyList<ChannelInfo>> GetProviderChannels(
        IListingsProvider provider,
        ListingsProviderInfo info,
        CancellationToken cancellationToken)
    {
        if (_providerChannelsCache.TryGetValue(info.Id, out var cached))
        {
            return cached;
        }

        var channels = await provider.GetChannels(info, cancellationToken).ConfigureAwait(false);
        var list = channels.ToList();
        foreach (var channel in list)
        {
            _logger.LogInformation("Found epg channel in {0} {1} {2} {3}", provider.Name, info.ListingsId, channel.Name, channel.Id);
        }

        _providerChannelsCache[info.Id] = list;
        return list;
    }

    private static ChannelInfo? GetEpgChannelFromTunerChannel(
        NameValuePair[] mappings,
        ChannelInfo tunerChannel,
        EpgChannelData epgChannelData)
    {
        if (!string.IsNullOrWhiteSpace(tunerChannel.Id))
        {
            var mappedTunerChannelId = GetMappedChannel(tunerChannel.Id, mappings);
            if (string.IsNullOrWhiteSpace(mappedTunerChannelId))
            {
                mappedTunerChannelId = tunerChannel.Id;
            }

            var channel = epgChannelData.GetChannelById(mappedTunerChannelId);
            if (channel is not null)
            {
                return channel;
            }
        }

        if (!string.IsNullOrWhiteSpace(tunerChannel.TunerChannelId))
        {
            var tunerChannelId = tunerChannel.TunerChannelId;
            if (tunerChannelId.Contains(".json.schedulesdirect.org", StringComparison.OrdinalIgnoreCase))
            {
                tunerChannelId = tunerChannelId.Replace(".json.schedulesdirect.org", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('I');
            }

            var mappedTunerChannelId = GetMappedChannel(tunerChannelId, mappings);
            if (string.IsNullOrWhiteSpace(mappedTunerChannelId))
            {
                mappedTunerChannelId = tunerChannelId;
            }

            var channel = epgChannelData.GetChannelById(mappedTunerChannelId);
            if (channel is not null)
            {
                return channel;
            }
        }

        if (!string.IsNullOrWhiteSpace(tunerChannel.Number))
        {
            var tunerChannelNumber = GetMappedChannel(tunerChannel.Number, mappings);
            if (string.IsNullOrWhiteSpace(tunerChannelNumber))
            {
                tunerChannelNumber = tunerChannel.Number;
            }

            var channel = epgChannelData.GetChannelByNumber(tunerChannelNumber);
            if (channel is not null)
            {
                return channel;
            }
        }

        if (!string.IsNullOrWhiteSpace(tunerChannel.Name))
        {
            // Alias-aware matcher. It checks every display-name alias of an EPG channel (primary
            // name plus alternates) with a normalisation tolerant of feed-quality/country markers
            // (PL:, HD, FHD, ...). Ambiguous matches are intentionally rejected so a tune channel
            // is never silently paired with the wrong guide entry.
            var channel = epgChannelData.MatchByQueryName(tunerChannel.Name);
            if (channel is not null)
            {
                return channel;
            }
        }

        return null;
    }

    private static TunerChannelMapping GetTunerChannelMapping(ChannelInfo tunerChannel, NameValuePair[] mappings, EpgChannelData epgChannelData)
    {
        var result = new TunerChannelMapping
        {
            Name = tunerChannel.Name,
            Id = tunerChannel.Id
        };

        if (!string.IsNullOrWhiteSpace(tunerChannel.Number))
        {
            result.Name = tunerChannel.Number + " " + result.Name;
        }

        var providerChannel = GetEpgChannelFromTunerChannel(mappings, tunerChannel, epgChannelData);
        if (providerChannel is not null)
        {
            result.ProviderChannelName = providerChannel.Name;
            result.ProviderChannelId = providerChannel.Id;
        }

        return result;
    }

    private async Task<List<ChannelInfo>> GetChannelsForListingsProvider(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        if (_tunerChannelsCache.TryGetValue(info.Id, out var cached)
            && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(TunerChannelsCacheMinutes))
        {
            return cached.Channels;
        }

        var channels = new List<ChannelInfo>();
        foreach (var hostInstance in _tunerHostManager.TunerHosts)
        {
            try
            {
                var tunerChannels = await hostInstance.GetChannels(false, cancellationToken).ConfigureAwait(false);

                channels.AddRange(tunerChannels.Where(channel => IsListingProviderEnabledForTuner(info, channel.TunerHostId)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting channels");
            }
        }

        _tunerChannelsCache[info.Id] = (DateTime.UtcNow, channels);

        return channels;
    }

    private IListingsProvider GetProvider(string? providerType)
        => _listingsProviders.FirstOrDefault(i => string.Equals(providerType, i.Type, StringComparison.OrdinalIgnoreCase))
           ?? throw new ResourceNotFoundException($"Couldn't find provider of type {providerType}");
}
