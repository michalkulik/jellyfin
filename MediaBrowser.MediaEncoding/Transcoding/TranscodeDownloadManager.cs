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

    /// <summary>
    /// How long ffmpeg is given to stop gracefully before it is killed.
    /// </summary>
    private const int GracefulStopTimeoutMs = 5000;

    /// <summary>
    /// How long the kill is given to take effect.
    /// </summary>
    private const int KillTimeoutMs = 5000;

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
            IsOriginal = true,
            Size = GetFileLength(path)
        };
        job.SetStatus(DownloadJobStatus.Ready);

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

        // Read the state before marking the job as cancelled: a finished conversion has a file that
        // may still be downloading, so it must be kept.
        var isReady = job.Status == DownloadJobStatus.Ready;

        // Mark the job as cancelled first: the conversion task re-reads this flag after ffmpeg was
        // started, which covers the race where the cancel arrives while the job is still queued.
        job.MarkCancelled();

        try
        {
            job.CancellationTokenSource?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while cancelling the token of download job {JobId}", jobId);
        }

        if (!job.IsOriginal && !isReady)
        {
            // Make sure ffmpeg is really gone before deleting its output, otherwise it would simply
            // keep writing to the (recreated) file and keep occupying a conversion slot.
            StopProcess(job);
            TryDeleteFile(job.OutputPath);
        }

        return true;
    }

    /// <inheritdoc />
    public bool Complete(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out var job))
        {
            // Already completed or expired, which is fine for an idempotent call.
            return false;
        }

        if (job.IsOriginal)
        {
            // The original job points at the media file, which must never be deleted.
            return true;
        }

        if (job.TranscodingJob is not null && !job.TranscodingJob.HasExited)
        {
            // The file is still being written, so it cannot be removed yet. Keep the job registered
            // and let the regular cleanup pick it up.
            _jobs.TryAdd(jobId, job);
            return false;
        }

        TryDeleteFile(job.OutputPath);
        _logger.LogDebug("Removed the converted file of download job {JobId}", jobId);
        return true;
    }

    /// <summary>
    /// Stops the ffmpeg process of a job, falling back to killing it when it does not exit.
    /// </summary>
    /// <param name="job">The job to stop.</param>
    private void StopProcess(DownloadJob job)
    {
        var transcodingJob = job.TranscodingJob;
        if (transcodingJob is null)
        {
            return;
        }

        var process = transcodingJob.Process;

        try
        {
            // Requests a graceful shutdown ("q" on stdin, then a kill after a timeout).
            transcodingJob.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping ffmpeg for download job {JobId}", job.Id);
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited && !process.WaitForExit(GracefulStopTimeoutMs))
            {
                _logger.LogWarning("FFmpeg did not stop, killing it for download job {JobId}", job.Id);
                process.Kill(true);
            }

            if (!process.HasExited)
            {
                process.WaitForExit(KillTimeoutMs);
            }

            if (!process.HasExited)
            {
                _logger.LogError("Unable to stop ffmpeg for download job {JobId}", job.Id);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process object can be disposed or inaccessible once it exited.
            _logger.LogDebug(ex, "Unable to inspect ffmpeg for download job {JobId}", job.Id);
        }
    }

    private async Task RunJobAsync(DownloadJob job, StreamState state, string commandLine, Guid userId)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        job.CancellationTokenSource = cancellationTokenSource;
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            job.SetStatus(DownloadJobStatus.Queued);
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.SetStatus(DownloadJobStatus.Cancelled);
            return;
        }

        try
        {
            job.SetStatus(DownloadJobStatus.Converting);

            var transcodingJob = await _transcodeManager.StartFfMpeg(
                state,
                job.OutputPath,
                commandLine,
                userId,
                TranscodingJobType.Progressive,
                cancellationTokenSource).ConfigureAwait(false);

            job.TranscodingJob = transcodingJob;

            // The cancel may have arrived while the process was starting, in which case it could not
            // stop anything yet, so stop the freshly started process here.
            if (job.IsCancelled)
            {
                StopProcess(job);
                TryDeleteFile(job.OutputPath);
                return;
            }

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

            if (job.IsCancelled || cancellationToken.IsCancellationRequested)
            {
                StopProcess(job);
                TryDeleteFile(job.OutputPath);
                return;
            }

            var length = GetFileLength(job.OutputPath);
            if (transcodingJob.ExitCode == 0 && length > 0)
            {
                job.Progress = 100;
                job.Size = length;
                job.SetStatus(DownloadJobStatus.Ready);
            }
            else
            {
                job.SetStatus(DownloadJobStatus.Failed);
                job.Error = string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}", transcodingJob.ExitCode);
                TryDeleteFile(job.OutputPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download conversion job {JobId} failed", job.Id);

            job.SetStatus(DownloadJobStatus.Failed);
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
        private readonly Lock _lock = new();

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

        public DownloadJobStatus Status { get; private set; } = DownloadJobStatus.Queued;

        public double? Progress { get; set; }

        public long? Size { get; set; }

        public string? Error { get; set; }

        public bool IsOriginal { get; set; }

        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public TranscodingJob? TranscodingJob { get; set; }

        public CancellationTokenSource? CancellationTokenSource { get; set; }

        /// <summary>
        /// Gets a value indicating whether the user cancelled this job.
        /// </summary>
        public bool IsCancelled
        {
            get
            {
                lock (_lock)
                {
                    return Status == DownloadJobStatus.Cancelled;
                }
            }
        }

        /// <summary>
        /// Marks the job as cancelled.
        /// </summary>
        public void MarkCancelled() => SetStatus(DownloadJobStatus.Cancelled);

        /// <summary>
        /// Updates the status without ever clearing a cancellation.
        /// </summary>
        /// <param name="status">The new status.</param>
        public void SetStatus(DownloadJobStatus status)
        {
            lock (_lock)
            {
                // A cancelled job must stay cancelled: completion of the process it raced with must
                // not turn it back into a ready or failed job.
                if (Status == DownloadJobStatus.Cancelled && status != DownloadJobStatus.Cancelled)
                {
                    return;
                }

                Status = status;
            }
        }

        public DownloadJobInfo ToInfo()
        {
            lock (_lock)
            {
                return new DownloadJobInfo
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
    }
}
