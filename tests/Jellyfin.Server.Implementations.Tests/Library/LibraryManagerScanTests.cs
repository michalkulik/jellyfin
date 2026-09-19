using System;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Tasks;
using Moq;
using Xunit;
using ServerLibraryManager = Emby.Server.Implementations.Library.LibraryManager;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class LibraryManagerScanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartScanInBackground_QueuesOnlyWhenIdle(bool scanRunning)
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        var tasks = fixture.Freeze<Mock<ITaskManager>>();
        var manager = fixture.Create<ServerLibraryManager>();
        typeof(ServerLibraryManager).GetProperty(nameof(ServerLibraryManager.IsScanRunning))!.SetValue(manager, scanRunning);

        await manager.StartScanInBackground().ConfigureAwait(true);

        tasks.Verify(t => t.QueueScheduledTask<RefreshMediaLibraryTask>(), scanRunning ? Times.Never() : Times.Once());
        tasks.Verify(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>(), Times.Never());
    }

    [Fact]
    public async Task ValidateMediaLibrary_RestartsScheduledScan()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        var tasks = fixture.Freeze<Mock<ITaskManager>>();
        var manager = fixture.Create<ServerLibraryManager>();

        await manager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(true);

        tasks.Verify(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>(), Times.Once());
        tasks.Verify(t => t.QueueScheduledTask<RefreshMediaLibraryTask>(), Times.Never());
    }

    [Fact]
    public void QueueItemLibraryScan_UnknownItem_DoesNotQueueAnything()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        var repository = fixture.Freeze<Mock<IItemRepository>>();
        repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((BaseItem)null!);
        var tasks = fixture.Freeze<Mock<ITaskManager>>();
        var manager = fixture.Create<ServerLibraryManager>();

        manager.QueueItemLibraryScan(Guid.NewGuid());

        tasks.Verify(t => t.Execute(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never());
    }

    [Fact]
    public void QueueItemLibraryScan_KnownItem_QueuesScopedScan()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        var tasks = fixture.Freeze<Mock<ITaskManager>>();
        var manager = fixture.Create<ServerLibraryManager>();

        var item = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies" };
        manager.RegisterItem(item);

        manager.QueueItemLibraryScan(item.Id);

        tasks.Verify(
            t => t.Execute(It.Is<IScheduledTask>(task => task is RefreshItemLibraryTask), It.IsAny<TaskOptions>()),
            Times.Once());
    }
}
