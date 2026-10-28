using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowInputValueTests
{
    [Fact]
    public void EveryMatrixWordAndLightWordSurvivesScratchReuse()
    {
        float negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        var words = new float[64];
        for (int i = 0; i < words.Length; i++) words[i] = i == 0 ? negativeZero : BitConverter.UInt32BitsToSingle(0x3f800000u + (uint)i);
        var matrices = MemoryMarshal.Cast<float, Matrix4x4>(words).ToArray();
        var view = new FrameView(matrices[0], matrices[1], matrices[2], matrices[3], new(negativeZero, 1, 2), 40, 32, 17, default);
        var builder = new ShadowInputObservationBuilder();
        var request = new ShadowStageProbeRequest(Guid.NewGuid(), 1, ShadowStageProbeMode.Counts);
        builder.Reset(request); builder.BeginPrefix(17, 3);
        builder.ObserveView(view);
        builder.ObserveCamera(new(negativeZero, 1, 2), new(3, 4, 5));
        builder.ObserveLight(new(negativeZero, 1, 2), new(3, 4, 5), new(6, 7, 8));
        float[] splits = [negativeZero, 2, 3, 4];
        builder.ObserveFits(4, 64, splits, matrices);
        var saved = builder.Finish();
        Array.Clear(matrices); Array.Clear(splits);
        builder.Reset(request with { Sequence = 2 });
        var result = new[] { saved.View.View, saved.View.Projection, saved.View.ViewProjection, saved.View.AbsoluteViewProjection };
        Assert.Equal(MemoryMarshal.AsBytes(words.AsSpan()).ToArray(), MemoryMarshal.AsBytes(result.AsSpan()).ToArray());
        var fits = new[] { saved.Fits.Fit0, saved.Fits.Fit1, saved.Fits.Fit2, saved.Fits.Fit3 };
        Assert.Equal(MemoryMarshal.AsBytes(words.AsSpan()).ToArray(), MemoryMarshal.AsBytes(fits.AsSpan()).ToArray());
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(saved.Fits.Splits.X));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(saved.Light.Raw.X));
        Assert.Equal(new Vector3(3, 4, 5), saved.Light.Normalized);
        Assert.Equal(new Vector3(6, 7, 8), saved.Light.Selected);
    }

    [Fact]
    public void DuplicateFitAndFinishNeverEraseMultiplicity()
    {
        var builder = new ShadowInputObservationBuilder();
        builder.Reset(new(Guid.NewGuid(), 1, ShadowStageProbeMode.Counts));
        builder.BeginPrefix(1, 0);
        builder.ObserveFits(1, 64, [2], [Matrix4x4.Identity]);
        var first = builder.Finish();
        builder.ObserveFits(1, 64, [3], [default]);
        Assert.Equal(2, builder.Finish().CascadeObservationCount);
        Assert.Equal(first.Fits, builder.Finish().Fits);
        Assert.False(builder.Finish().Complete);
    }
}
