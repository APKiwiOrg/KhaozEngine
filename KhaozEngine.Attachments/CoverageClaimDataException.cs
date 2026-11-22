namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of the geometry or claim given to <see cref="CoverageClaimCheck.Verify"/>.</summary>
public sealed class CoverageClaimDataException : MalformedDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public CoverageClaimDataException(CoverageClaimRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public CoverageClaimRefusal Reason { get; }

    /// <summary>The input list holding the refused mesh.</summary>
    public CoverageClaimMeshKind? MeshKind { get; init; }

    /// <summary>The zero-based index of the refused mesh in its list. A body mesh's index is its region map part.</summary>
    public int? MeshIndex { get; init; }

    /// <summary>The region map's part count.</summary>
    public int? ExpectedMeshCount { get; init; }

    /// <summary>The number of body meshes given.</summary>
    public int? ActualMeshCount { get; init; }

    /// <summary>The region map part's triangle count.</summary>
    public int? ExpectedTriangleCount { get; init; }

    /// <summary>The body mesh's triangle count.</summary>
    public int? ActualTriangleCount { get; init; }

    /// <summary>The mesh's index count that is not a multiple of three.</summary>
    public int? IndexCount { get; init; }

    /// <summary>The zero-based position of the refused index in the mesh's index list.</summary>
    public int? IndexPosition { get; init; }

    /// <summary>The out of range index, or the index of the position that is not finite.</summary>
    public uint? VertexIndex { get; init; }

    /// <summary>The number of positions in the mesh an index ran past.</summary>
    public int? VertexCount { get; init; }

    /// <summary>The covered region name the body does not declare.</summary>
    public string? RegionName { get; init; }

    /// <summary>The definition ID of the piece part whose mesh lacks swing evidence.</summary>
    public uint? PartId { get; init; }

    /// <summary>The asset key of the piece part whose mesh lacks swing evidence.</summary>
    public string? MeshKey { get; init; }
}
