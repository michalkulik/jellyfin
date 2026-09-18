using System;

namespace MediaBrowser.Model.Downloads
{
    /// <summary>
    /// Information about a server side download (convert then download) job.
    /// </summary>
    public class DownloadJobInfo
    {
        /// <summary>
        /// Gets or sets the opaque job id.
        /// </summary>
        public required string Id { get; set; }

        /// <summary>
        /// Gets or sets the item id the job belongs to.
        /// </summary>
        public required Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the current job status.
        /// </summary>
        public DownloadJobStatus Status { get; set; }

        /// <summary>
        /// Gets or sets the conversion progress in percent (0-100), when known.
        /// </summary>
        public double? Progress { get; set; }

        /// <summary>
        /// Gets or sets the suggested file name of the converted file.
        /// </summary>
        public string? FileName { get; set; }

        /// <summary>
        /// Gets or sets the size of the converted file in bytes, when ready.
        /// </summary>
        public long? Size { get; set; }

        /// <summary>
        /// Gets or sets the error message when the job failed.
        /// </summary>
        public string? Error { get; set; }
    }
}
