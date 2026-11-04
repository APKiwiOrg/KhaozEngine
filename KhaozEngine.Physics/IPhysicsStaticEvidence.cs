using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>An exact represented coordinate, not an interval midpoint.</summary>
public readonly record struct PhysicsEvidencePoint(double X, double Y, double Z);
public enum PhysicsEvidenceKind : byte { Triangle, SolidPolygon, Sphere, Capsule, Cylinder }
public enum PhysicsEvidenceStatus : byte { Unavailable, Complete, Unsupported, Unresolved, CapacityExceeded }

/// <summary>Callback-local installed address and actual geometry. Primitive poses use the physics frame.
/// Neither a local static handle nor this callback's spans are portable evidence identity.</summary>
public readonly record struct PhysicsEvidenceFace(StaticHandle Static, int Leaf, int Feature,
    PhysicsEvidenceKind Kind, Pose Pose, float Radius, float HalfLength);
public delegate bool PhysicsEvidenceVisitor(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices);

/// <summary>Optional complete static geometry capture under an authenticated read interval. Implementations retain
/// every relevant obstruction, independent of slope or navigation eligibility. A refusal never certifies a prefix.</summary>
public interface IPhysicsStaticEvidence
{
    PhysicsEvidenceStatus CaptureStaticEvidence(IPhysicsQueryLease lease, Vector3 minimum, Vector3 maximum,
        int maximumFeatures, PhysicsEvidenceVisitor visitor);
}
