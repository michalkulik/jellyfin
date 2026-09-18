using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Downloads;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Downloads;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Transcoding;

/// <inheritdoc cref="ITranscodeDownloadManager"/>
public sealed class TranscodeDownloadManager : ITranscodeDownloadManager, IDisposable
{
    private const int MaxConcurrentConversions = 2;

    private static readonly TimeSpan _jobRetention = TimeSpan.FromHours(12);
    private static readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentConversions, MaxConcurrentConversions);
    private readonly ITranscodeManager _transcodeManager;
    private readonly ILogger<TranscodeDownloadManager> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeDownloadManager"/> class.
    /// </summary>
    /// <param name="transcodeManager">The transcode manager used to run ffmpeg.</param>
    /// <param name="logger">The logger.</param>
    public TranscodeDownloadManager(ITranscodeManager transcodeManager, ILogger<TranscodeDownloadManager> logger)
    {
        _transcodeManager = transcodeManager;
        _logger = logger;
        _cleanupTimer = new Timer(_ => CleanupJobs(), null, _cleanupInterval, _cleanupInterval);
    }

    /// <inheritdoc />
    public DownloadJobInfo Enqueue(
        string jobId,
        StreamState state,
        string commandLine,
        Guid itemId,
        Guid userId,
        string fileName)
    {
        var job = new DownloadJob(jobId, itemId, fileName, state.OutputFilePath);
        _jobs[jobId] = job;

        _ = Task.Run(() => RunJobAsync(job, state, commandLine, userId), CancellationToken.None);

        return job.ToInfo();
    }

    /// <inheritdoc />
    public DownloadJobInfo EnqueueOriginal(string jobId, Guid itemId, string path, string fileName)
    {
        var job = new DownloadJob(jobId, itemId, fileName, path)
        {
            Status = DownloadJobStatus.Ready,
            IsOriginal = true,
            Size = GetFileLength(path)
        };

        _jobs[jobId] = job;

        return job.ToInfo();
    }

    /// <inheritdoc />
    public DownloadJobInfo? GetJob(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job.ToInfo() : null;

    /// <inheritdoc />
    public bool TryGetReadyFile(string jobId, out string? path, out string? fileName)
    {
        path = null;
        fileName = null;

        if (!_jobs.TryGetValue(jobId, out var job) || job.Status != DownloadJobStatus.Ready)
        {
            return false;
        }

        if (!File.Exists(job.OutputPath))
        {
            return false;
        }

        path = job.OutputPath;
        fileName = job.FileName;
        return true;
    }

    /// <inheritdoc />
    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        job.Status = DownloadJobStatus.Cancelled;

        try
        {
            job.CancellationTokenSource?.Cancel();
            job.TranscodingJob?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while cancelling download job {JobId}", jobId);
        }

        if (!job.IsOriginal)
        {
            TryDeleteFile(job.OutputPath);
        }

        return true;
    }

    private async Task RunJobAsync(DownloadJob job, StreamState state, string commandLine, Guid userId)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        job.CancellationTokenSource = cancellationTokenSource;
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            job.Status = DownloadJobStatus.Queued;
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadJobStatus.Cancelled;
            return;
        }

        try
        {
            job.Status = DownloadJobStatus.Converting;

            var transcodingJob = await _transcodeManager.StartFfMpeg(
                state,
                job.OutputPath,
                commandLine,
                userId,
                TranscodingJobType.Progressive,
                cancellationTokenSource).ConfigureAwait(false);

            job.TranscodingJob = transcodingJob;

            while (!transcodingJob.HasExited)
            {
                job.Progress = transcodingJob.CompletionPercentage;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                job.Status = DownloadJobStatus.Cancelled;
                TryDeleteFile(job.OutputPath);
                return;
            }

            var length = GetFileLength(job.OutputPath);
            if (transcodingJob.ExitCode == 0 && length > 0)
            {
                job.Progress = 100;
                job.Size = length;
                job.Status = DownloadJobStatus.Ready;
            }
            else
            {
                job.Status = DownloadJobStatus.Failed;
                job.Error = string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}", transcodingJob.ExitCode);
                TryDeleteFile(job.OutputPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download conversion job {JobId} failed", job.Id);

            job.Status = DownloadJobStatus.Failed;
            job.Error = ex.Message;
            TryDeleteFile(job.OutputPath);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private void CleanupJobs()
    {
        if (_disposed)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _jobRetention;

        foreach (var job in _jobs.Values)
        {
            if (job.CreatedAt > cutoff)
            {
                continue;
            }

            if (_jobs.TryRemove(job.Id, out _) && !job.IsOriginal)
            {
                TryDeleteFile(job.OutputPath);
            }
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to delete download output {Path}", path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cleanupTimer.Dispose();
        _concurrency.Dispose();
    }

    private sealed class DownloadJob
    {
        public DownloadJob(string id, Guid itemId, string fileName, string outputPath)
        {
            Id = id;
            ItemId = itemId;
            FileName = fileName;
            OutputPath = outputPath;
        }

        public string Id { get; }

        public Guid ItemId { get; }

        public string FileName { get; }

        public string OutputPath { get; }

        public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;

        public double? Progress { get; set; }

        public long? Size { get; set; }

        public string? Error { get; set; }

        public bool IsOriginal { get; set; }

        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public TranscodingJob? TranscodingJob { get; set; }

        public CancellationTokenSource? CancellationTokenSource { get; set; }

        public DownloadJobInfo ToInfo() => new()
        {
            Id = Id,
            ItemId = ItemId,
            Status = Status,
            Progress = Progress,
            FileName = FileName,
            Size = Size,
            Error = Error
        };
    }
}
