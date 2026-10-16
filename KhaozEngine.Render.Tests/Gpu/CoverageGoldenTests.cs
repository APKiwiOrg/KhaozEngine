using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Property proof only. Backend-owned reference grids require the reviewed correctness packet first.
public sealed class CoverageGoldenTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuFact]
    public void Opaque_appearance_packet_has_native_protected_skin_and_a_cleared_core()
    {
        var row = new CoverageCase(0);
        CoveragePixelProof.AssertSurface(scene.Capture(row, false, true), scene.Capture(row, true, true),
            scene.CaptureReference(row, true), requireBreakthrough: true);
    }
}
