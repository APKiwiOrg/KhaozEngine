using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// A linked chain's second pass uses the same authored lengths, radius and tier
// obstacles. These indices are separate from the scratch used by each joint.
internal sealed class ChainFirstPassContacts(int colliderCount)
{
    private Vector3 _root;
    private float _radius;
    private int _obstacles = -1;
    private int _count;

    internal ChainCapsuleCull Cull { get; } = new(colliderCount);
    internal float? BendCosine { get; set; }

    internal void Begin()
    {
        _obstacles = -1;
        BendCosine = null;
    }

    internal ChainCapsuleCandidates Select(ChainParticles particles, int chain, float radius,
        ReadOnlySpan<ColliderCapsule> colliders)
    {
        _root = particles.Positions[particles.Rig.FirstJoint(chain) + chain];
        _radius = radius;
        _obstacles = colliders.Length;
        var result = ChainConstraints.SelectCapsules(Cull, particles, chain, radius, colliders);
        _count = result.Count;
        return result;
    }

    internal bool TryReuse(ChainParticles particles, int chain, float radius, int obstacles, out int count)
    {
        count = _count;
        return _obstacles == obstacles && _radius == radius &&
            _root == particles.Positions[particles.Rig.FirstJoint(chain) + chain];
    }
}
