using System;
using System.Collections.Generic;
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
}

public sealed record SkinnedAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh) : AttachmentPartDefinition(Id, Mesh)
{
    /// <summary>
    /// The part's triangles tagged by region name, as runs in triangle order. Required in a set with a layer and empty
    /// in a set without one. A set owns a merged copy, so adjacent runs of one region become one run.
    /// </summary>
    public IReadOnlyList<NamedRegionRun> Regions { get; init; } = [];

    /// <summary>Equal when the base members match and both parts list the same runs, compared run by run.</summary>
    public bool Equals(SkinnedAttachmentPartDefinition? other) =>
        other is not null && base.Equals(other) && (Regions ?? []).SequenceEqual(other.Regions ?? []);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (NamedRegionRun run in Regions ?? [])
            hash.Add(run);
        return hash.ToHashCode();
    }
}
