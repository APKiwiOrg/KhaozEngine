using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class ShadowInputObservationAllocationTests
{
    [Theory]
    [InlineData(ShadowStageProbeMode.Counts)]
    [InlineData(ShadowStageProbeMode.Timings)]
    public void SamplingActualInputValuesAllocatesZeroAfterWarmup(ShadowStageProbeMode mode)
    {
        var builder = new ShadowInputObservationBuilder();
        var epoch = Guid.NewGuid();
        long sequence = 0;
        var view = new FrameView(Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
            Matrix4x4.Identity, new(128, 0, 256), 1591, 1027, 7, new(.25f, -.25f));
        float[] splits = [10, 20, 40, 80];
        Matrix4x4[] fits = [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];
        var decision = new ShadowPassDiagnostics(true, true, false, false, false, false, false,
            false, false, 0, 4, [], 0, 0);
        void Sample()
        {
            builder.Reset(new(epoch, ++sequence, mode));
            builder.BeginPrefix(7, 1.5f);
            builder.ObserveView(view);
            builder.ObserveCamera(Vector3.One, Vector3.UnitZ);
            builder.ObserveLight(-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY);
            builder.ObserveFits(4, 64, splits, fits);
            builder.ObserveDecision(decision);
            _ = builder.Finish();
        }
        for (int i = 0; i < 32; i++) Sample();
        AllocationMeasurement.NoPerCallAllocation("actual input value sampling", Sample);
        Assert.True(builder.Finish().Complete);
        Assert.Equal(4, builder.Finish().Fits.Count);
    }
}
