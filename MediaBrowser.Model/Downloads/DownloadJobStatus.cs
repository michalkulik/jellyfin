namespace MediaBrowser.Model.Downloads
{
    /// <summary>
    /// The status of a download job.
    /// </summary>
    public enum DownloadJobStatus
    {
        /// <summary>
        /// The job is queued and waiting to start.
        /// </summary>
        Queued = 0,

        /// <summary>
        /// The server is converting the item.
        /// </summary>
        Converting = 1,

        /// <summary>
        /// The converted file is ready to be downloaded.
        /// </summary>
        Ready = 2,

        /// <summary>
        /// The job failed.
        /// </summary>
        Failed = 3,

        /// <summary>
        /// The job was cancelled.
        /// </summary>
        Cancelled = 4
    }
}
