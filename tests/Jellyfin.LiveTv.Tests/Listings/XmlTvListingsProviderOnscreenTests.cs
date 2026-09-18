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
}
