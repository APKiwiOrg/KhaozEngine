using KhaozEngine.MapDoc.Identity;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A water read result. Every status other than <see cref="Dry"/> and <see cref="Wet"/> is a failure that
/// carries its membership or narrowing reason, never a dry answer.</summary>
public enum MapWaterStatus { Dry, Wet, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }

/// <summary>One water read against an occupied space. A wet sample holds the body's wet interval
/// [<see cref="LowerY"/>, <see cref="UpperY"/>) in the space, its stored level and whether the top is the free
/// surface, whose boundary id is <c>water:{bodyId}</c>. A closed top carries the space's upper boundary id. A dry
/// sample names the occupied space, when one resolved, and leaves the body fields null and the heights NaN. Every
/// sample carries the acquisition's read witness and the water digest the query captured.</summary>
public sealed record MapWaterSample(MapWaterStatus Status, MapFramePoint Point, string? BodyId, string? SpaceId, string? Medium,
    float LowerY, float UpperY, float NominalSurfaceY, bool UpperIsFreeSurface, string? LowerBoundaryId, string? UpperBoundaryId,
    string? Detail, MapReadWitness Witness, string WaterDigest);
