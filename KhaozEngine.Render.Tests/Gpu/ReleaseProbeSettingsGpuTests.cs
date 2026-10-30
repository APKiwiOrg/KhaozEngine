using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class ReleaseProbeSettingsGpuTests
{
    [GpuTheory]
    [InlineData(null, "6", 2, 2)]
    [InlineData("6", "bogus", 3, 6)]
    public void FrozenDiagnosticFramesWinOverTheAmbientBase(string? slots, string ambient, int frozen, int expected)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        string? originalBase = Environment.GetEnvironmentVariable(MetalFramesInFlight.EnvVarName);
        string? originalSlots = Environment.GetEnvironmentVariable(MetalRecordingCapacityPolicy.EnvVarName);
        try
        {
            var settings = MetalHeadlessDiagnosticSettings.Resolve(new(false, false),
                name => name == MetalFramesInFlight.EnvVarName ? frozen.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                MetalValidationReader.Current());
            Environment.SetEnvironmentVariable(MetalFramesInFlight.EnvVarName, ambient);
            Environment.SetEnvironmentVariable(MetalRecordingCapacityPolicy.EnvVarName, slots);
            using var device = (MetalGpuDevice)MetalGpuDevice.CreateHeadless(settings).Device;
            Assert.Equal(expected, device.Rings.FramesInFlight);
            Assert.Contains(device.DiagnosticConfiguration.ObservationOptions, option =>
                option.Key == "frames-in-flight" && option.Value == frozen.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetalFramesInFlight.EnvVarName, originalBase);
            Environment.SetEnvironmentVariable(MetalRecordingCapacityPolicy.EnvVarName, originalSlots);
        }
    }
}
