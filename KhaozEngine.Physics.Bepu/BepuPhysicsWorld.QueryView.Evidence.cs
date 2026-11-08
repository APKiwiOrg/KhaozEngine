using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    private sealed partial class QueryView : IPhysicsStaticEvidence
    {
        public PhysicsEvidenceStatus CaptureStaticEvidence(IPhysicsQueryLease lease, Vector3 minimum,
            Vector3 maximum, int maximumFeatures, PhysicsEvidenceVisitor visitor)
        {
            _owner.AuthenticateFeatureLease(lease);
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(visitor);
            var status = _owner.CaptureStaticEvidence(lease, minimum, maximum, maximumFeatures, Visit);
            ThrowIfDisposed();
            lease.AssertCurrent();
            return status;

            bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices) =>
                _exclusions.ExcludesSweepStatic(face.Static.Value) || visitor(face, vertices);
        }
    }
}
