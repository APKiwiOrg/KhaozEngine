using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

internal static class CoverageGeometryTestRun
{
    static readonly ConditionalWeakTable<CoverageScratch, CoverageReferenceScratch> references = new();

    internal static CoverageReferenceScratch Reference(CoverageScratch scratch) =>
        references.GetValue(scratch, s => new(s.Topology, s.CommandCapacity));

    internal static bool TryEvaluate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posed, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, Span<ModelVertex> output, out CoverageFailure failure) =>
        TryEvaluate(topology, posed, model, commands, scratch, output, out _, out failure);

    internal static bool TryEvaluate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posed, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, Span<ModelVertex> output,
        out CoverageGeometryCertificate certificate, out CoverageFailure failure)
    {
        if (!ReferenceEquals(topology, scratch.Topology) || posed.Length != topology.SourceVertexCount
            || output.Length != topology.VertexCount || posed.Overlaps(output)) throw new ArgumentException("Invalid offline geometry buffers.");
        certificate = default;
        var reference = Reference(scratch);
        reference.Arithmetic.Reset();
        // Admission and final certification remain exact. Normals publish after the geometry predicate under test.
        if (!CoverageFrames.TryPrepare(topology, model, commands, scratch, CoverageCertification.Magnitude(posed, default), out failure)
            || !CoverageReferenceFrames.TryPrepare(topology, model, commands, reference, out failure)
            || !CoverageReferenceConstructor.TryInterpolate(topology, posed, reference, out failure)
            || !CoverageReferenceConstructor.TryConformGroups(topology, commands, reference, out failure)
            || !CoverageReferenceConstructor.TryCorrectAndTransition(topology, commands, reference, out failure)
            || !CoverageCertification.ValidateGeometry(topology, reference.Before, reference.After, model, commands, scratch,
                out certificate, out failure)
            || !CoverageReferenceNormals.TryUpdate(topology, reference, out failure))
        {
            certificate = default;
            return false;
        }
        reference.Published.AsSpan().CopyTo(output);
        return true;
    }

    internal static bool UpdateNormals(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, Span<ModelVertex> after,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        if (!ReferenceEquals(topology, scratch.ReferenceTopology.Topology) || before.Length != topology.VertexCount
            || after.Length != topology.VertexCount) throw new ArgumentException("Invalid reference normal buffers.");
        before.CopyTo(scratch.Before);
        after.CopyTo(scratch.After);
        if (!CoverageReferenceNormals.TryUpdate(topology, scratch, out failure)) return false;
        scratch.Published.AsSpan().CopyTo(after);
        return true;
    }
}
