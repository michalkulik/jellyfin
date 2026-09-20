using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Encoder;

public class HardwareAccelerationDetectorTests
{
    [Theory]
    [InlineData("8086", new[] { HardwareAccelerationType.qsv, HardwareAccelerationType.vaapi })]
    [InlineData("1002", new[] { HardwareAccelerationType.vaapi })]
    [InlineData("10de", new[] { HardwareAccelerationType.nvenc })]
    [InlineData("10DE", new[] { HardwareAccelerationType.nvenc })]
    public void GetAccelerationTypesForPciVendor_KnownVendor_ReturnsExpectedTypes(string vendorId, HardwareAccelerationType[] expected)
    {
        Assert.Equal(expected, HardwareAccelerationDetector.GetAccelerationTypesForPciVendor(vendorId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("ffff")]
    public void GetAccelerationTypesForPciVendor_UnknownVendor_ReturnsEmpty(string? vendorId)
    {
        Assert.Empty(HardwareAccelerationDetector.GetAccelerationTypesForPciVendor(vendorId));
    }

    [Theory]
    [InlineData("libva info: Trying to open /usr/lib/jellyfin-ffmpeg/lib/dri/iHD_drv_video.so", "Intel iHD driver")]
    [InlineData("libva info: Trying to open /usr/lib/x86_64-linux-gnu/dri/i965_drv_video.so", "Intel i965 driver")]
    [InlineData("Trying to open /usr/lib/dri/radeonsi_drv_video.so", "AMD radeonsi driver")]
    [InlineData("Load nvidia driver", "NVIDIA driver")]
    [InlineData("using /dev/mpp_service", "Rockchip MPP driver")]
    public void GetDriverNameFromFfmpegOutput_KnownDriver_ReturnsName(string output, string expected)
    {
        Assert.Equal(expected, HardwareAccelerationDetector.GetDriverNameFromFfmpegOutput(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("libva info: VA-API version 1.24.0")]
    public void GetDriverNameFromFfmpegOutput_UnknownDriver_ReturnsNull(string? output)
    {
        Assert.Null(HardwareAccelerationDetector.GetDriverNameFromFfmpegOutput(output));
    }
}
