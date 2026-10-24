using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalGpuDevice : IGpuResourceMemoryDiagnostics
{
    internal MetalMemoryDiagnostics Memory { get; }

    internal MetalCounterMetadata ReadCounterMetadata()
    {
        lock (_lifecycle)
        {
            if (_disposed || _liveness.IsDead) return new([], [], GpuDiagnosticStatus.Invalid, "The Metal device was disposed or lost.");
            if (_diagnosticConfiguration is { CompilerMetadataEnabled: false })
                return new([], [], GpuDiagnosticStatus.Unsupported, "Cold counter metadata was not enabled on this explicit instance.");
            return TimestampApi?.ReadCounterMetadata() ?? new([], [], GpuDiagnosticStatus.Unsupported, "No timestamp capability API was created.");
        }
    }

    public GpuResourceMemorySnapshot ReadMemorySnapshot()
    {
        lock (_lifecycle)
        {
            bool invalid = _disposed || _liveness.IsDead;
            if (invalid || !KhaozEngineMetal.IsPlatformSupported) return Memory.Snapshot(invalid);
            return Memory.Snapshot(false, ReadNativeMemory);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    MetalNativeMemorySnapshot ReadNativeMemory() => MTLDiagnosticInterop.ReadMemory(_device.Handle);
}
