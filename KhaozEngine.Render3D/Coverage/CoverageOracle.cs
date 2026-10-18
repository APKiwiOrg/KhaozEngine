using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Reusable headless reference construction followed by exact geometry certification at the approved
/// clearance with zero device tolerance. Owns no GPU resources. Single-caller and not thread safe.</summary>
/// <remarks>Construction retains the quarter-clearance reserve. Signed-zero flushing follows the reference kernel
/// contract and diagnostic flush events never refuse a sample. A certificate is sampled-pose evidence only.</remarks>
public sealed class CoverageOracle
{
    readonly Matrix4x4[] inverseBind, composed;
    readonly ModelVertex[] posed, final;
    readonly CoverageReferenceSkin skin;
    readonly CoverageReferenceScratch reference;
    readonly CoverageScratch certification;
    int constructedLength;

    /// <summary>Copies one through 128 finite affine inverse binds and allocates bounded actor-owned workspaces.
    /// Bone indices must fit that palette, and command capacity must be one through CoverageScratch.MaxCommands.</summary>
    public CoverageOracle(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> inverseBind,
        int commandCapacity = CoverageScratch.MaxCommands)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (inverseBind.Length is < 1 or > SkinningMath.MaxBonesPerDraw)
            throw new ArgumentOutOfRangeException(nameof(inverseBind));
        if (commandCapacity is < 1 or > CoverageScratch.MaxCommands)
            throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        foreach (ref readonly var matrix in inverseBind)
            if (!CoverageAffine.TryFrom(matrix, out _))
                throw new ArgumentException("Oracle inverse binds must be finite affine matrices.", nameof(inverseBind));
        if (topology.Source.SkinVertices.Length != topology.SourceVertexCount)
            throw new ArgumentException("The oracle requires a skinned host source.", nameof(topology));
        Topology = topology;
        this.inverseBind = inverseBind.ToArray();
        skin = new(topology.Source, inverseBind.Length);
        composed = new Matrix4x4[inverseBind.Length];
        posed = new ModelVertex[topology.SourceVertexCount];
        final = new ModelVertex[topology.VertexCount];
        reference = new(topology, commandCapacity);
        certification = new(topology, commandCapacity);
    }

    /// <summary>The immutable topology used to bind every submitted command.</summary>
    public CoverageRefinement Topology { get; }

    /// <summary>Checks the exact supplied pose with pinned reference skinning, production construction and the
    /// unchanged exact geometry certifier, in that order. Warm valid and refusal paths allocate no managed memory.</summary>
    /// <remarks>The palette must match the copied inverse-bind count or an ArgumentException is thrown. Empty or
    /// oversized command sets refuse at construction. A refusal clears the certificate and preserves its guard code,
    /// host, equipment, command, count and limit. No Scene3D, render capture, fallback or drawable token is created.</remarks>
    public bool TryCheck(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, out CoverageOracleCertificate certificate, out CoverageOracleFailure failure)
    {
        certificate = default;
        failure = default;
        constructedLength = 0;
        reference.Arithmetic.Reset();
        reference.UniformArithmetic.Reset();
        if (palette.Length != inverseBind.Length)
            throw new ArgumentException("Oracle palette must match the inverse-bind count.", nameof(palette));
        if (commands.Length == 0 || commands.Length > reference.CommandCapacity)
        {
            failure = new(CoverageOracleStage.Construction, new(CoverageFailureCode.CapacityExceeded,
                Topology.HostIdentity, 0, 0, commands.Length, reference.CommandCapacity));
            return false;
        }
        for (int i = 0; i < palette.Length; i++)
            composed[i] = CoverageReferenceSkinning.Compose(palette[i], inverseBind[i]);
        if (!CoverageReferenceSkinning.TrySkin(reference.Arithmetic, skin, Topology.Source, composed, posed, out var code))
        {
            failure = new(CoverageOracleStage.Construction, new(code, Topology.HostIdentity, 0, 0, 0, 0));
            return false;
        }
        if (!CoverageReferenceConstructor.TryConstruct(Topology, posed, model, commands, reference, final, out var refused))
        {
            failure = new(CoverageOracleStage.Construction, refused);
            return false;
        }
        constructedLength = final.Length;
        CertificationRunsForTests++;
        if (!CoverageCertification.ValidateGeometry(Topology, reference.Before, final, model, commands, certification,
            out var geometry, out refused))
        {
            failure = new(CoverageOracleStage.Certification, refused);
            return false;
        }
        certificate = new(geometry.FullCoreClearance, geometry.MinimumCoreClearance, geometry.DisplacedVertices);
        return true;
    }

    /// <summary>Test observation only. Invalidated by the next check and empty after any construction refusal.</summary>
    internal ReadOnlySpan<ModelVertex> LastConstructedVertices => final.AsSpan(0, constructedLength);
    /// <summary>Counts exact validation calls without callbacks in the numerical path.</summary>
    internal int CertificationRunsForTests { get; private set; }
}
