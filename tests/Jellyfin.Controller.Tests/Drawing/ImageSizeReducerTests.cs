using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Controller.Tests.Drawing
{
    public sealed class ImageSizeReducerTests : IDisposable
    {
        private const long MaxBudgetBytes = 1024;

        private readonly string _tempDirectory;

        public ImageSizeReducerTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "image-size-reducer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }

            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task ReduceAsync_SmallEnoughImage_IsNotProcessed()
        {
            var path = CreateFile("poster.jpg", 1024);
            var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Strict);

            var reduced = await ImageSizeReducer.ReduceAsync(
                imageProcessor.Object,
                CreateItem(),
                path,
                2048,
                CancellationToken.None);

            Assert.False(reduced);
            Assert.Equal(1024, new FileInfo(path).Length);
        }

        [Fact]
        public async Task ReduceAsync_LargeImage_IsReplacedBySmallerVersion()
        {
            var path = CreateFile("poster.jpg", 4096);
            var replacement = CreateFile("replacement.jpg", 512);

            var imageProcessor = new Mock<IImageProcessor>();
            imageProcessor
                .Setup(i => i.ProcessImage(It.IsAny<ImageProcessingOptions>()))
                .ReturnsAsync((Path: replacement, MimeType: (string?)"image/jpeg", DateModified: DateTime.UtcNow));

            var reduced = await ImageSizeReducer.ReduceAsync(
                imageProcessor.Object,
                CreateItem(),
                path,
                MaxBudgetBytes,
                CancellationToken.None);

            Assert.True(reduced);
            Assert.Equal(512, new FileInfo(path).Length);
        }

        [Fact]
        public async Task ReduceAsync_ProcessorReturnsOriginal_KeepsImage()
        {
            var path = CreateFile("poster.jpg", 4096);

            var imageProcessor = new Mock<IImageProcessor>();
            imageProcessor
                .Setup(i => i.ProcessImage(It.IsAny<ImageProcessingOptions>()))
                .Returns((ImageProcessingOptions options) => Task.FromResult((Path: options.Image.Path, MimeType: (string?)"image/jpeg", DateModified: DateTime.UtcNow)));

            var reduced = await ImageSizeReducer.ReduceAsync(
                imageProcessor.Object,
                CreateItem(),
                path,
                MaxBudgetBytes,
                CancellationToken.None);

            Assert.False(reduced);
            Assert.Equal(4096, new FileInfo(path).Length);
        }

        [Fact]
        public async Task ReduceAsync_LargerReplacement_IsIgnored()
        {
            var path = CreateFile("poster.jpg", 4096);
            var replacement = CreateFile("replacement.jpg", 8192);

            var imageProcessor = new Mock<IImageProcessor>();
            imageProcessor
                .Setup(i => i.ProcessImage(It.IsAny<ImageProcessingOptions>()))
                .ReturnsAsync((Path: replacement, MimeType: (string?)"image/jpeg", DateModified: DateTime.UtcNow));

            var reduced = await ImageSizeReducer.ReduceAsync(
                imageProcessor.Object,
                CreateItem(),
                path,
                MaxBudgetBytes,
                CancellationToken.None);

            Assert.False(reduced);
            Assert.Equal(4096, new FileInfo(path).Length);
        }

        [Fact]
        public async Task ReduceAsync_KeepsTryingUntilBudgetIsMet()
        {
            var path = CreateFile("poster.jpg", 8192);

            // Every attempt returns a file that is still too large, so all steps are used.
            var attempts = new List<int>();

            var imageProcessor = new Mock<IImageProcessor>();
            imageProcessor
                .Setup(i => i.ProcessImage(It.IsAny<ImageProcessingOptions>()))
                .Returns((ImageProcessingOptions options) =>
                {
                    attempts.Add(options.MaxWidth ?? 0);
                    var generated = CreateFile($"attempt-{attempts.Count}.jpg", 4096);
                    return Task.FromResult((Path: generated, MimeType: (string?)"image/jpeg", DateModified: DateTime.UtcNow));
                });

            var reduced = await ImageSizeReducer.ReduceAsync(
                imageProcessor.Object,
                CreateItem(),
                path,
                MaxBudgetBytes,
                CancellationToken.None);

            Assert.True(reduced);
            Assert.Equal(4, attempts.Count);
            Assert.Equal(4096, new FileInfo(path).Length);
        }

        private BaseItem CreateItem() => new LiveTvProgram();

        private string CreateFile(string name, int size)
        {
            var path = Path.Combine(_tempDirectory, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }
    }
}
