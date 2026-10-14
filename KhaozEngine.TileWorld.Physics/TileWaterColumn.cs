using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

internal readonly record struct TileWaterColumn(MovementDomainKey Domain,
    Vector3 Minimum, Vector3 Maximum, string MinXBoundaryId, string MaxXBoundaryId,
    string MinZBoundaryId, string MaxZBoundaryId, string BedBoundaryId, string SurfaceBoundaryId)
{
    internal MovementWaterInterval Interval => new(Minimum.Y, Maximum.Y, Maximum.Y, true, BedBoundaryId, SurfaceBoundaryId);
}
