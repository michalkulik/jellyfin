using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.LiveTv.Listings;
using MediaBrowser.Model.LiveTv;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Listings;

public class XmlTvListingsProviderOnscreenTests
{
    private readonly XmlTvListingsProvider _provider;

    public XmlTvListingsProviderOnscreenTests()
    {
        var messageHandler = new Mock<HttpMessageHandler>();
        messageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(
                (m, _) => Task.FromResult(new HttpResponseMessage()
                {
                    Content = new StreamContent(File.OpenRead(Path.Combine("Test Data/LiveTv/Listings/XmlTv", m.RequestUri!.Segments[^1])))
                }));

        var http = new Mock<IHttpClientFactory>();
        http.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(messageHandler.Object));
        var fixture = new Fixture();
        fixture.Customize(new AutoMoqCustomization { ConfigureMembers = true }).Inject(http);
        _provider = fixture.Create<XmlTvListingsProvider>();
    }

    /// <summary>
    /// Providers such as epg.ovh express the episode number through
    /// <c>&lt;episode-num system="onscreen"&gt;S2E23&lt;/episode-num&gt;</c> without a sub-title.
    /// Those programmes must still be recognised as series episodes so they can be grouped and recorded.
    /// </summary>
    [Fact]
    public async Task GetProgramsAsync_OnscreenEpisodeNumberWithoutSubTitle_IsDetectedAsSeries()
    {
        var info = new ListingsProviderInfo { Path = "Test Data/LiveTv/Listings/XmlTv/onscreen-only.xml" };
        var start = new DateTime(2022, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        var programs = (await _provider.GetProgramsAsync(info, "3297", start, start.AddDays(1), CancellationToken.None)).ToList();

        var onscreenS1E2 = Assert.Single(programs, p => p.Name == "A_onscreen_s1e2");
        Assert.True(onscreenS1E2.IsSeries);
        Assert.Equal(1, onscreenS1E2.SeasonNumber);
        Assert.Equal(2, onscreenS1E2.EpisodeNumber);
        Assert.NotNull(onscreenS1E2.SeriesId);

        var onscreenPadded = Assert.Single(programs, p => p.Name == "B_onscreen_padded");
        Assert.True(onscreenPadded.IsSeries);
        Assert.Equal(1, onscreenPadded.SeasonNumber);
        Assert.Equal(2, onscreenPadded.EpisodeNumber);

        var onscreenAlternative = Assert.Single(programs, p => p.Name == "C_onscreen_x");
        Assert.True(onscreenAlternative.IsSeries);
        Assert.Equal(1, onscreenAlternative.SeasonNumber);
        Assert.Equal(2, onscreenAlternative.EpisodeNumber);

        // Programmes without any episode information must not be treated as series.
        var progIdOnly = Assert.Single(programs, p => p.Name == "F_progid");
        Assert.False(progIdOnly.IsSeries);
    }

    /// <summary>
    /// epg.ovh expresses the episode number as <c>&lt;episode-num system="onscreen"&gt;E16&lt;/episode-num&gt;</c>,
    /// sometimes without a season, together with a range/total suffix. Those programmes carry a
    /// sub-title too, so they used to lose their number entirely.
    /// </summary>
    [Fact]
    public async Task GetProgramsAsync_OnscreenEpisodeVariants_AreParsed()
    {
        var info = new ListingsProviderInfo { Path = "Test Data/LiveTv/Listings/XmlTv/onscreen-variants.xml" };
        var start = new DateTime(2022, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        var programs = (await _provider.GetProgramsAsync(info, "3297", start, start.AddDays(1), CancellationToken.None)).ToList();

        // A sub-title plus "E16": the episode number is known but the season is not.
        var episodeOnly = Assert.Single(programs, p => p.Name == "Była sobie Ziemia");
        Assert.True(episodeOnly.IsSeries);
        Assert.Equal(16, episodeOnly.EpisodeNumber);
        Assert.Null(episodeOnly.SeasonNumber);
        Assert.Equal("Bioróżnorodność", episodeOnly.EpisodeTitle);

        // "E3/26" — the episode index is followed by the total number of episodes.
        var withTotal = Assert.Single(programs, p => p.Name == "B_onscreen_range");
        Assert.Equal(3, withTotal.EpisodeNumber);
        Assert.Null(withTotal.SeasonNumber);

        // "S3E12-ost." — the last episode of a season.
        var lastEpisode = Assert.Single(programs, p => p.Name == "C_onscreen_last");
        Assert.Equal(3, lastEpisode.SeasonNumber);
        Assert.Equal(12, lastEpisode.EpisodeNumber);

        // "S2 E7" — a space between the season and episode markers.
        var spaced = Assert.Single(programs, p => p.Name == "D_onscreen_spaced");
        Assert.Equal(2, spaced.SeasonNumber);
        Assert.Equal(7, spaced.EpisodeNumber);

        // "S1E2/6" — season and episode followed by the total.
        var seasonWithTotal = Assert.Single(programs, p => p.Name == "E_onscreen_total");
        Assert.Equal(1, seasonWithTotal.SeasonNumber);
        Assert.Equal(2, seasonWithTotal.EpisodeNumber);

        // A bare number on a sports programme is an event id, not an episode number.
        var sports = Assert.Single(programs, p => p.Name == "F_onscreen_event_id");
        Assert.False(sports.IsSeries);
        Assert.Null(sports.EpisodeNumber);

        // A bare number on a series without a sub-title is a real episode number.
        var bareSeries = Assert.Single(programs, p => p.Name == "G_onscreen_bare_series");
        Assert.True(bareSeries.IsSeries);
        Assert.Equal(4, bareSeries.EpisodeNumber);
        Assert.Null(bareSeries.SeasonNumber);
    }
}
