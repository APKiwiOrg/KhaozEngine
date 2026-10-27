using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalGpuDevice
{
    internal const string RecordingCapacityMarker = "KE_METAL_RECORDING_CAPACITY_V1 ";
    internal string? RecordingCapacityProof { get; private set; }

    [SupportedOSPlatform("macos")]
    void ReportRecordingCapacity(MetalRecordingCapacityPolicy policy, MetalSwapchainHost? host)
    {
        using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

        int? actualDrawables = host?.Layer.MaximumDrawableCount();
        int retireCap = GpuRetireQueue.SealedBatchCapFor(this);
        policy.ValidateActual(_rings.FramesInFlight, actualDrawables, retireCap);
        var assembly = typeof(MetalGpuDevice).Assembly;
        RecordingCapacityProof = RecordingCapacityMarker + JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            probe = "metal-recording-capacity",
            baseFrames = policy.BaseFrames,
            selectedRecordingSlots = policy.RecordingSlots,
            actualRecordingSlots = _rings.FramesInFlight,
            requestedDrawableCount = policy.BaseFrames,
            actualDrawableCount = actualDrawables,
            drawableReadback = actualDrawables.HasValue ? "available" : "not-applicable",
            retirementPolicyCap = retireCap,
            uncommittedDiagnosticBound = _uncommitted.Bound,
            deviceIdentity = _rings.RingWaitDeviceIdentity,
            deviceName = Capabilities.DeviceName,
            assemblyName = assembly.GetName().Name,
            assemblyVersion = assembly.GetName().Version!.ToString(),
            assemblyMvid = assembly.ManifestModule.ModuleVersionId,
            assemblyInformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
        });
        log.Info(RecordingCapacityProof);
    }

    [SupportedOSPlatform("macos")]
    void AbortInitialization(MetalSwapchainHost? host)
    {
        if (_present is not null)
        {
            // CreateForHost still owns the input layer reference on failure. Replace the reference consumed
            // by boundary disposal so its existing catch can release exactly that input reference.
            host!.Value.Layer.Retain();
            _present.Dispose();
            _present = null;
        }
        _pointSampler.DestroyShared();
        _linearSampler.DestroyShared();
        _setup.Dispose();
    }
}
