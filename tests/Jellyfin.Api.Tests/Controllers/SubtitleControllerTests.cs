using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Results;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class SubtitleControllerTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly SubtitleController _subject;

    public SubtitleControllerTests()
    {
        _subject = new SubtitleController(
            Mock.Of<IServerConfigurationManager>(),
            _libraryManager.Object,
            Mock.Of<ISubtitleManager>(),
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILogger<SubtitleController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
            }
        };
    }

    [Fact]
    public void GetSubtitleDownloadLanguages_UnknownItem_ReturnsNotFound()
    {
        _libraryManager
            .Setup(m => m.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns((BaseItem?)null);

        var result = _subject.GetSubtitleDownloadLanguages(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetSubtitleDownloadLanguages_ConfiguredLanguages_ReturnsThem()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        _libraryManager
            .Setup(m => m.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>()))
            .Returns(item);
        _libraryManager
            .Setup(m => m.GetLibraryOptions(item))
            .Returns(new LibraryOptions { SubtitleDownloadLanguages = ["pol", "eng"] });

        var result = _subject.GetSubtitleDownloadLanguages(item.Id);

        var okResult = Assert.IsType<OkResult<IEnumerable<string>>>(result.Result);
        var languages = Assert.IsAssignableFrom<IEnumerable<string>>(okResult.Value);
        Assert.Equal(new[] { "pol", "eng" }, languages);
    }

    [Fact]
    public void GetSubtitleDownloadLanguages_NoConfiguredLanguages_ReturnsEmpty()
    {
        // A null list means subtitle downloading is switched off for the library. Clients get an
        // empty list instead of null so they can treat it as "nothing configured" without a null check.
        var item = new Movie { Id = Guid.NewGuid() };
        _libraryManager
            .Setup(m => m.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>()))
            .Returns(item);
        _libraryManager
            .Setup(m => m.GetLibraryOptions(item))
            .Returns(new LibraryOptions { SubtitleDownloadLanguages = null });

        var result = _subject.GetSubtitleDownloadLanguages(item.Id);

        var okResult = Assert.IsType<OkResult<IEnumerable<string>>>(result.Result);
        var languages = Assert.IsAssignableFrom<IEnumerable<string>>(okResult.Value);
        Assert.Empty(languages);
    }
}
