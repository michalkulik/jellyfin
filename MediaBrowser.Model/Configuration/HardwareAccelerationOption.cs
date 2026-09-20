using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// Describes a hardware acceleration method that is actually usable on this server, together with
/// the device that would be used for it.
/// </summary>
public class HardwareAccelerationOption
{
    /// <summary>
    /// Gets or sets the hardware acceleration type.
    /// </summary>
    public HardwareAccelerationType Type { get; set; }

    /// <summary>
    /// Gets or sets the device that will be used for the acceleration, for example
    /// <c>/dev/dri/renderD128</c>. May be <c>null</c> when the method does not need a device,
    /// like VideoToolbox on macOS.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Gets or sets a human readable description of the hardware that was detected, for example
    /// <c>Intel iHD driver for Intel(R) Gen Graphics</c>. May be <c>null</c> when it could not be
    /// determined.
    /// </summary>
    public string? DeviceName { get; set; }
}
