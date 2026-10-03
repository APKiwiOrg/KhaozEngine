namespace KhaozEngine.Movement;

/// <summary>One sampled medium water surface of a captured column: the cell index in canonical Z/X order, the
/// absolute water surface Y and the area bits the classifier returned for the surface point.</summary>
internal readonly record struct PhysicsNavWater(int Cell, float SurfaceY, uint Areas);
