namespace KhaozEngine.Render3D;

/// <summary>The reason a MeshIndex input was refused.</summary>
public enum MeshIndexRefusal
{
    /// <summary>Refuses a source index outside the primitive vertex domain.</summary>
    SourceIndexOutsideVertexCount,

    /// <summary>Refuses an index whose rebase cannot fit an unsigned index.</summary>
    RebasedIndexOverflow,
}
