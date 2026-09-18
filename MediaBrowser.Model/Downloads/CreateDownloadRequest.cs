using System;

namespace MediaBrowser.Model.Downloads
{
    /// <summary>
    /// Request to create a server side download (convert then download) job.
    /// </summary>
    public class CreateDownloadRequest
    {
        /// <summary>
        /// Gets or sets the media source id. When omitted the primary media source is used.
        /// </summary>
        public string? MediaSourceId { get; set; }

        /// <summary>
        /// Gets or sets the maximum video bitrate in bits per second. When omitted or 0 the original file is used.
        /// </summary>
        public int? MaxBitrate { get; set; }

        /// <summary>
        /// Gets or sets the maximum video height. When omitted the source resolution is kept.
        /// </summary>
        public int? MaxHeight { get; set; }

        /// <summary>
        /// Gets or sets the output container. Defaults to the requested quality default.
        /// </summary>
        public string? Container { get; set; }

        /// <summary>
        /// Gets or sets the audio stream index to include.
        /// </summary>
        public int? AudioStreamIndex { get; set; }

        /// <summary>
        /// Gets or sets the video stream index to include.
        /// </summary>
        public int? VideoStreamIndex { get; set; }

        /// <summary>
        /// Gets or sets the device id used to distinguish the output file.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the original file should be served without conversion.
        /// </summary>
        public bool Original { get; set; }
    }
}
