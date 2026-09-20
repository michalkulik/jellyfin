#pragma warning disable CA1031 // Do not catch general exception types

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Encoder;

/// <summary>
/// Detects the hardware acceleration methods that are actually usable on this server.
/// </summary>
/// <remarks>
/// The list of hardware acceleration methods offered by the server used to be static, which made
/// it possible to select a method that could never work on the current machine. This class narrows
/// the list down by combining three sources of truth:
/// <list type="number">
/// <item>the devices the operating system exposes, for example the DRM render nodes in
/// <c>/dev/dri</c> or the NVIDIA device nodes;</item>
/// <item>the encoders and hwaccels the bundled FFmpeg was built with;</item>
/// <item>a real initialization of the hardware device through FFmpeg, so a device node that exists
/// but cannot be opened does not produce a usable option.</item>
/// </list>
/// The result is cached, because the detection starts FFmpeg processes.
/// </remarks>
public sealed class HardwareAccelerationDetector : IHardwareAccelerationDetector
{
    private const string DriDirectory = "/dev/dri";

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<HardwareAccelerationDetector> _logger;
    private readonly object _lock = new();

    private IReadOnlyList<HardwareAccelerationOption>? _cachedOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="HardwareAccelerationDetector"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{HardwareAccelerationDetector}"/> interface.</param>
    public HardwareAccelerationDetector(
        IMediaEncoder mediaEncoder,
        ILogger<HardwareAccelerationDetector> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<HardwareAccelerationOption> GetAvailableOptions()
    {
        lock (_lock)
        {
            return _cachedOptions ??= Detect();
        }
    }

    /// <summary>
    /// Maps a PCI vendor id to the hardware acceleration methods that vendor can provide.
    /// </summary>
    /// <param name="pciVendorId">The PCI vendor id, for example <c>8086</c> for Intel.</param>
    /// <returns>The hardware acceleration methods that may be available for the vendor.</returns>
    internal static HardwareAccelerationType[] GetAccelerationTypesForPciVendor(string? pciVendorId)
        => pciVendorId?.ToUpperInvariant() switch
        {
            // Intel
            "8086" => [HardwareAccelerationType.qsv, HardwareAccelerationType.vaapi],
            // AMD
            "1002" => [HardwareAccelerationType.vaapi],
            // NVIDIA
            "10DE" => [HardwareAccelerationType.nvenc],
            _ => []
        };

    /// <summary>
    /// Extracts a human readable driver name from the verbose FFmpeg output produced while
    /// initializing a hardware device.
    /// </summary>
    /// <param name="ffmpegOutput">The output of the FFmpeg hardware device initialization.</param>
    /// <returns>The driver name, or <c>null</c> when the output did not contain a known driver.</returns>
    internal static string? GetDriverNameFromFfmpegOutput(string? ffmpegOutput)
    {
        if (string.IsNullOrEmpty(ffmpegOutput))
        {
            return null;
        }

        if (ffmpegOutput.Contains("iHD_drv_video", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel iHD driver";
        }

        if (ffmpegOutput.Contains("i965_drv_video", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel i965 driver";
        }

        if (ffmpegOutput.Contains("radeonsi_drv_video", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD radeonsi driver";
        }

        if (ffmpegOutput.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA driver";
        }

        if (ffmpegOutput.Contains("mpp_service", StringComparison.OrdinalIgnoreCase)
            || ffmpegOutput.Contains("rockchip", StringComparison.OrdinalIgnoreCase))
        {
            return "Rockchip MPP driver";
        }

        return null;
    }

    private static string? ReadTextFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetUeventValue(string uevent, string key)
    {
        var prefix = key + "=";
        foreach (var line in uevent.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates the DRM render nodes and the PCI vendor id behind each of them.
    /// </summary>
    /// <returns>The detected render nodes.</returns>
    private static IEnumerable<(string Path, string? Driver, string? PciVendorId)> GetRenderNodes()
    {
        if (!Directory.Exists(DriDirectory))
        {
            yield break;
        }

        string[] nodes;
        try
        {
            nodes = Directory.GetFiles(DriDirectory, "renderD*");
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(nodes, StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var name = Path.GetFileName(node);
            var uevent = ReadTextFileOrNull($"/sys/class/drm/{name}/device/uevent");
            var pciId = GetUeventValue(uevent ?? string.Empty, "PCI_ID");
            var vendorId = pciId is { Length: >= 4 } ? pciId[..4] : null;
            yield return (node, GetUeventValue(uevent ?? string.Empty, "DRIVER"), vendorId);
        }
    }

    private static bool IsDeviceNodePresent(string path) => File.Exists(path);

    private IReadOnlyList<HardwareAccelerationOption> Detect()
    {
        var candidates = new List<(HardwareAccelerationType Type, string? Device)>();
        var deviceDescriptions = new Dictionary<HardwareAccelerationType, string?>();

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add((HardwareAccelerationType.videotoolbox, null));
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows exposes no device nodes, so the vendor runtime is the only hint available
            // before FFmpeg is asked to initialize the device.
            candidates.Add((HardwareAccelerationType.nvenc, null));
            candidates.Add((HardwareAccelerationType.qsv, null));
            candidates.Add((HardwareAccelerationType.amf, null));
            candidates.Add((HardwareAccelerationType.videotoolbox, null));
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var (path, driver, vendorId) in GetRenderNodes())
            {
                foreach (var type in GetAccelerationTypesForPciVendor(vendorId))
                {
                    if (candidates.Any(c => c.Type == type))
                    {
                        continue;
                    }

                    candidates.Add((type, path));
                    deviceDescriptions[type] = DescribeRenderNode(vendorId, driver);
                }
            }

            // NVIDIA does not always expose a DRM render node, but the driver always creates
            // /dev/nvidiactl when the kernel modules are loaded.
            if (!candidates.Any(c => c.Type == HardwareAccelerationType.nvenc)
                && (IsDeviceNodePresent("/dev/nvidiactl") || IsDeviceNodePresent("/dev/nvidia0")))
            {
                candidates.Add((HardwareAccelerationType.nvenc, null));
                deviceDescriptions[HardwareAccelerationType.nvenc] = "NVIDIA GPU";
            }

            if (IsDeviceNodePresent("/dev/mpp_service") || IsDeviceNodePresent("/dev/rga"))
            {
                candidates.Add((HardwareAccelerationType.rkmpp, null));
                deviceDescriptions[HardwareAccelerationType.rkmpp] = "Rockchip MPP";
            }

            // Raspberry Pi and other single board computers expose mem2mem video nodes.
            if (IsDeviceNodePresent("/dev/video-dec0") || IsDeviceNodePresent("/dev/video10"))
            {
                candidates.Add((HardwareAccelerationType.v4l2m2m, null));
                deviceDescriptions[HardwareAccelerationType.v4l2m2m] = "V4L2 mem2mem device";
            }
        }
        else
        {
            _logger.LogDebug("Hardware acceleration detection is not implemented for this platform");
        }

        var result = new List<HardwareAccelerationOption>();
        foreach (var (type, device) in candidates)
        {
            if (Validate(type, device, out var driverName, out var effectiveDevice))
            {
                result.Add(new HardwareAccelerationOption
                {
                    Type = type,
                    Device = effectiveDevice,
                    DeviceName = BuildDeviceName(driverName, deviceDescriptions.GetValueOrDefault(type), device)
                });
            }
        }

        _logger.LogInformation(
            "Detected hardware acceleration methods: {Methods}",
            result.Count == 0 ? "none" : string.Join(", ", result.Select(option => option.Type.ToString())));

        return result;
    }

    private static string? DescribeRenderNode(string? pciVendorId, string? driver)
    {
        var vendor = pciVendorId?.ToUpperInvariant() switch
        {
            "8086" => "Intel GPU",
            "1002" => "AMD GPU",
            "10DE" => "NVIDIA GPU",
            _ => null
        };

        if (vendor is null)
        {
            return driver;
        }

        return driver is null
            ? vendor
            : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", vendor, driver);
    }

    private static string? BuildDeviceName(string? driverName, string? deviceDescription, string? device)
    {
        var parts = new[] { driverName, deviceDescription }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (parts.Length == 0)
        {
            return device;
        }

        return string.Join(" — ", parts);
    }

    /// <summary>
    /// Checks whether the encoders exist and whether FFmpeg can really initialize the device.
    /// </summary>
    /// <param name="type">The hardware acceleration type to validate.</param>
    /// <param name="device">The device to use, when the method needs one.</param>
    /// <param name="driverName">The driver reported by FFmpeg, when available.</param>
    /// <param name="effectiveDevice">The device that should be stored in the configuration.</param>
    /// <returns><c>true</c> when the method can be used.</returns>
    private bool Validate(
        HardwareAccelerationType type,
        string? device,
        out string? driverName,
        out string? effectiveDevice)
    {
        driverName = null;
        effectiveDevice = device;

        var encoder = GetPrimaryEncoder(type);
        if (encoder is not null && !_mediaEncoder.SupportsEncoder(encoder))
        {
            _logger.LogDebug("Hardware acceleration {Type} is unavailable: {Encoder} is missing", type, encoder);
            return false;
        }

        if (type is HardwareAccelerationType.vaapi or HardwareAccelerationType.qsv
            && !_mediaEncoder.SupportsHwaccel(type.ToString()))
        {
            _logger.LogDebug("Hardware acceleration {Type} is unavailable: FFmpeg has no such hwaccel", type);
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            // Other platforms cannot be probed with a device path; the encoder check above is the
            // best that can be done without starting a full transcode.
            return true;
        }

        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            return false;
        }

        var initArguments = type switch
        {
            HardwareAccelerationType.vaapi => "vaapi=va:" + device,
            HardwareAccelerationType.qsv => "qsv=hw:" + device,
            HardwareAccelerationType.nvenc => "cuda=cu:0",
            HardwareAccelerationType.rkmpp => "drm=dr:" + device + " -init_hw_device rkmpp=rk@dr",
            HardwareAccelerationType.v4l2m2m => null,
            _ => null
        };

        if (initArguments is null)
        {
            return true;
        }

        try
        {
            var validator = new EncoderValidator(_logger, encoderPath);
            if (!validator.CheckHwDeviceInitialization(initArguments, out var output))
            {
                _logger.LogDebug("Hardware acceleration {Type} is unavailable: the device cannot be initialized", type);
                return false;
            }

            driverName = GetDriverNameFromFfmpegOutput(output);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while validating hardware acceleration {Type}", type);
            return false;
        }
    }

    private static string? GetPrimaryEncoder(HardwareAccelerationType type)
        => type switch
        {
            HardwareAccelerationType.amf => "h264_amf",
            HardwareAccelerationType.qsv => "h264_qsv",
            HardwareAccelerationType.nvenc => "h264_nvenc",
            HardwareAccelerationType.v4l2m2m => "h264_v4l2m2m",
            HardwareAccelerationType.vaapi => "h264_vaapi",
            HardwareAccelerationType.videotoolbox => "h264_videotoolbox",
            HardwareAccelerationType.rkmpp => "h264_rkmpp",
            _ => null
        };
}
