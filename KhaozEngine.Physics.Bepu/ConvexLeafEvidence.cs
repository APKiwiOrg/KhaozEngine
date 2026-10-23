using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

internal enum ConvexShellFeature { Apex, LowerCone, Shoulder, Cylinder, HeadDisc, Rim, Interior }
internal enum ConvexTargetFeature { Face, Edge, Vertex, Interior }

// Bounds enclose the exact attained mathematical witnesses. The Vector3 fields are their
// encodings, whose errors and the complete distance bracket share the existing spatial budget.
// Normal-cone predicates use exact expressions, never these rounded representatives.
internal readonly record struct ConvexLeafEvidence(bool IsComplete, bool HasAttainedWitness,
    bool HasNormalConeCertificate, float LowerDistance, float UpperDistance, Vector3 QueryWitness,
    Vector3 TargetWitness, Vector3 Normal, ConvexShellFeature QueryFeature, ConvexTargetFeature TargetFeature,
    int LocalFeatureId, GeometryVector QueryWitnessBounds, GeometryVector TargetWitnessBounds, GeometryVector TowardTargetBounds);
