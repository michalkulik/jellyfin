using System;
using Jellyfin.LiveTv.Listings;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Listings;

public class EpgChannelDataTests
{
    private static ChannelInfo Channel(string id, string name, params string[] alternates)
        => new()
        {
            Id = id,
            Name = name,
            AlternateNames = alternates.Length == 0 ? Array.Empty<string>() : alternates
        };

    [Fact]
    public void ExactPrimaryName_Matches()
    {
        var epg = new EpgChannelData(new[]
        {
            Channel("AXN", "AXN")
        });

        Assert.Equal("AXN", epg.MatchByQueryName("AXN")?.Id);
    }

    [Fact]
    public void PrimaryNameOfAnotherChannel_DoesNotMatch()
    {
        var epg = new EpgChannelData(new[]
        {
            Channel("c1", "Canal+ Film")
        });

        Assert.Null(epg.MatchByQueryName("Canal+ Seriale"));
    }

    [Fact]
    public void AlternateName_Matches_EvenWhenPrimaryDiffers()
    {
        // The EPG channel is primarily known as "Polsat Viasat History" but also advertises the
        // "PL: Viasat History" alias used by the playlist.
        var epg = new EpgChannelData(new[]
        {
            Channel("polvat-his", "Polsat Viasat History", "PL: Viasat History")
        });

        Assert.Equal("polvat-his", epg.MatchByQueryName("PL: Viasat History")?.Id);
        Assert.Equal("polvat-his", epg.MatchByQueryName("PL: Viasat History HD")?.Id);
    }

    [Fact]
    public void CountryAndQualityMarker_AreNormalised()
    {
        var epg = new EpgChannelData(new[]
        {
            Channel("c1", "AXN", "PL: AXN HD", "AXN PL", "AXN FHD")
        });

        Assert.Equal("c1", epg.MatchByQueryName("PL: AXN")?.Id);
        Assert.Equal("c1", epg.MatchByQueryName("AXN HD")?.Id);
        Assert.Equal("c1", epg.MatchByQueryName("AXN FHD")?.Id);
        Assert.Equal("c1", epg.MatchByQueryName("AXN PL")?.Id);
    }

    [Fact]
    public void DistinctResolutionFeeds_StayDistinct()
    {
        // "TVP HD" and "TVP 4K" are separate guide channels; a "TVP HD" playlist entry must not
        // be mapped onto the 4K feed.
        var epg = new EpgChannelData(new[]
        {
            Channel("tvp-hd", "TVP HD"),
            Channel("tvp-4k", "TVP 4K")
        });

        Assert.Equal("tvp-hd", epg.MatchByQueryName("PL: TVP HD")?.Id);
        Assert.Equal("tvp-4k", epg.MatchByQueryName("TVP 4K")?.Id);
    }

    [Fact]
    public void PartialName_MatchesUniqueChannel()
    {
        var epg = new EpgChannelData(new[]
        {
            Channel("bbc-world", "BBC World News"),
            Channel("other", "Some Other Channel")
        });

        // Query name carries an extra informative suffix; the unique prefix match wins.
        Assert.Equal("bbc-world", epg.MatchByQueryName("BBC World News Europe")?.Id);
    }

    [Fact]
    public void AmbiguousName_ReturnsNull()
    {
        // Two unrelated guide channels share the same alias; matching must stay conservative.
        var epg = new EpgChannelData(new[]
        {
            Channel("fr-uk", "France 24 UK", "France 24"),
            Channel("fr-fr", "France 24 FR", "France 24")
        });

        Assert.Null(epg.MatchByQueryName("France 24"));
    }

    [Fact]
    public void MissingName_ReturnsNull()
    {
        var epg = new EpgChannelData(new[]
        {
            Channel("c1", "RT"),  // note: 'RT HD' is not present
            Channel("c2", "Polsat")
        });

        Assert.Null(epg.MatchByQueryName("Bloomberg TV"));
    }

    [Fact]
    public void GetChannelById_AndByNumber_StillWork()
    {
        var epg = new EpgChannelData(new[]
        {
            new ChannelInfo
            {
                Id = "ch854",
                Name = "13 Ulica",
                Number = "854"
            }
        });

        Assert.Equal("13 Ulica", epg.GetChannelById("ch854")?.Name);
        Assert.Equal("ch854", epg.GetChannelByNumber("854")?.Id);
        Assert.Equal("ch854", epg.GetChannelByName("13 Ulica")?.Id);
    }
}
