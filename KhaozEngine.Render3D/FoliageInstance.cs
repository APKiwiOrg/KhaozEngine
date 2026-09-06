using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>One immutable authored foliage placement with its stable density-thinning rank.</summary>
public readonly record struct FoliageInstance(MeshHandle Mesh, Matrix4x4 Transform, float ThinningRank);
