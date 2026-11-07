using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal static class RigidPointShadowAgreement
{
    internal enum Failure { None, Backend, Image, Alpha, Capture, Coverage, Depth, Receiver }
    internal sealed record Request(int Width, int Height, int LightCount, int FaceResolution, int SplitSequence, int FullSequence);
    internal sealed record Sample(int OutputX, int OutputY, int X, int Y, uint NdcDepthBits, int[] Normal);
    internal sealed record Receiver(int Sequence, int OutputWidth, int OutputHeight, int Width, int Height,
        int NormalWidth, int NormalHeight, Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 CorrectedViewProjection,
        Vector3 Origin, bool ClipYInverted, bool DepthZeroToOne, bool DirectBilinear, Sample[] Samples);
    internal sealed record Capture(RigidPointShadowEvidence.Atlas Base, RigidPointShadowEvidence.Atlas? Transient,
        RigidPointShadowEvidence.Atlas Full, int[] TransientRows, Receiver? SplitReceiver, Receiver? FullReceiver);
    internal sealed record Result(bool Accepted, bool Exact, Failure Rejection, RigidPointShadowEvidence.PixelReport Image,
        RigidPointShadowEvidence.DepthReport? Depth, bool CompleteCoverage, bool ReceiverAgreement);

    internal static Result Evaluate(GpuBackendKind backend, byte[] split, byte[] full, Request request, Capture? capture)
    {
        var image = RigidPointShadowEvidence.ComparePixels(split, full, request.Width, request.Height);
        if (image.ChangedChannels == 0) return new(true, true, Failure.None, image, null, false, false);
        if (backend != GpuBackendKind.VulkanNative) return Reject(Failure.Backend);
        for (int i = 3; i < split.Length; i += 4)
            if (split[i] != full[i]) return Reject(Failure.Alpha);
        if (image.ChangedPixels > 1 || image.MaximumDelta > 2) return Reject(Failure.Image);
        if (capture is null) return Reject(Failure.Capture);
        if (!Coverage(capture, request)) return Reject(Failure.Coverage);
        RigidPointShadowEvidence.DepthReport depth;
        try { depth = RigidPointShadowEvidence.CompareDepth(capture.Base, capture.Transient, capture.Full); }
        catch (ArgumentException) { return Reject(Failure.Coverage); }
        int expected = checked(request.LightCount * 6 * request.FaceResolution * request.FaceResolution);
        if (expected <= 0 || depth.ComparedTexels != expected) return Reject(Failure.Coverage);
        bool receivers = Receivers(capture.SplitReceiver, capture.FullReceiver, request, image);
        if (!receivers) return new(false, false, Failure.Receiver, image, depth, true, false);
        bool bounded = depth.ClearCoveredDisagreements == 0 && depth.BaseNonfinite == 0 && depth.TransientNonfinite == 0
            && depth.CombinedNonfinite == 0 && depth.FullNonfinite == 0
            && depth.MaximumFiniteAbsoluteDelta <= 1e-5 && depth.MaximumFiniteUlpDelta <= 320;
        return new(bounded, false, bounded ? Failure.None : Failure.Depth, image, depth, true, true);

        Result Reject(Failure reason) => new(false, false, reason, image, null, false, false);
    }

    static bool Coverage(Capture capture, Request request)
    {
        if (request.LightCount <= 0 || request.FaceResolution <= 0
            || !AssignedBase(capture.Base) || !AssignedBase(capture.Full)
            || capture.Base.Width != capture.Full.Width || capture.Base.Height != capture.Full.Height
            || !capture.Base.Rows.SequenceEqual(capture.Full.Rows)
            || capture.TransientRows.Length != request.LightCount || capture.TransientRows.Any(row => row < -1)) return false;
        if (capture.Transient is not { } transient) return capture.TransientRows.All(row => row < 0);
        return transient.FaceResolution == request.FaceResolution && transient.Height > 0
            && transient.Height <= capture.Base.Height && transient.Rows.SequenceEqual(capture.TransientRows)
            && transient.Rows.Where(row => row >= 0).Distinct().Count() == transient.Rows.Count(row => row >= 0);

        bool AssignedBase(RigidPointShadowEvidence.Atlas atlas) => atlas.FaceResolution == request.FaceResolution
            && atlas.Height > 0 && atlas.Rows.Length == request.LightCount && atlas.Rows.All(row => row >= 0)
            && atlas.Rows.Distinct().Count() == request.LightCount;
    }

    static bool Receivers(Receiver? split, Receiver? full, Request request, RigidPointShadowEvidence.PixelReport image)
    {
        if (split is null || full is null || !Valid(split, request.SplitSequence) || !Valid(full, request.FullSequence)
            || split.Width != full.Width || split.Height != full.Height
            || !Same(split.View, full.View) || !Same(split.Projection, full.Projection)
            || !Same(split.CorrectedViewProjection, full.CorrectedViewProjection) || !Same(split.Origin, full.Origin)) return false;
        var expected = new HashSet<(int Ox, int Oy, int X, int Y)>();
        foreach (var pixel in image.FirstPixels)
            foreach (var site in RigidPointShadowEvidence.ReceiverFootprint(pixel.X, pixel.Y, request.Width, request.Height,
                split.Width, split.Height, flipV: false))
                if (site.NominalWeight > 0) expected.Add((pixel.X, pixel.Y, site.X, site.Y));
        if (image.FirstPixels.Length != image.ChangedPixels || expected.Count == 0
            || split.Samples.Length != expected.Count || full.Samples.Length != expected.Count) return false;
        foreach (var key in expected)
        {
            Sample[] a = split.Samples.Where(sample => Key(sample) == key).ToArray();
            Sample[] b = full.Samples.Where(sample => Key(sample) == key).ToArray();
            if (a.Length != 1 || b.Length != 1 || a[0].NdcDepthBits != b[0].NdcDepthBits
                || !Normal(a[0].Normal) || !Normal(b[0].Normal) || !a[0].Normal.SequenceEqual(b[0].Normal)) return false;
            float ndc = BitConverter.Int32BitsToSingle(unchecked((int)a[0].NdcDepthBits));
            if (!float.IsFinite(ndc) || ndc < 0 || ndc > 1) return false;
        }
        return true;

        bool Valid(Receiver input, int sequence) => sequence > 0 && input.Sequence == sequence
            && input.OutputWidth == request.Width && input.OutputHeight == request.Height
            && input.Width > 0 && input.Height > 0 && input.NormalWidth == input.Width && input.NormalHeight == input.Height
            && input.DirectBilinear && !input.ClipYInverted && input.DepthZeroToOne && input.Samples is not null
            && Matrix(input.View) && Matrix(input.Projection) && Matrix(input.CorrectedViewProjection)
            && float.IsFinite(input.Origin.X) && float.IsFinite(input.Origin.Y) && float.IsFinite(input.Origin.Z);
        static bool Normal(int[] normal) => normal is { Length: 4 } && normal.All(value => value is >= 0 and <= 255);
        static (int, int, int, int) Key(Sample sample) => (sample.OutputX, sample.OutputY, sample.X, sample.Y);
    }

    // Matrix and origin equality is bit equality, including signed zero. Receiver depth words are also exact.
    static bool Same<T>(T a, T b) where T : unmanaged =>
        MemoryMarshal.AsBytes(new[] { a }.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(new[] { b }.AsSpan()));

    static bool Matrix(Matrix4x4 matrix)
    {
        float determinant = matrix.GetDeterminant();
        if (!float.IsFinite(determinant) || determinant == 0) return false;
        foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(new[] { matrix }.AsSpan()))
            if (!float.IsFinite(value)) return false;
        return true;
    }
}
