using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>One immutable authored foliage placement with its stable density-thinning rank.</summary>
public readonly record struct FoliageInstance(
    MeshHandle Mesh,
    Matrix4x4 Transform,
    float ThinningRank,
    FoliageMotionProfile Motion = default,
    bool CastsShadows = false)
{
    /// <summary>Deconstructs the placement without the optional motion and shadow settings.</summary>
    public void Deconstruct(out MeshHandle Mesh, out Matrix4x4 Transform, out float ThinningRank)
    {
        Mesh = this.Mesh;
        Transform = this.Transform;
        ThinningRank = this.ThinningRank;
    }
}
