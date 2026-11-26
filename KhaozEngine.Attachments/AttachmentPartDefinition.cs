using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

public readonly record struct AttachmentMeshRef(string Key, string Sha256);

/// <summary>A run of <see cref="TriangleCount"/> consecutive triangles of a skinned part tagged with the region named <see cref="Region"/>.</summary>
public readonly record struct NamedRegionRun(string Region, int TriangleCount);

public abstract record AttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh);

public sealed record RigidAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh, string Joint, Matrix4x4 Local)
    : AttachmentPartDefinition(Id, Mesh)
{
    /// <summary>The body region the part sits on. Required in a set with a layer and absent from a set without one.</summary>
    public string? Region { get; init; }

    /// <summary>Optional detailed cloth support and the matching cheap capsules.</summary>
    public ImmutableArray<ClothHullGroup> ClothHulls { get; init; } = [];
}

public sealed record SkinnedAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh) : AttachmentPartDefinition(Id, Mesh)
{
    /// <summary>
    /// The part's triangles tagged by region name, as runs in triangle order. Required in a set with a layer and empty
    /// in a set without one. A set owns a merged copy, so adjacent runs of one region become one run.
    /// </summary>
    public IReadOnlyList<NamedRegionRun> Regions { get; init; } = [];

    /// <summary>The part's authored motion, or null when it only follows the body.</summary>
    public PartMotionDefinition? Motion { get; init; }

    /// <summary>The part's declared lift, or null when it keeps its authored rest positions.</summary>
    public PartLiftDefinition? Lift { get; init; }

    /// <summary>Equal when the base members, region runs, authored motion and lift match.</summary>
    public bool Equals(SkinnedAttachmentPartDefinition? other) =>
        other is not null && base.Equals(other) && (Regions ?? []).SequenceEqual(other.Regions ?? [])
        && Equals(Motion, other.Motion) && Equals(Lift, other.Lift);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (NamedRegionRun run in Regions ?? [])
            hash.Add(run);
        if (Motion is not null) hash.Add(Motion);
        if (Lift is not null) hash.Add(Lift);
        return hash.ToHashCode();
    }
}
