using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.MediaEncoding.Transcoding;
using MediaBrowser.Model.Downloads;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Transcoding
{
    public sealed class TranscodeDownloadManagerTests : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly Mock<ITranscodeManager> _transcodeManager = new();
        private readonly TranscodeDownloadManager _manager;

        public TranscodeDownloadManagerTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "download-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);

            _manager = new TranscodeDownloadManager(
                _transcodeManager.Object,
                Mock.Of<ILogger<TranscodeDownloadManager>>());
        }

        public void Dispose()
        {
            _manager.Dispose();

            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }

            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Cancel_UnknownJob_ReturnsFalse()
        {
            Assert.False(_manager.Cancel("does-not-exist"));
        }

        [Fact]
        public void Cancel_OriginalJob_MarksItCancelledWithoutDeletingTheSourceFile()
        {
            var source = CreateFile("source.mkv", 128);
            var info = _manager.EnqueueOriginal("original", Guid.NewGuid(), source, "source.mkv");
            Assert.Equal(DownloadJobStatus.Ready, info.Status);

            Assert.True(_manager.Cancel("original"));

            Assert.Equal(DownloadJobStatus.Cancelled, _manager.GetJob("original")!.Status);

            // The original download points at the media file and must never be deleted.
            Assert.True(File.Exists(source));
        }

        [Fact]
        public async Task Cancel_RunningConversion_StaysCancelledAndDeletesThePartialOutput()
        {
            var output = Path.Combine(_tempDirectory, "converted.mp4");

            StartConversion(output);

            var jobId = StartJob(output);
            await WaitForStatusAsync(jobId, DownloadJobStatus.Converting);

            // Simulate partial output produced by ffmpeg before the cancel.
            await File.WriteAllBytesAsync(output, new byte[256], TestContext.Current.CancellationToken);

            Assert.True(_manager.Cancel(jobId));

            Assert.Equal(DownloadJobStatus.Cancelled, _manager.GetJob(jobId)!.Status);
            Assert.False(File.Exists(output));
        }

        [Fact]
        public async Task Cancel_RunningConversion_IsNotOverwrittenWhenTheProcessFinishes()
        {
            var output = Path.Combine(_tempDirectory, "finished.mp4");

            var transcodingJob = new TranscodingJob(Mock.Of<ILogger<TranscodingJob>>())
            {
                HasExited = false
            };
            _transcodeManager
                .Setup(i => i.StartFfMpeg(
                    It.IsAny<StreamState>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<TranscodingJobType>(),
                    It.IsAny<CancellationTokenSource>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(transcodingJob);

            var jobId = StartJob(output);
            await WaitForStatusAsync(jobId, DownloadJobStatus.Converting);

            await File.WriteAllBytesAsync(output, new byte[256], TestContext.Current.CancellationToken);
            _manager.Cancel(jobId);

            // The ffmpeg process finishes successfully after the cancel. It previously overwrote the
            // cancelled state with Ready, which resurrected an unwanted download.
            transcodingJob.HasExited = true;
            transcodingJob.ExitCode = 0;

            await Task.Delay(1500, TestContext.Current.CancellationToken);

            Assert.Equal(DownloadJobStatus.Cancelled, _manager.GetJob(jobId)!.Status);
        }

        [Fact]
        public async Task Cancel_KeepsTheReadyFileOfAFinishedJob()
        {
            var output = Path.Combine(_tempDirectory, "completed.mp4");

            var transcodingJob = new TranscodingJob(Mock.Of<ILogger<TranscodingJob>>())
            {
                HasExited = true,
                ExitCode = 0
            };
            _transcodeManager
                .Setup(i => i.StartFfMpeg(
                    It.IsAny<StreamState>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<TranscodingJobType>(),
                    It.IsAny<CancellationTokenSource>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(transcodingJob);

            var jobId = StartJob(output);
            await File.WriteAllBytesAsync(output, new byte[256], TestContext.Current.CancellationToken);
            await WaitForStatusAsync(jobId, DownloadJobStatus.Ready);

            // Cancelling a job whose conversion already finished must not delete the converted file:
            // the client may be downloading it right now, and re-converting is expensive.
            Assert.True(_manager.Cancel(jobId));
            Assert.Equal(DownloadJobStatus.Cancelled, _manager.GetJob(jobId)!.Status);
            Assert.True(File.Exists(output));
        }

        private void StartConversion(string output)
        {
            var transcodingJob = new TranscodingJob(Mock.Of<ILogger<TranscodingJob>>())
            {
                HasExited = false
            };

            _transcodeManager
                .Setup(i => i.StartFfMpeg(
                    It.IsAny<StreamState>(),
                    output,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<TranscodingJobType>(),
                    It.IsAny<CancellationTokenSource>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(transcodingJob);
        }

        private string StartJob(string output)
        {
            var jobId = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            var state = new StreamState(
                Mock.Of<MediaBrowser.Controller.Library.IMediaSourceManager>(),
                TranscodingJobType.Progressive,
                _transcodeManager.Object)
            {
                OutputFilePath = output
            };

            _manager.Enqueue(jobId, state, "ffmpeg", Guid.NewGuid(), Guid.NewGuid(), "converted.mp4");
            return jobId;
        }

        private async Task WaitForStatusAsync(string jobId, DownloadJobStatus status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (!timeout.IsCancellationRequested)
            {
                if (_manager.GetJob(jobId)?.Status == status)
                {
                    return;
                }

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"The job did not reach {status} in time.");
        }

        private string CreateFile(string name, int size)
        {
            var path = Path.Combine(_tempDirectory, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }
    }
}
