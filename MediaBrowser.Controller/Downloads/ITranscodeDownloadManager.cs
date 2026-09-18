using System;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Downloads;

namespace MediaBrowser.Controller.Downloads
{
    /// <summary>
    /// Manages server side "convert then download" jobs.
    /// </summary>
    public interface ITranscodeDownloadManager
    {
        /// <summary>
        /// Enqueues a new conversion job and starts the background transcode.
        /// </summary>
        /// <param name="jobId">The opaque job id, also used as the output file discriminator.</param>
        /// <param name="state">The prepared stream state describing the requested conversion.</param>
        /// <param name="commandLine">The ffmpeg command line produced for the state.</param>
        /// <param name="itemId">The id of the item being downloaded.</param>
        /// <param name="userId">The id of the requesting user.</param>
        /// <param name="fileName">The suggested download file name.</param>
        /// <returns>The created job.</returns>
        DownloadJobInfo Enqueue(
            string jobId,
            StreamState state,
            string commandLine,
            Guid itemId,
            Guid userId,
            string fileName);

        /// <summary>
        /// Registers an already available file (e.g. the original) as a finished download job.
        /// </summary>
        /// <param name="jobId">The opaque job id.</param>
        /// <param name="itemId">The id of the item being downloaded.</param>
        /// <param name="path">The path of the file to serve.</param>
        /// <param name="fileName">The suggested download file name.</param>
        /// <returns>The created job.</returns>
        DownloadJobInfo EnqueueOriginal(string jobId, Guid itemId, string path, string fileName);

        /// <summary>
        /// Gets the current state of a job.
        /// </summary>
        /// <param name="jobId">The job id.</param>
        /// <returns>The job, or <c>null</c> when it does not exist.</returns>
        DownloadJobInfo? GetJob(string jobId);

        /// <summary>
        /// Gets the converted file path of a finished job.
        /// </summary>
        /// <param name="jobId">The job id.</param>
        /// <param name="path">The converted file path.</param>
        /// <param name="fileName">The suggested download file name.</param>
        /// <returns><c>true</c> when the job finished successfully and the file is available.</returns>
        bool TryGetReadyFile(string jobId, out string? path, out string? fileName);

        /// <summary>
        /// Cancels a job and deletes its partial output.
        /// </summary>
        /// <param name="jobId">The job id.</param>
        /// <returns><c>true</c> when the job existed.</returns>
        bool Cancel(string jobId);
    }
}
