#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.Listings
{
    internal class EpgChannelData
    {
        // Minimum match score (0..1) required before a partial name match is accepted.
        private const double MinimumPartialMatchScore = 0.8;

        // Feed-resolution adjectives are normalised to a common representative so "AXN HD" and
        // "AXN FHD" match, while genuinely different feeds like "TVP HD" and "TVP 4K" remain
        // distinguishable.
        private static readonly Dictionary<string, string> ResolutionAliases = new(StringComparer.Ordinal)
        {
            ["fhd"] = "hd",
            ["fullhd"] = "hd",
            ["uhd"] = "4k",
            ["hdr"] = "hdr",
            ["3d"] = "3d",
            ["sd"] = "sd"
        };

        private static readonly Regex CountryPrefixRegex = new(
            @"^(?:pl|po|cz|sk)\s*[:|\-]\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CountryBarRegex = new(
            @"^(?:pl)\s*\|\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex NonWordRegex = new(
            @"[^\p{L}\p{Nd}]+",
            RegexOptions.CultureInvariant);

        private readonly Dictionary<string, ChannelInfo> _channelsById;

        private readonly Dictionary<string, ChannelInfo> _channelsByNumber;

        private readonly Dictionary<string, ChannelInfo> _channelsByName;

        private readonly List<NameRecord> _nameRecords;

        public EpgChannelData(IEnumerable<ChannelInfo> channels)
        {
            _channelsById = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByNumber = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByName = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _nameRecords = new List<NameRecord>();

            foreach (var channel in channels)
            {
                _channelsById[channel.Id] = channel;

                if (!string.IsNullOrEmpty(channel.Number))
                {
                    _channelsByNumber[channel.Number] = channel;
                }

                // Index every alias the channel is known by: the primary name plus alternatives.
                var names = new[] { channel.Name ?? string.Empty }
                    .Concat(channel.AlternateNames ?? Array.Empty<string>());

                foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
                {
                    _nameRecords.Add(new NameRecord(channel, name.Trim()));

                    var key = NormalizeName(name);
                    if (!string.IsNullOrEmpty(key) && !_channelsByName.ContainsKey(key))
                    {
                        _channelsByName[key] = channel;
                    }
                }
            }
        }

        public ChannelInfo? GetChannelById(string id)
            => _channelsById.GetValueOrDefault(id);

        public ChannelInfo? GetChannelByNumber(string number)
            => _channelsByNumber.GetValueOrDefault(number);

        /// <summary>
        /// Exact lookup by a single normalised display name.
        /// </summary>
        /// <param name="name">The channel display name to look up.</param>
        /// <returns>The matched EPG channel, or <c>null</c> if no exact match exists.</returns>
        public ChannelInfo? GetChannelByName(string name)
            => _channelsByName.GetValueOrDefault(NormalizeName(name ?? string.Empty));

        /// <summary>
        /// Finds the EPG channel whose display name best matches the given tuner channel name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// All aliases of an EPG channel (primary <see cref="ChannelInfo.Name"/> plus any
        /// <see cref="ChannelInfo.AlternateNames"/>) participate in the lookup, which is what
        /// lets a playlist name such as "PL: Viasat History" match an EPG channel whose primary
        /// label differs (e.g. "Polsat Viasat History") but which lists "PL: Viasat History" as
        /// an alternative name.
        /// </para>
        /// <para>
        /// An exact (normalised) match always wins. When there is no exact match a conservative
        /// partial match is allowed, but only when a single channel is a clear, unmatched winner;
        /// ambiguous results intentionally return <c>null</c> to avoid silently mis-assigning a
        /// tune channel to the wrong guide entry.
        /// </para>
        /// </remarks>
        /// <param name="name">The tuner channel name to look up.</param>
        /// <returns>The matched EPG channel, or <c>null</c> if no confident match exists.</returns>
        public ChannelInfo? MatchByQueryName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var query = Tokenize(name);
            if (query.Length == 0)
            {
                return null;
            }

            string exactKey = string.Join(" ", query);

            List<NameRecord>? exact = null;
            foreach (var record in _nameRecords)
            {
                if (string.Equals(record.Normalized, exactKey, StringComparison.OrdinalIgnoreCase))
                {
                    (exact ??= new List<NameRecord>()).Add(record);
                }
            }

            if (exact is not null)
            {
                return Unanimous(exact)?.Channel;
            }

            return FindBestPartialMatch(query);
        }

        private ChannelInfo? FindBestPartialMatch(string[] query)
        {
            NameRecord? best = null;
            double bestScore = 0;
            bool ambiguous = false;

            foreach (var record in _nameRecords)
            {
                var score = HighestPrefixScore(query, record.Tokens);
                if (score < MinimumPartialMatchScore)
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = record;
                    ambiguous = false;
                }
                else if (Math.Abs(score - bestScore) < 0.0001
                         && best.HasValue
                         && !ReferenceEquals(best.Value.Channel, record.Channel))
                {
                    ambiguous = true;
                }
            }

            if (best is null || ambiguous)
            {
                return null;
            }

            return best.Value.Channel;
        }

        private static double HighestPrefixScore(string[] query, string[] candidate)
        {
            var matched = 0;
            var max = Math.Max(query.Length, candidate.Length);
            var min = Math.Min(query.Length, candidate.Length);

            while (matched < min && string.Equals(query[matched], candidate[matched], StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }

            if (matched == 0)
            {
                return 0;
            }

            var score = (double)matched / max;

            // Prefer candidates where the query is an exact prefix match of the candidate or vice versa.
            if (matched == min)
            {
                score += 0.2;
            }

            return score;
        }

        private static NameRecord? Unanimous(List<NameRecord> records)
        {
            var first = records[0];
            for (var i = 1; i < records.Count; i++)
            {
                if (!ReferenceEquals(first.Channel, records[i].Channel))
                {
                    return null;
                }
            }

            return first;
        }

        /// <summary>
        /// Produces a strongly normalised, order-preserving key for a channel name.
        /// </summary>
        /// <param name="value">The channel name to normalise.</param>
        /// <returns>The normalised key, or an empty string when <paramref name="value"/> has no
        /// informative content.</returns>
        public static string NormalizeName(string value)
        {
            return string.Join(" ", Tokenize(value ?? string.Empty));
        }

        private static string[] Tokenize(string value)
        {
            // Strip parenthetical notes such as "(without guarantee)".
            value = StripParentheses(value);

            // Country prefix markers ("PL:", "PL|") carry no matching information.
            value = CountryPrefixRegex.Replace(value, " ");
            value = CountryBarRegex.Replace(value, " ");

            var words = NonWordRegex.Split(value.ToLowerInvariant())
                .Where(w => w.Length > 0)
                .ToList();

            // Collapse resolution adjectives and drop duplicate informative words.
            var result = new List<string>(words.Count);
            foreach (var raw in words)
            {
                var word = ResolutionAliases.TryGetValue(raw, out var alias) ? alias : raw;

                if (result.Contains(word))
                {
                    continue;
                }

                result.Add(word);
            }

            // A trailing standalone "pl" is a country marker and not part of the channel brand.
            if (result.Count > 0 && result[^1] == "pl")
            {
                result.RemoveAt(result.Count - 1);
            }

            return result.ToArray();
        }

        private static string StripParentheses(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            var depth = 0;
            foreach (var c in value)
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                }
                else if (depth == 0)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// A single (normalised) display name variant of an EPG channel.
        /// </summary>
        private struct NameRecord
        {
            public NameRecord(ChannelInfo channel, string name)
            {
                Channel = channel;
                Normalized = NormalizeName(name);
                Tokens = Tokenize(name);
            }

            public ChannelInfo Channel { get; }

            public string Normalized { get; }

            public string[] Tokens { get; }
        }
    }
}
