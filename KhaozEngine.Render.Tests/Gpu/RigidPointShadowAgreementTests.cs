using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class RigidPointShadowAgreementTests
{
    static readonly byte[] Split = [9, 12, 11, 255], Full = [8, 10, 9, 255];
    static readonly RigidPointShadowAgreement.Request Request = new(1, 1, 1, 1, 7, 9);

    [Theory]
    [InlineData(GpuBackendKind.MetalNative)]
    [InlineData(GpuBackendKind.Direct3D11Native)]
    [InlineData(GpuBackendKind.VulkanNative)]
    public void ExactImagesNeedNoReadbackOnAnyNativeBackend(GpuBackendKind backend)
    {
        var result = RigidPointShadowAgreement.Evaluate(backend, Split, Split, Request, null);
        Assert.True(result.Accepted);
        Assert.True(result.Exact);
        Assert.Null(result.Depth);
    }

    [Fact]
    public void MeasuredVulkanDepthPrecisionNeedsCompleteCurrentReceiverAgreement()
    {
        var result = Check(Valid());
        Assert.True(result.Accepted);
        Assert.False(result.Exact);
        Assert.True(result.CompleteCoverage);
        Assert.True(result.ReceiverAgreement);
        Assert.Equal(1, result.Image.ChangedPixels);
        Assert.Equal(3, result.Image.ChangedChannels);
        Assert.Equal(2, result.Image.MaximumDelta);
        Assert.Equal(6, result.Depth!.ComparedTexels);
        Assert.Equal(251UL, result.Depth.MaximumFiniteUlpDelta);
    }

    [Fact]
    public void AnUnassignedTransientUsesBaseOnlyAndDepthSignedZeroKeepsItsUlp()
    {
        var capture = Valid() with { Transient = null, TransientRows = [-1] };
        Array.Fill(capture.Base.Values, 0.5f);
        Array.Fill(capture.Full.Values, 0.5f);
        capture.Base.Values[0] = -0f;
        capture.Full.Values[0] = 0f;
        var result = Check(capture);
        Assert.True(result.Accepted);
        Assert.Equal(1, result.Depth!.MismatchedTexels);
        Assert.Equal(0d, result.Depth.MaximumFiniteAbsoluteDelta);
        Assert.Equal(1UL, result.Depth.MaximumFiniteUlpDelta);
    }

    [Theory]
    [InlineData(GpuBackendKind.MetalNative)]
    [InlineData(GpuBackendKind.Direct3D11Native)]
    public void OtherBackendsKeepExactImages(GpuBackendKind backend) =>
        Assert.Equal(RigidPointShadowAgreement.Failure.Backend,
            RigidPointShadowAgreement.Evaluate(backend, Split, Full, Request, Valid()).Rejection);

    [Fact]
    public void AlphaAndThreeByteColorDriftAreRejected()
    {
        Assert.Equal(RigidPointShadowAgreement.Failure.Alpha, Check(Valid(), split: [9, 12, 11, 254]).Rejection);
        Assert.Equal(RigidPointShadowAgreement.Failure.Image, Check(Valid(), split: [11, 12, 11, 255]).Rejection);
    }

    [Fact]
    public void ASecondChangedPixelFailsWithOtherwiseCompleteInputs()
    {
        var capture = Valid();
        var sites = new[] { Site(0, 0, 0, 0), Site(0, 0, 0, 1), Site(1, 0, 1, 0), Site(1, 0, 1, 1) };
        capture = capture with
        {
            SplitReceiver = capture.SplitReceiver! with { OutputWidth = 2, Samples = sites },
            FullReceiver = capture.FullReceiver! with { OutputWidth = 2, Samples = sites },
        };
        var result = RigidPointShadowAgreement.Evaluate(GpuBackendKind.VulkanNative,
            [9, 12, 11, 255, 9, 12, 11, 255], [8, 10, 9, 255, 8, 10, 9, 255], Request with { Width = 2 }, capture);
        Assert.Equal(RigidPointShadowAgreement.Failure.Image, result.Rejection);
    }

    [Theory]
    [InlineData(0x3f66672e, 0x3f666666)]
    [InlineData(0x3dccce0e, 0x3dcccccd)]
    public void AbsoluteAndUlpLimitsAreIndependent(int candidateWord, int fullWord)
    {
        var capture = Valid();
        capture.Transient!.Values[0] = BitConverter.Int32BitsToSingle(candidateWord);
        capture.Full.Values[0] = BitConverter.Int32BitsToSingle(fullWord);
        var result = Check(capture);
        Assert.Equal(RigidPointShadowAgreement.Failure.Depth, result.Rejection);
        Assert.True(result.CompleteCoverage);
        Assert.True(result.ReceiverAgreement);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("transient")]
    [InlineData("full")]
    public void AnyNonfiniteSourceFailsEvenWhenTheNearerLayerIsFinite(string source)
    {
        var capture = Valid();
        capture.Base.Values[0] = capture.Transient!.Values[0] = capture.Full.Values[0] = 0.5f;
        if (source == "base") capture.Base.Values[0] = float.PositiveInfinity;
        else if (source == "transient") capture.Transient.Values[0] = float.PositiveInfinity;
        else capture.Full.Values[0] = float.NaN;
        Assert.Equal(RigidPointShadowAgreement.Failure.Depth, Check(capture).Rejection);
    }

    [Theory]
    [InlineData("missing-capture")]
    [InlineData("missing-light")]
    [InlineData("missing-texel")]
    [InlineData("missing-transient")]
    [InlineData("zero-lights")]
    [InlineData("aliased-lights")]
    public void MissingOrEmptyCoverageCannotEstablishAgreement(string fault)
    {
        var capture = Valid();
        var request = Request;
        if (fault == "missing-capture") { Assert.False(Check(null).Accepted); return; }
        if (fault == "missing-light") request = request with { LightCount = 2 };
        if (fault == "missing-texel") capture = capture with { Base = capture.Base with { Values = [1f, 1f, 1f, 1f, 1f] } };
        if (fault == "missing-transient")
        {
            capture = capture with { Transient = null };
            Array.Fill(capture.Base.Values, 0.5f);
            Array.Fill(capture.Full.Values, 0.5f);
        }
        if (fault == "zero-lights")
        {
            request = request with { LightCount = 0 };
            capture = capture with
            {
                Base = capture.Base with { Rows = [] },
                Full = capture.Full with { Rows = [] },
                Transient = null,
                TransientRows = [],
            };
        }
        if (fault == "aliased-lights")
        {
            request = request with { LightCount = 2 };
            capture = capture with
            {
                Base = Atlas([0, 0], Enumerable.Repeat(0.5f, 12).ToArray()),
                Full = Atlas([0, 0], Enumerable.Repeat(0.5f, 12).ToArray()),
                Transient = null,
                TransientRows = [-1, -1],
            };
        }
        Assert.False(RigidPointShadowAgreement.Evaluate(GpuBackendKind.VulkanNative, Split, Full, request, capture).Accepted);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("size")]
    [InlineData("normal-size")]
    [InlineData("depth")]
    [InlineData("normal")]
    [InlineData("mapping")]
    [InlineData("camera")]
    [InlineData("clip")]
    [InlineData("depth-range")]
    [InlineData("normal-missing")]
    [InlineData("receiver-nonfinite")]
    [InlineData("receiver-signed-zero")]
    public void EveryActualReceiverSiteAndInputMustAgree(string fault)
    {
        var capture = Valid();
        var receiver = capture.FullReceiver!;
        if (fault == "missing") capture = capture with { FullReceiver = null };
        else
        {
            receiver = fault switch
            {
                "partial" => receiver with { Samples = receiver.Samples[..3] },
                "duplicate" => receiver with { Samples = [receiver.Samples[0], receiver.Samples[0], receiver.Samples[2], receiver.Samples[3]] },
                "stale" => receiver with { Sequence = 8 },
                "size" => receiver with { Width = 3 },
                "normal-size" => receiver with { NormalWidth = 3 },
                "mapping" => receiver with { DirectBilinear = false },
                "camera" => receiver with { Projection = Matrix4x4.CreateScale(2f) },
                "clip" => receiver with { ClipYInverted = true },
                "depth-range" => receiver with { DepthZeroToOne = false },
                _ => receiver,
            };
            if (fault == "depth") receiver.Samples[0] = receiver.Samples[0] with { NdcDepthBits = 0x3e800001 };
            if (fault == "normal") receiver.Samples[0] = receiver.Samples[0] with { Normal = [129, 255, 128, 255] };
            if (fault == "normal-missing") receiver.Samples[0] = receiver.Samples[0] with { Normal = [] };
            if (fault == "receiver-nonfinite") receiver.Samples[0] = receiver.Samples[0] with { NdcDepthBits = 0x7f800000 };
            if (fault == "receiver-signed-zero")
            {
                capture.SplitReceiver!.Samples[0] = capture.SplitReceiver.Samples[0] with { NdcDepthBits = 0 };
                receiver.Samples[0] = receiver.Samples[0] with { NdcDepthBits = 0x80000000 };
            }
            capture = capture with { FullReceiver = receiver };
        }
        Assert.Equal(RigidPointShadowAgreement.Failure.Receiver, Check(capture).Rejection);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("previous-pose")]
    [InlineData("ghost")]
    public void OmittedPreviousAndGhostDepthFailWithTinyColorDifferences(string fault)
    {
        var capture = Valid() with { Transient = null, TransientRows = [-1] };
        Array.Fill(capture.Base.Values, 0.5f);
        Array.Fill(capture.Full.Values, 0.5f);
        capture.Base.Values[0] = fault == "omitted" ? 1f : 0.25f;
        if (fault == "ghost") capture.Full.Values[0] = 1f;
        Assert.Equal(RigidPointShadowAgreement.Failure.Depth, Check(capture).Rejection);
    }

    [Fact]
    public void WeakCOnlyOmissionFailsTheSameGuardWithAnAllowedColorDelta()
    {
        var capture = Valid() with
        {
            Base = Atlas([0, 1, 2], Enumerable.Repeat(0.5f, 18).ToArray()),
            Full = Atlas([0, 1, 2], Enumerable.Repeat(0.5f, 18).ToArray()),
            Transient = null,
            TransientRows = [-1, -1, -1],
        };
        // Captured C +Z witness. The first two lights stay identical, only C loses current actor depth.
        capture.Base.Values[16] = 1f;
        capture.Full.Values[16] = BitConverter.Int32BitsToSingle(1062188297);
        var result = RigidPointShadowAgreement.Evaluate(GpuBackendKind.VulkanNative, Split, Full,
            Request with { LightCount = 3 }, capture);
        Assert.Equal(RigidPointShadowAgreement.Failure.Depth, result.Rejection);
        Assert.True(result.CompleteCoverage);
        Assert.True(result.ReceiverAgreement);
        Assert.Equal(1, result.Image.ChangedPixels);
        Assert.Equal(2, result.Image.MaximumDelta);
        Assert.Equal(1, result.Depth!.ClearCoveredDisagreements);
        Assert.Equal(0.18864387273788452, result.Depth.MaximumFiniteAbsoluteDelta);
    }

    static RigidPointShadowAgreement.Result Check(RigidPointShadowAgreement.Capture? capture, byte[]? split = null) =>
        RigidPointShadowAgreement.Evaluate(GpuBackendKind.VulkanNative, split ?? Split, Full, Request, capture);

    static RigidPointShadowEvidence.Atlas Atlas(int[] rows, float[] values) => new(6, values.Length / 6, 1, rows, values);
    static RigidPointShadowAgreement.Sample Site(int ox, int oy, int x, int y) => new(ox, oy, x, y, 0x3e800000, [128, 255, 128, 255]);
    static RigidPointShadowAgreement.Receiver Receiver(int sequence) => new(sequence, 1, 1, 2, 2, 2, 2,
        Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, false, true, true,
        [Site(0, 0, 0, 0), Site(0, 0, 1, 0), Site(0, 0, 0, 1), Site(0, 0, 1, 1)]);
    static RigidPointShadowAgreement.Capture Valid() => new(
        Atlas([0], [1f, 1f, 1f, 1f, 1f, 1f]),
        Atlas([0], [BitConverter.Int32BitsToSingle(1052693882), 0.5f, 0.5f, 0.5f, 0.5f, 0.5f]),
        Atlas([0], [BitConverter.Int32BitsToSingle(1052693631), 0.5f, 0.5f, 0.5f, 0.5f, 0.5f]), [0], Receiver(7), Receiver(9));
}
