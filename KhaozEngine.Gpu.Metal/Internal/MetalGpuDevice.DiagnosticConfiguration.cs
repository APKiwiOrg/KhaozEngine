using System;
using System.Globalization;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalGpuDevice : IGpuDeviceDiagnosticConfiguration
{
    GpuEffectiveDiagnosticConfiguration? _diagnosticConfiguration;
    public GpuEffectiveDiagnosticConfiguration DiagnosticConfiguration => _diagnosticConfiguration
        ?? throw new InvalidOperationException("This device did not use explicit frozen diagnostic settings.");
    [SupportedOSPlatform("macos")]
    static GpuEffectiveDiagnosticConfiguration EffectiveConfiguration(MTLDevice device, MetalHeadlessDiagnosticSettings settings, string? cache)
    {
        string key = "registry:" + device.RegistryId().ToString("x16", CultureInfo.InvariantCulture);
        if (settings.AdapterKey is { } pinned && pinned != key) throw new InvalidOperationException("The Metal registry ID differs from the frozen adapter.");
        return new(GpuBackendKind.MetalNative, key, "Metal driver version unavailable, OS=" + Environment.OSVersion.VersionString,
            settings.Diagnostics, settings.Diagnostics.CompilerMetadata, false,
            device.ClassName().Contains("Debug", StringComparison.Ordinal), settings.Validation.ShaderValidationArmed, false, false, 0,
            [new("frontend", SpirvFrontEndPin.Identity), new("msl.compile", MslCompilePin.Identity),
             new("msl.cross", MslCrossCompilePin.Identity), new("toolchain", SpirvToolchainVersion.Identity)],
            [new("cache.msl", cache ?? "disabled"), new("cache.spirv.memo", settings.CompileCache.ToString()),
             new("drain", "shared-event"), new("frames-in-flight", settings.FramesInFlight.ToString(CultureInfo.InvariantCulture)),
             new("recording", "metal-native")]);
    }
}
