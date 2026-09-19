using System;
using System.Security.Claims;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class ItemRefreshControllerTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly ItemRefreshController _subject;

    public ItemRefreshControllerTests()
    {
        _subject = new ItemRefreshController(
            _libraryManager.Object,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
            }
        };
    }

    [Fact]
    public void ScanItem_UnknownItem_ReturnsNotFound()
    {
        _libraryManager
            .Setup(m => m.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns((BaseItem?)null);

        Assert.IsType<NotFoundResult>(_subject.ScanItem(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(typeof(CollectionFolder))]
    [InlineData(typeof(Series))]
    [InlineData(typeof(Season))]
    public void ScanItem_ScannableType_QueuesTheScan(Type itemType)
    {
        var item = (BaseItem)Activator.CreateInstance(itemType)!;
        item.Id = Guid.NewGuid();
        _libraryManager
            .Setup(m => m.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>()))
            .Returns(item);

        Assert.IsType<NoContentResult>(_subject.ScanItem(item.Id));
        _libraryManager.Verify(m => m.QueueItemLibraryScan(item.Id), Times.Once());
    }

    [Theory]
    [InlineData(typeof(Movie))]
    [InlineData(typeof(Episode))]
    public void ScanItem_NonScannableType_ReturnsBadRequest(Type itemType)
    {
        var item = (BaseItem)Activator.CreateInstance(itemType)!;
        item.Id = Guid.NewGuid();
        _libraryManager
            .Setup(m => m.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>()))
            .Returns(item);

        Assert.IsType<BadRequestResult>(_subject.ScanItem(item.Id));
        _libraryManager.Verify(m => m.QueueItemLibraryScan(It.IsAny<Guid>()), Times.Never());
    }
}
