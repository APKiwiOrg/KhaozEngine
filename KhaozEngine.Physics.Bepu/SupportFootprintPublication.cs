using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Publishes attained-point enclosures without discarding the independent upward certificate.</summary>
internal static class SupportFootprintPublication
{
    internal static bool TryWitness(SupportWitnessRole role, GeometryVector position, GeometryVector normal,
        out SupportFootprintWitness witness)
    {
        witness = default;
        GeometryVectorOutput p = GeometryVectorOperations.Publish(position);
        GeometryVectorOutput n = GeometryVectorOperations.Publish(normal);
        if (!p.IsResolved || !n.IsResolved) return false;
        witness = new(role, p.Value, p.Error, n.Value, n.Error, normal.Y.Lower);
        return true;
    }
}
