namespace KhaozEngine.Render3D;

/// <summary>The stable reasons a skinned mesh view is refused, or a source with live views is kept.</summary>
public enum SkinnedMeshViewRefusal
{
    /// <summary>The source handle is default or stale.</summary>
    SourceNotLive,

    /// <summary>The source is itself a view. Views are one level deep.</summary>
    SourceIsView,

    /// <summary>The source carries host coverage evidence. A view is never a coverage host.</summary>
    SourceHasCoverage,

    /// <summary>The view names no triangles. A fully hidden mesh is skipped by its caller instead.</summary>
    IndicesEmpty,

    /// <summary>The index count is not a multiple of three. <see cref="SkinnedMeshViewArgumentException.IndexCount"/>
    /// names it.</summary>
    IndexCountNotTriangles,

    /// <summary>An index reaches past the source's vertices. <see cref="SkinnedMeshViewArgumentException.IndexPosition"/>
    /// and <see cref="SkinnedMeshViewArgumentException.VertexCount"/> name it.</summary>
    IndexOutOfRange,

    /// <summary>The source was asked to unload while a view of it lives.
    /// <see cref="SkinnedMeshViewArgumentException.LiveViews"/> names the count.</summary>
    SourceHasLiveViews,

    /// <summary>The view must supply one rest position per source vertex.
    /// <see cref="SkinnedMeshViewArgumentException.VertexCount"/> and
    /// <see cref="SkinnedMeshViewArgumentException.PositionCount"/> name the expected and supplied counts.</summary>
    PositionCountMismatch,

    /// <summary>A rest position has a non-finite component.
    /// <see cref="SkinnedMeshViewArgumentException.VertexIndex"/> names the vertex.</summary>
    PositionNonFinite,
}
