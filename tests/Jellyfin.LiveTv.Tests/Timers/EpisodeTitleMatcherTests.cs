using Jellyfin.LiveTv.Timers;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Timers
{
    public static class EpisodeTitleMatcherTests
    {
        [Theory]
        // A recording that was moved from the recordings folder into a regular library keeps the
        // recording file name, so the guide title only appears inside it.
        [InlineData("Reksio 2026_02_05_10_55_00 - Reksiowa wiosna", "Reksiowa wiosna")]
        [InlineData("Reksio 2026_01_24_10_45_00 - Reksio poszukiwacz", "Reksio poszukiwacz")]
        [InlineData("Dummy Reksio 2026_01_21_10_55_00 - Reksio magik", "Reksio magik")]
        // The episode is stored in a season sub folder, which is part of the path and not the name.
        [InlineData("Reksio 2026_02_08_10_50_00 - Reksio i świerszczyk", "Reksio i świerszczyk")]
        public static void MatchesEpisode_RecordingFileName_Matches(string fileName, string episodeTitle)
        {
            Assert.True(EpisodeTitleMatcher.MatchesEpisode(fileName, null, episodeTitle));
        }

        [Theory]
        [InlineData("Reksio 11 DVDRip XviD [R68]", "Reksiowa wiosna")]
        [InlineData("Reksio 11 DVDRip XviD [R68]", "Reksio dentysta")]
        // A shorter title must not match a longer one.
        [InlineData("Reksio 2026_02_07_10_55_00 - Reksio i koguty", "Reksio i kogut")]
        public static void MatchesEpisode_DifferentEpisode_DoesNotMatch(string fileName, string episodeTitle)
        {
            Assert.False(EpisodeTitleMatcher.MatchesEpisode(fileName, null, episodeTitle));
        }

        [Fact]
        public static void MatchesEpisode_PathIsUsedWhenTheNameIsEmpty()
        {
            Assert.True(EpisodeTitleMatcher.MatchesEpisode(
                string.Empty,
                "/media/emby/seriale-dzieci/Reksio/Sezon 2/Reksio 2026_02_05_10_55_00 - Reksiowa wiosna.mkv",
                "Reksiowa wiosna"));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("Reksio", null)]
        [InlineData("Reksio", "")]
        public static void MatchesEpisode_MissingData_DoesNotMatch(string? fileName, string? episodeTitle)
        {
            Assert.False(EpisodeTitleMatcher.MatchesEpisode(fileName, null, episodeTitle));
        }

        [Fact]
        public static void MatchesEpisode_IgnoresCaseAndPunctuation()
        {
            Assert.True(EpisodeTitleMatcher.MatchesEpisode(
                "Reksio 2026_02_05_10_55_00 - REKSIOWA WIOSNA",
                null,
                "Reksiowa wiosna."));
        }

        [Theory]
        [InlineData("Reksio", "Reksio", true)]
        [InlineData("Reksio", "reksio", true)]
        // A feed sometimes repeats the series name as the episode title, which would otherwise match
        // every single episode of the series.
        [InlineData("Klub przyjaciół Myszki Miki", "Klub Myszki Miki Plus", false)]
        [InlineData("Reksio", "Reksiowa wiosna", false)]
        public static void IsSameAsSeriesName_ComparesNormalizedNames(string seriesName, string episodeTitle, bool expected)
        {
            Assert.Equal(expected, EpisodeTitleMatcher.IsSameAsSeriesName(seriesName, episodeTitle));
        }

        [Theory]
        // Punctuation and separators differ between the guide and the library.
        [InlineData("Jej Wysokość Zosia Królewska Szkoła Magii", "Jej Wysokość Zosia: Królewska Szkoła Magii")]
        [InlineData("Myszka Miki Frajdomek", "Myszka Miki: Frajdomek")]
        [InlineData("Super Wings Przygody Superlotków", "Super Wings: Przygody Superlotków")]
        public static void NormalizeName_RemovesPunctuationDifferences(string first, string second)
        {
            Assert.Equal(EpisodeTitleMatcher.NormalizeName(first), EpisodeTitleMatcher.NormalizeName(second));
        }

        [Fact]
        public static void NormalizeName_KeepsThePlusAsAWord()
        {
            // The trailing "+" marks a spin-off, so it must survive normalization.
            Assert.Equal("klub myszki miki plus", EpisodeTitleMatcher.NormalizeName("Klub Myszki Miki+"));
            Assert.Equal("klub myszki miki plus", EpisodeTitleMatcher.NormalizeName("Klub Myszki Miki Plus"));
        }

        [Fact]
        public static void NormalizeName_DoesNotMergeASeriesWithItsSpinOff()
        {
            // Two different series in this library, they must never compare equal.
            Assert.NotEqual(
                EpisodeTitleMatcher.NormalizeName("Klub przyjaciół Myszki Miki"),
                EpisodeTitleMatcher.NormalizeName("Klub przyjaciół Myszki Miki+"));
        }

        [Theory]
        // The library may call a series differently from the guide.
        [InlineData("Reksio", "/media/emby/seriale-dzieci/Reksio", "Reksio", true)]
        // The folder holds the moved recordings under the guide name while the series itself was
        // renamed by metadata to the spin-off title, so the folder name has to be considered.
        [InlineData("Klub przyjaciół Myszki Miki+", "/media/emby/seriale-dzieci/Klub Myszki Miki Plus", "Klub Myszki Miki Plus", true)]
        // ... or the folder does, while the series name is the one from the guide.
        [InlineData("Klub Myszki Miki Plus", "/media/emby_recordings/Klub Myszki Miki Plus", "Klub Myszki Miki Plus", true)]
        // The other spin-off must not be treated as the same series.
        [InlineData("Klub przyjaciół Myszki Miki", "/media/emby_recordings/Klub przyjaciół Myszki Miki", "Klub Myszki Miki Plus", false)]
        [InlineData("Klub przyjaciół Myszki Miki+", "/media/emby/seriale-dzieci/Klub Myszki Miki Plus", "Klub przyjaciół Myszki Miki", false)]
        // A season is stored as a number, "Superkoty 2" is a different series from "Superkoty".
        [InlineData("Superkoty 2", "/media/emby_recordings/Superkoty 2", "Superkoty", false)]
        [InlineData("Reksio", "/media/emby/seriale-dzieci/Reksio", "Superkoty", false)]
        [InlineData("Reksio", "/media/emby/seriale-dzieci/Reksio", null, false)]
        public static void IsSameSeries_MatchesNameOrFolder(string seriesName, string seriesPath, string? guideName, bool expected)
        {
            Assert.Equal(expected, EpisodeTitleMatcher.IsSameSeries(seriesName, seriesPath, guideName));
        }

        [Fact]
        public static void NormalizeName_GivesNullForBlankInput()
        {
            Assert.Equal(string.Empty, EpisodeTitleMatcher.NormalizeName(null));
            Assert.Equal(string.Empty, EpisodeTitleMatcher.NormalizeName("   "));
        }

        [Theory]
        [InlineData("reksio i koty", "reksio i kot", false)]
        [InlineData("reksio i kot", "reksio i kot", true)]
        [InlineData("reksio aktor", "reksio", true)]
        [InlineData("reksio", "reksio", true)]
        [InlineData("", "reksio", false)]
        public static void ContainsWholePhrase_MatchesOnlyWholeWords(string haystack, string needle, bool expected)
        {
            Assert.Equal(expected, EpisodeTitleMatcher.ContainsWholePhrase(haystack, needle));
        }
    }
}
