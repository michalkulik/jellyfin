#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Extensions;
using Jellyfin.XmlTv;
using Jellyfin.XmlTv.Entities;
using Jellyfin.XmlTv.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Listings
{
    public class XmlTvListingsProvider : IListingsProvider
    {
        private static readonly TimeSpan _maxCacheAge = TimeSpan.FromHours(1);
        private static readonly TimeSpan _downloadTimeout = TimeSpan.FromMinutes(15);

        private readonly IServerConfigurationManager _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<XmlTvListingsProvider> _logger;

        private readonly ConcurrentDictionary<string, DateTime> _lastDownloadFailures = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, Dictionary<string, (int? Season, int? Episode)> Data)> _onscreenEpisodeCache = new(StringComparer.Ordinal);

        public XmlTvListingsProvider(
            IServerConfigurationManager config,
            IHttpClientFactory httpClientFactory,
            ILogger<XmlTvListingsProvider> logger)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public string Name => "XmlTV";

        public string Type => "xmltv";

        private string GetLanguage(ListingsProviderInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.PreferredLanguage))
            {
                return info.PreferredLanguage;
            }

            return _config.Configuration.PreferredMetadataLanguage;
        }

        private async Task<string> GetXml(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("xmltv path: {Path}", info.Path);

            string cacheFilename = info.Id + ".xml";
            string cacheDir = Path.Join(_config.ApplicationPaths.CachePath, "xmltv");
            string cacheFile = Path.Join(cacheDir, cacheFilename);

            if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) >= DateTime.UtcNow.Subtract(_maxCacheAge))
            {
                return cacheFile;
            }

            var isRemote = info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase);

            if (isRemote
                && _lastDownloadFailures.TryGetValue(info.Path, out var lastFailure)
                && DateTime.UtcNow - lastFailure < _maxCacheAge)
            {
                if (File.Exists(cacheFile))
                {
                    return cacheFile;
                }

                throw new InvalidOperationException("Skipping the XMLTV download after a recent failure: " + info.Path);
            }

            Directory.CreateDirectory(cacheDir);

            var tempFile = cacheFile + ".tmp";

            try
            {
                using var timeout = new CancellationTokenSource(_downloadTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var downloadCancellationToken = linkedTokenSource.Token;

                if (isRemote)
                {
                    _logger.LogInformation("Downloading xmltv listings from {Path}", info.Path);

                    var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
                    httpClient.Timeout = _downloadTimeout;

                    using var response = await httpClient
                        .GetAsync(info.Path, HttpCompletionOption.ResponseHeadersRead, downloadCancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var redirectedUrl = response.RequestMessage?.RequestUri?.ToString() ?? info.Path;
                    var stream = await response.Content.ReadAsStreamAsync(downloadCancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        await UnzipIfNeededAndCopy(redirectedUrl, stream, tempFile, downloadCancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var stream = AsyncFile.OpenRead(info.Path);
                    await using (stream.ConfigureAwait(false))
                    {
                        await UnzipIfNeededAndCopy(info.Path, stream, tempFile, downloadCancellationToken).ConfigureAwait(false);
                    }
                }

                File.Move(tempFile, cacheFile, true);
                _lastDownloadFailures.TryRemove(info.Path, out _);

                return cacheFile;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteTempFile(tempFile);

                throw;
            }
            catch (Exception ex)
            {
                TryDeleteTempFile(tempFile);
                _lastDownloadFailures[info.Path] = DateTime.UtcNow;

                _logger.LogError(ex, "Error downloading or processing XMLTV file from {Path}", info.Path);

                if (File.Exists(cacheFile))
                {
                    _logger.LogWarning("Falling back to the previously downloaded XMLTV file for {Path}", info.Path);

                    return cacheFile;
                }

                if (ex is OperationCanceledException)
                {
                    throw new TimeoutException(
                        string.Format(CultureInfo.InvariantCulture, "Timed out downloading the XMLTV file from {0}", info.Path),
                        ex);
                }

                throw;
            }
        }

        private void TryDeleteTempFile(string tempFile)
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Error deleting temporary XMLTV file {File}", tempFile);
            }
        }

        private async Task UnzipIfNeededAndCopy(string originalUrl, Stream stream, string file, CancellationToken cancellationToken)
        {
            var fileStream = new FileStream(
                file,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                IODefaults.FileStreamBufferSize,
                FileOptions.Asynchronous);

            await using (fileStream.ConfigureAwait(false))
            {
                if (Path.GetExtension(originalUrl.AsSpan().LeftPart('?')).Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(originalUrl.AsSpan().LeftPart('?')).Equals(".gzip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new GZipStream(stream, CompressionMode.Decompress);
                        await reader.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error extracting from gz file {File}", originalUrl);
                    }
                }
                else
                {
                    await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
            }

            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                throw new InvalidOperationException("Downloaded XMLTV file is empty: " + originalUrl);
            }
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentNullException(nameof(channelId));
            }

            _logger.LogDebug("Getting xmltv programs for channel {Id}", channelId);

            string path = await GetXml(info, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));

            // The XMLTV reader only understands <episode-num> values expressed in the xmltv_ns or
            // SxxExx systems. Many providers (e.g. epg.ovh) instead use system="onscreen" (e.g. "S2E23"),
            // which the reader silently drops. Read those separately so programmes without a
            // <sub-title> can still be recognised as series episodes.
            var onscreenEpisodes = GetOnscreenEpisodeData(path);

            return reader.GetProgrammes(channelId, startDateUtc, endDateUtc, cancellationToken)
                        .Select(p => GetProgramInfoWithEtag(p, info, onscreenEpisodes));
        }

        private ProgramInfo GetProgramInfoWithEtag(
            XmlTvProgram program,
            ListingsProviderInfo info,
            IReadOnlyDictionary<string, (int? Season, int? Episode)> onscreenEpisodes)
        {
            var programInfo = GetProgramInfo(program, info, onscreenEpisodes);

            if (XmlTvProgramEtag.TryCreate(programInfo, out var etag, out var reason))
            {
                programInfo.Etag = etag;
            }
            else
            {
                _logger.LogDebug(
                    "Unable to create XMLTV program ETag for program {ProgramId} on channel {ChannelId} from {StartDate} to {EndDate}: {Reason}. The program will be treated as updated on each guide refresh.",
                    programInfo.Id,
                    programInfo.ChannelId,
                    programInfo.StartDate,
                    programInfo.EndDate,
                    reason);
            }

            return programInfo;
        }

        private static ProgramInfo GetProgramInfo(
            XmlTvProgram program,
            ListingsProviderInfo info,
            IReadOnlyDictionary<string, (int? Season, int? Episode)> onscreenEpisodes)
        {
            string? episodeTitle = program.Episode?.Title;
            var programCategories = program.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            var imageUrl = program.Icons.FirstOrDefault()?.Source;
            var episodeImageUrl = program.Images?.FirstOrDefault(m => m.Type == ImageType.Still)?.Path;
            var backgroundImageUrl = program.Images?.FirstOrDefault(m => m.Type == ImageType.Backdrop)?.Path;
            var rating = program.Ratings.FirstOrDefault()?.Value;
            var starRating = program.StarRatings?.FirstOrDefault()?.StarRating;

            // Some XMLTV sources (e.g. epg.ovh) only provide episode numbers through
            // <episode-num system="onscreen">S1E3</episode-num>, which the XMLTV reader ignores.
            // The reader also ignores programmes that carry no <sub-title> at all, even though the
            // on-screen episode number clearly marks them as series episodes. Fall back to the
            // separately parsed on-screen data in that case.
            var seasonNumber = program.Episode?.Series;
            var episodeNumber = program.Episode?.Episode;

            if (episodeNumber is null
                && onscreenEpisodes.TryGetValue(BuildProgramKey(program.ChannelId, program.StartDate), out var onscreenEpisode))
            {
                seasonNumber = onscreenEpisode.Season;
                episodeNumber = onscreenEpisode.Episode;
            }

            var isSeries = episodeNumber is not null || !string.IsNullOrEmpty(episodeTitle);

            var programInfo = new ProgramInfo
            {
                ChannelId = program.ChannelId,
                EndDate = program.EndDate.UtcDateTime,
                EpisodeNumber = episodeNumber,
                EpisodeTitle = episodeTitle,
                Genres = programCategories,
                StartDate = program.StartDate.UtcDateTime,
                Name = program.Title,
                Overview = program.Description,
                ProductionYear = program.CopyrightDate?.Year,
                SeasonNumber = seasonNumber,
                IsSeries = isSeries,
                IsRepeat = program.IsPreviouslyShown && !program.IsNew,
                IsPremiere = program.Premiere is not null,
                IsLive = program.IsLive,
                IsKids = programCategories.Any(c => info.KidsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsMovie = programCategories.Any(c => info.MovieCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsNews = programCategories.Any(c => info.NewsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsSports = programCategories.Any(c => info.SportsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                ImageUrl = string.IsNullOrEmpty(imageUrl) ? null : imageUrl,
                HasImage = !string.IsNullOrEmpty(imageUrl),
                BackdropImageUrl = string.IsNullOrEmpty(backgroundImageUrl) ? null : backgroundImageUrl,
                ThumbImageUrl = string.IsNullOrEmpty(episodeImageUrl) ? null : episodeImageUrl,
                OfficialRating = string.IsNullOrEmpty(rating) ? null : rating,
                CommunityRating = starRating is null ? null : (float)starRating.Value,
                SeriesId = isSeries ? program.Title?.GetMD5().ToString("N", CultureInfo.InvariantCulture) : null
            };

            if (string.IsNullOrWhiteSpace(program.ProgramId))
            {
                string uniqueString = (program.Title ?? string.Empty) + (episodeTitle ?? string.Empty);

                if (programInfo.SeasonNumber.HasValue)
                {
                    uniqueString = "-" + programInfo.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (programInfo.EpisodeNumber.HasValue)
                {
                    uniqueString = "-" + programInfo.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                programInfo.ShowId = uniqueString.GetMD5().ToString("N", CultureInfo.InvariantCulture);

                // If we don't have valid episode info, assume it's a unique program, otherwise recordings might be skipped
                if (programInfo.IsSeries
                    && !programInfo.IsRepeat
                    && (programInfo.EpisodeNumber ?? 0) == 0)
                {
                    programInfo.ShowId += programInfo.StartDate.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                programInfo.ShowId = program.ProgramId;
            }

            // Construct an id from the channel and start date
            programInfo.Id = string.Format(CultureInfo.InvariantCulture, "{0}_{1:O}", program.ChannelId, program.StartDate);

            if (programInfo.IsMovie)
            {
                programInfo.IsSeries = false;
                programInfo.EpisodeNumber = null;
                programInfo.EpisodeTitle = null;
            }

            return programInfo;
        }

        public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        {
            // Saving the provider is an explicit retry, so the download backoff has to be dropped
            // together with the cached file the listings manager deletes.
            if (!string.IsNullOrEmpty(info.Path))
            {
                _lastDownloadFailures.TryRemove(info.Path, out _);
            }

            // Assume all urls are valid. check files for existence
            if (!info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(info.Path))
            {
                throw new FileNotFoundException("Could not find the XmlTv file specified:", info.Path);
            }

            return Task.CompletedTask;
        }

        public async Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        {
            // In theory this should never be called because there is always only one lineup
            string path = await GetXml(info, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));
            IEnumerable<XmlTvChannel> results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new NameIdPair() { Id = c.Id, Name = c.DisplayName }).ToList();
        }

        public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            // In theory this should never be called because there is always only one lineup
            string path = await GetXml(info, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));
            var results = reader.GetChannels();
            var channels = results.ToList();

            // The XmlTv package only exposes a single, language-selected display name per channel,
            // but an XMLTV file often carries many <display-name> aliases (e.g. "AXN", "PL: AXN HD",
            // "AXN PL", ...). Collect all of them so the EPG matcher can use every alias.
            var allNames = ReadAllChannelNames(path);

            // Should this method be async?
            var resultChannels = channels
                .Select(c =>
                {
                    var names = allNames.GetValueOrDefault(c.Id);
                    var name = c.DisplayName ?? names?.FirstOrDefault();

                    string[]? alternateNames = null;
                    if (names is not null && names.Count > 1)
                    {
                        alternateNames = names
                            .Where(n => !string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase))
                            .Select(n => n.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }

                    return new ChannelInfo
                    {
                        Id = c.Id,
                        Name = name,
                        AlternateNames = alternateNames is null || alternateNames.Length == 0 ? Array.Empty<string>() : alternateNames,
                        ImageUrl = string.IsNullOrEmpty(c.Icons.FirstOrDefault()?.Source) ? null : c.Icons.FirstOrDefault()!.Source,
                        Number = string.IsNullOrWhiteSpace(c.Number) ? c.Id : c.Number
                    };
                })
                .ToList();

            return resultChannels;
        }

        /// <summary>
        /// Reads every <c>&lt;display-name&gt;</c> of every <c>&lt;channel&gt;</c> from the XMLTV file,
        /// preserving document order, using a streaming reader so the file is never fully loaded into memory.
        /// </summary>
        /// <param name="path">Path to the (uncompressed) XMLTV file.</param>
        /// <returns>A map of channel id to its list of display names.</returns>
        private static Dictionary<string, List<string>> ReadAllChannelNames(string path)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            using var stream = File.OpenRead(path);
            using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
            {
                // The file is a locally configured/trusted XMLTV source; skip DTD for speed and safety
                // and to tolerate malformed files that still contain useful <channel> data.
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });

            while (xmlReader.Read())
            {
                if (xmlReader.NodeType != XmlNodeType.Element || xmlReader.Name != "channel")
                {
                    continue;
                }

                string? id = xmlReader.GetAttribute("id");
                if (id is null)
                {
                    continue;
                }

                var names = new List<string>();

                // Move to first child of <channel>.
                if (!xmlReader.Read())
                {
                    break;
                }

                while (!(xmlReader.NodeType == XmlNodeType.EndElement && xmlReader.Name == "channel"))
                {
                    if (xmlReader.NodeType == XmlNodeType.Element && xmlReader.Name == "display-name")
                    {
                        string text = xmlReader.ReadElementContentAsString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            names.Add(text.Trim());
                        }

                        // ReadElementContentAsString advances past the <display-name> element.
                        continue;
                    }

                    if (xmlReader.NodeType == XmlNodeType.Element && !xmlReader.IsEmptyElement)
                    {
                        // Skip nested element trees (icons, urls, ...) without recursively parsing them.
                        xmlReader.Skip();
                        continue;
                    }

                    if (!xmlReader.Read())
                    {
                        break;
                    }
                }

                if (names.Count > 0)
                {
                    result[id] = names;
                }
            }

            return result;
        }

        /// <summary>
        /// Builds a stable key that identifies a single programme by its channel and start instant.
        /// </summary>
        /// <param name="channelId">The channel id.</param>
        /// <param name="start">The programme start.</param>
        /// <returns>The lookup key.</returns>
        private static string BuildProgramKey(string channelId, DateTimeOffset start)
            => string.Concat(channelId, "|", start.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture));

        private Dictionary<string, (int? Season, int? Episode)> GetOnscreenEpisodeData(string path)
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            if (_onscreenEpisodeCache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc)
            {
                return cached.Data;
            }

            var data = ReadOnscreenEpisodeData(path);
            _onscreenEpisodeCache[path] = (lastWriteUtc, data);
            return data;
        }

        /// <summary>
        /// Reads the on-screen (<c>system="onscreen"</c>) episode numbers of every programme that has no
        /// <c>&lt;sub-title&gt;</c>, using a streaming reader so the file is never fully loaded into memory.
        /// Programmes with a sub-title are already recognised as series by the XMLTV reader.
        /// </summary>
        /// <param name="path">Path to the (uncompressed) XMLTV file.</param>
        /// <returns>A map of programme key to its parsed season/episode numbers.</returns>
        private static Dictionary<string, (int? Season, int? Episode)> ReadOnscreenEpisodeData(string path)
        {
            var result = new Dictionary<string, (int? Season, int? Episode)>(StringComparer.Ordinal);

            using var stream = File.OpenRead(path);
            using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });

            while (xmlReader.Read())
            {
                if (xmlReader.NodeType != XmlNodeType.Element || xmlReader.Name != "programme" || xmlReader.IsEmptyElement)
                {
                    continue;
                }

                string? channelId = xmlReader.GetAttribute("channel");
                string? start = xmlReader.GetAttribute("start");
                if (channelId is null || start is null)
                {
                    continue;
                }

                var startOffset = ParseXmlTvStart(start);
                if (startOffset is null)
                {
                    continue;
                }

                XElement? onscreen = null;
                using (var subtree = xmlReader.ReadSubtree())
                {
                    var element = XElement.Load(subtree);

                    // A sub-title already marks the programme as a series for the XMLTV reader.
                    if (element.Element("sub-title") is not null)
                    {
                        continue;
                    }

                    onscreen = element.Elements("episode-num")
                        .FirstOrDefault(e => string.Equals((string?)e.Attribute("system"), "onscreen", StringComparison.OrdinalIgnoreCase));
                }

                if (onscreen is null)
                {
                    continue;
                }

                ParseOnscreenEpisode(onscreen.Value, out var season, out var episode);
                result[BuildProgramKey(channelId, startOffset.Value)] = (season, episode);
            }

            return result;
        }

        /// <summary>
        /// Parses the subset of XMLTV date formats ("yyyyMMddHHmmss[ +HHMM]") used by listings providers.
        /// </summary>
        /// <param name="value">The raw start attribute value.</param>
        /// <returns>The parsed start, or <c>null</c> when the value cannot be parsed.</returns>
        private static DateTimeOffset? ParseXmlTvStart(string value)
        {
            var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var digits = parts[0];
            if (digits.Length is < 4 or > 14 || !digits.All(char.IsAsciiDigit))
            {
                return null;
            }

            if (!DateTime.TryParseExact(
                    digits.PadRight(14, '0'),
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateTime))
            {
                return null;
            }

            var offset = TimeSpan.Zero;
            if (parts.Length > 1 && parts[1].Length > 0)
            {
                bool negative = parts[1][0] == '-';
                var offsetDigits = parts[1].TrimStart('+', '-').PadRight(4, '0');
                if (offsetDigits.Length != 4
                    || !int.TryParse(offsetDigits.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                    || !int.TryParse(offsetDigits.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                {
                    return null;
                }

                offset = new TimeSpan(negative ? -hours : hours, negative ? -minutes : minutes, 0);
            }

            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), offset);
        }

        /// <summary>
        /// Parses common on-screen episode notations such as "S2E23" or "2x23".
        /// </summary>
        /// <param name="value">The on-screen episode-num value.</param>
        /// <param name="season">The parsed season number, if any.</param>
        /// <param name="episode">The parsed episode number, if any.</param>
        private static void ParseOnscreenEpisode(string? value, out int? season, out int? episode)
        {
            season = null;
            episode = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            var match = Regex.Match(trimmed, @"^[Ss]?(?<season>\d+)\s*[Ee](?<episode>\d+)$");
            if (!match.Success)
            {
                match = Regex.Match(trimmed, @"^(?<season>\d+)\s*[xX]\s*(?<episode>\d+)$");
            }

            if (!match.Success)
            {
                return;
            }

            if (int.TryParse(match.Groups["season"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seasonNumber))
            {
                season = seasonNumber;
            }

            if (int.TryParse(match.Groups["episode"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber))
            {
                episode = episodeNumber;
            }
        }
    }
}
