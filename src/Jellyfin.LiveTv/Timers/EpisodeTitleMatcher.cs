using System;
using System.IO;
using System.Text;

namespace Jellyfin.LiveTv.Timers
{
    /// <summary>
    /// Matches guide episodes against episodes that are already in a library by their title.
    /// </summary>
    /// <remarks>
    /// Many XMLTV feeds, including the Polish ones used by this fork, carry only a title and no
    /// season/episode numbers. Episodes recorded by Jellyfin are also stored under their file name
    /// (for example "Reksio 2026_02_05_10_55_00 - Reksiowa wiosna") rather than under a clean title, so
    /// plain name comparison would never find them once they are moved into a regular library.
    /// </remarks>
    public static class EpisodeTitleMatcher
    {
        /// <summary>
        /// Whether the series of a library holds the series the guide reports.
        /// </summary>
        /// <remarks>
        /// The library often names a series differently from the guide, and the folder on disk may
        /// again use another name (this library keeps "Klub Myszki Miki Plus" in a folder while the
        /// series itself is called "Klub przyjaciół Myszki Miki+"), so both are compared.
        /// </remarks>
        /// <param name="seriesName">Name of the library series.</param>
        /// <param name="seriesPath">Path of the library series.</param>
        /// <param name="guideName">Name the guide uses for the series.</param>
        /// <returns>True when the library series is the one the guide reports.</returns>
        public static bool IsSameSeries(string? seriesName, string? seriesPath, string? guideName)
        {
            var wanted = NormalizeName(guideName);
            if (wanted.Length == 0)
            {
                return false;
            }

            return string.Equals(NormalizeName(seriesName), wanted, StringComparison.Ordinal)
                || string.Equals(GetLeafFolderName(seriesPath), wanted, StringComparison.Ordinal);
        }

        /// <summary>
        /// Normalized name of the last folder of a path, empty when there is none.
        /// </summary>
        /// <param name="path">Path to inspect.</param>
        /// <returns>The normalized folder name, never null.</returns>
        public static string GetLeafFolderName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return NormalizeName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        /// <summary>
        /// Whether an episode with the given name or file name is the guide episode with the given title.
        /// </summary>
        /// <param name="episodeName">Name of the library episode, may be null.</param>
        /// <param name="episodePath">Path of the library episode, may be null.</param>
        /// <param name="episodeTitle">Title reported by the guide.</param>
        /// <returns>True when the episode matches the title.</returns>
        public static bool MatchesEpisode(string? episodeName, string? episodePath, string? episodeTitle)
        {
            var wanted = NormalizeName(episodeTitle);
            if (wanted.Length == 0)
            {
                return false;
            }

            if (ContainsWholePhrase(NormalizeName(episodeName), wanted))
            {
                return true;
            }

            var fileName = string.IsNullOrWhiteSpace(episodePath)
                ? null
                : Path.GetFileNameWithoutExtension(episodePath);

            return ContainsWholePhrase(NormalizeName(fileName), wanted);
        }

        /// <summary>
        /// Whether the title the guide reports for the episode is in fact the name of the series. Some
        /// feeds repeat the series name as the episode title, and such a title would match every
        /// episode of the series.
        /// </summary>
        /// <param name="seriesName">Name of the series.</param>
        /// <param name="episodeTitle">Title reported by the guide.</param>
        /// <returns>True when both describe the same name.</returns>
        public static bool IsSameAsSeriesName(string? seriesName, string? episodeTitle)
            => string.Equals(NormalizeName(seriesName), NormalizeName(episodeTitle), StringComparison.Ordinal);

        /// <summary>
        /// Lowercases a name and reduces every run of characters that are not letters or digits to a
        /// single space, so names that only differ by punctuation compare equal.
        /// </summary>
        /// <param name="value">Value to normalize.</param>
        /// <returns>The normalized name, never null.</returns>
        public static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    if (pendingSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = true;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Whether the needle occurs in the haystack as a whole phrase, so "reksio i kot" does not match
        /// "reksio i koty".
        /// </summary>
        /// <param name="haystack">Normalized text to search in.</param>
        /// <param name="needle">Normalized text to look for.</param>
        /// <returns>True when the phrase occurs on its own.</returns>
        public static bool ContainsWholePhrase(string? haystack, string? needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            {
                return false;
            }

            var index = haystack.IndexOf(needle, StringComparison.Ordinal);

            while (index >= 0)
            {
                var end = index + needle.Length;
                var startsAtBoundary = index == 0 || haystack[index - 1] == ' ';
                var endsAtBoundary = end == haystack.Length || haystack[end] == ' ';

                if (startsAtBoundary && endsAtBoundary)
                {
                    return true;
                }

                index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
            }

            return false;
        }
    }
}
