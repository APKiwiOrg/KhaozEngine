using System.Runtime.CompilerServices;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointShadowAttributionValueStorageTests(ITestOutputHelper log)
{
    [Fact]
    public void Every_recorded_bridge_and_sample_value_is_reference_free()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageProbeRequest>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageProbeInputs>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageProbeSnapshot>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageCallbackResult>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageProbeRow>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PointAttributionFrame>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<FrameDiagnosticsSample>());
        log.WriteLine($"Request={Unsafe.SizeOf<ShadowStageProbeRequest>()}, inputs={Unsafe.SizeOf<ShadowStageProbeInputs>()}, snapshot={Unsafe.SizeOf<ShadowStageProbeSnapshot>()}, sample={Unsafe.SizeOf<FrameDiagnosticsSample>()}, sample buffer={(long)Unsafe.SizeOf<FrameDiagnosticsSample>() * 4096} bytes.");
    }
}
