using System.Collections.Generic;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Detects the hardware acceleration methods that can actually be used on this server.
/// </summary>
public interface IHardwareAccelerationDetector
{
    /// <summary>
    /// Gets the hardware acceleration methods that are usable on this server, including the device
    /// that would be used for each of them.
    /// </summary>
    /// <remarks>
    /// Software encoding (<see cref="MediaBrowser.Model.Entities.HardwareAccelerationType.none"/>)
    /// is always possible and is therefore not part of the result. The detection only reports a
    /// method when both the required encoders are present in FFmpeg and the hardware device can be
    /// initialized, so the result can be used to hide options that would never work.
    /// </remarks>
    /// <returns>The detected hardware acceleration methods.</returns>
    IReadOnlyList<HardwareAccelerationOption> GetAvailableOptions();
}
