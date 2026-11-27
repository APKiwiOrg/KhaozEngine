using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// Attach the residual to an accepted material pose, independently of the proxy's own transition.
internal readonly record struct ClothDetailResidual(Vector3 Position, Vector3 Normal, Vector3 Tangent)
{
    internal ClothDetailResidual(in ModelVertex shown, in ModelVertex material)
        : this(shown.Position - material.Position, shown.Normal - material.Normal, Direction(shown) - Direction(material)) { }

    internal static ClothDetailResidual[][] Allocate(BoundClothSurface surface)
    {
        if (!surface.HasMaterialMapping) return [];
        var result = new ClothDetailResidual[surface.Materials.Length][];
        for (int material = 0; material < result.Length; material++)
            result[material] = new ClothDetailResidual[surface.Materials[material].Vertices.Length];
        return result;
    }

    internal ClothDetailResidual InBasis(in Matrix4x4 transform) =>
        transform == Matrix4x4.Identity ? this : new(Vector3.TransformNormal(Position, transform),
            Vector3.TransformNormal(Normal, transform), Vector3.TransformNormal(Tangent, transform));

    internal static ClothDetailResidual Lerp(in ClothDetailResidual from, in ClothDetailResidual to, float amount) =>
        new(Vector3.Lerp(from.Position, to.Position, amount), Vector3.Lerp(from.Normal, to.Normal, amount),
            Vector3.Lerp(from.Tangent, to.Tangent, amount));

    internal ModelVertex Apply(ModelVertex material)
    {
        material.Position += Position;
        if (Normal != Vector3.Zero)
            material.Normal = ClothDetailedReconstruction.Unit(material.Normal + Normal, material.Normal);
        if (Tangent != Vector3.Zero)
        {
            Vector3 direction = Direction(material);
            material.Tangent = new(ClothDetailedReconstruction.Unit(direction + Tangent, direction), material.Tangent.W);
        }
        return material;
    }

    private static Vector3 Direction(in ModelVertex vertex) => new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
}
