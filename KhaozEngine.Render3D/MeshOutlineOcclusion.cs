namespace KhaozEngine.Render3D;

/// <summary>What may hide a target outline group's border.</summary>
public enum MeshOutlineOcclusion
{
    /// <summary>
    /// Opaque depth-writing scene geometry hides the covered part of the target and the border beside it, and the
    /// border never paints over nearer geometry.
    /// </summary>
    SceneDepth = 0,

    /// <summary>
    /// Nothing hides the border. It follows the target's whole projected silhouette over every surface in front of
    /// or behind it, as a screen-space marker does.
    /// </summary>
    None = 1,
}
