#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Drawing
{
    /// <summary>
    /// Reduces the file size of images that were fetched from external sources.
    /// </summary>
    public static class ImageSizeReducer
    {
        /// <summary>
        /// The default maximum size of a single cached image.
        /// </summary>
        public const long DefaultMaxSizeBytes = 200 * 1024;

        /// <summary>
        /// Maximum dimensions and encoder quality tried, in order, while shrinking an image.
        /// </summary>
        private static readonly (int MaxWidth, int MaxHeight, int Quality)[] _steps =
        [
            (500, 750, 90),
            (400, 600, 85),
            (320, 480, 80),
            (240, 360, 75)
        ];

        /// <summary>
        /// Re-encodes the image at <paramref name="path"/> until it fits within <paramref name="maxSizeBytes"/>.
        /// </summary>
        /// <param name="imageProcessor">The image processor used to re-encode the image.</param>
        /// <param name="item">The item the image belongs to.</param>
        /// <param name="path">The path of the stored image.</param>
        /// <param name="maxSizeBytes">The maximum allowed file size.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the image was replaced with a smaller version.</returns>
        public static async Task<bool> ReduceAsync(
            IImageProcessor imageProcessor,
            BaseItem item,
            string path,
            long maxSizeBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(imageProcessor);
            ArgumentNullException.ThrowIfNull(item);

            var originalSize = GetFileLength(path);
            if (originalSize <= 0 || originalSize <= maxSizeBytes)
            {
                return false;
            }

            // Keep the original format so the file extension keeps matching its contents.
            var outputFormat = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? ImageFormat.Png
                : ImageFormat.Jpg;

            var image = new ItemImageInfo
            {
                Path = path,
                Type = ImageType.Primary,
                DateModified = File.GetLastWriteTimeUtc(path)
            };

            var reduced = false;

            foreach (var (maxWidth, maxHeight, quality) in _steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var options = new ImageProcessingOptions
                {
                    Item = item,
                    ItemId = item.Id,
                    Image = image,
                    MaxWidth = maxWidth,
                    MaxHeight = maxHeight,
                    Quality = quality,
                    RequiresAutoOrientation = false,
                    SupportedOutputFormats = [outputFormat]
                };

                string processedPath;
                try
                {
                    (processedPath, _, _) = await imageProcessor.ProcessImage(options).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The image processor falls back to the original image on failure; nothing more to do.
                    return reduced;
                }

                // The processor returns the original path when it cannot encode the image.
                if (string.Equals(processedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return reduced;
                }

                var processedSize = GetFileLength(processedPath);

                // Only replace the stored image when the re-encode is actually smaller.
                if (processedSize <= 0 || processedSize >= GetFileLength(path))
                {
                    continue;
                }

                File.Copy(processedPath, path, true);
                reduced = true;

                if (GetFileLength(path) <= maxSizeBytes)
                {
                    return reduced;
                }
            }

            return reduced;
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
    }
}
