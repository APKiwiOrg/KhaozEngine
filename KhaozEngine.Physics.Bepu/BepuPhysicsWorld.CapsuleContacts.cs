using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsCapsuleContacts
{
    // Work exhaustion is an incomplete query, never a usable prefix. This backend-policy limit is
    // separate from the caller's output capacity and must travel with a bake's backend/policy identity.
    const int MaximumContactCandidates = 4096;
    const float MaximumContactErrorMetres = 0.001f;
    readonly List<CollidableReference> _contactCandidates = new();
    readonly CapsuleContactCollector _contactCollector = new();

    /// <inheritdoc/>
    public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float maxSeparationMetres,
        Span<CapsuleContact> destination, QueryFilter filter = default) =>
        QueryCapsuleContactsCore(capsule, pose, maxSeparationMetres, destination, filter, null);

    unsafe CapsuleContactResult QueryCapsuleContactsCore(CapsuleShape capsule, Pose pose,
        float maxSeparationMetres, Span<CapsuleContact> destination, QueryFilter filter,
        StaticQueryExclusions? exclusions)
    {
        using QueryOperation scope = EnterQuery();
        ArgumentNullException.ThrowIfNull(capsule);
        if (!float.IsFinite(capsule.Radius) || capsule.Radius <= 0f ||
            !float.IsFinite(capsule.Length) || capsule.Length < 0f)
            throw new ArgumentOutOfRangeException(nameof(capsule));
        if (!Finite(pose.Position) || !Finite(pose.Orientation) ||
            MathF.Abs(pose.Orientation.LengthSquared() - 1f) > 0.0001f)
            throw new ArgumentException("A capsule query needs a finite, unit rigid pose.", nameof(pose));
        if (!float.IsFinite(maxSeparationMetres) || maxSeparationMetres < 0f)
            throw new ArgumentOutOfRangeException(nameof(maxSeparationMetres));
        if (filter.Layers != 0)
            throw new NotSupportedException("Bepu does not assign body layers, so nonzero query layer masks are unsupported.");
        if (filter.Mobility is not (QueryMobility.All or QueryMobility.Statics or QueryMobility.Dynamics))
            throw new ArgumentOutOfRangeException(nameof(filter));

        _contactCollector.Reset();
        _contactCandidates.Clear();
        var queryShape = new Capsule(capsule.Radius, capsule.Length);
        queryShape.ComputeBounds(pose.Orientation, out Vector3 min, out Vector3 max);
        // Broad phase must include uncertain boundary contacts too. Use the full accepted error budget
        // here, then derive the narrower per-query budget from every selected pair before narrow phase.
        Vector3 expansion = new(maxSeparationMetres + MaximumContactErrorMetres);
        min += pose.Position - expansion;
        max += pose.Position + expansion;
        if (!Finite(min) || !Finite(max)) return Uncertified();
        var overlaps = new ContactCandidates(_contactCandidates, filter.Mobility, exclusions);
        _sim.BroadPhase.GetOverlaps(min, max, ref overlaps);
        if (overlaps.Overflow) return Uncertified();

        float scale = MathF.Max(1f, MathF.Max(MaxAbs(pose.Position), MathF.Max(MaxAbs(min), MaxAbs(max))));
        foreach (CollidableReference candidate in _contactCandidates)
        {
            bool dynamic = candidate.Mobility != CollidableMobility.Static;
            TypedIndex shape;
            RigidPose bodyPose;
            int handle;
            if (dynamic)
            {
                var body = _sim.Bodies.GetBodyReference(candidate.BodyHandle);
                shape = body.Collidable.Shape;
                bodyPose = body.Pose;
                if (!_reverseDynamics.TryGetValue(candidate.BodyHandle.Value, out handle)) return Uncertified();
            }
            else
            {
                _sim.Statics.GetDescription(candidate.StaticHandle, out StaticDescription description);
                shape = description.Shape;
                bodyPose = description.Pose;
                if (!_reverseHandles.TryGetValue(candidate.StaticHandle.Value, out handle)) return Uncertified();
            }
            if (!Finite(bodyPose.Position) || !Finite(bodyPose.Orientation) ||
                MathF.Abs(bodyPose.Orientation.LengthSquared() - 1f) > 0.0001f) return Uncertified();
            _sim.Shapes[shape.Type].ComputeBounds(shape.Index, bodyPose, out Vector3 bodyMin, out Vector3 bodyMax);
            if (!Finite(bodyMin) || !Finite(bodyMax)) return Uncertified();
            scale = MathF.Max(scale, MathF.Max(MaxAbs(bodyPose.Position),
                MathF.Max(MaxAbs(bodyMin), MaxAbs(bodyMax))));
            _contactCollector.AddPair(shape, bodyPose, handle, dynamic);
        }

        // Capsule/hull face selection uses an epsilon no greater than 1e-3 * capsule radius in the
        // pinned backend. Reserve additional local float spacing for transforms and subtraction.
        // A frame/shape outside the 1 mm operating budget is refused rather than reported clear.
        float error = 0.001f * capsule.Radius + 8f * (MathF.BitIncrement(scale) - scale);
        if (!float.IsFinite(error) || error > MaximumContactErrorMetres) return Uncertified();
        float margin = maxSeparationMetres + error;
        _contactCollector.Margin = margin;
        // Refuse excessive mesh density before asking the backend to allocate or test its children.
        for (int i = 0; i < _contactCollector.PairCount; i++)
        {
            CapsuleContactCollector.Pair pair = _contactCollector.PairAt(i);
            if (pair.Shape.Type == default(Mesh).TypeId &&
                !CapsuleMeshContacts.Prepare(_sim.Shapes, pair, queryShape, pose, margin)) return Uncertified();
        }
        var batcher = new CollisionBatcher<CapsuleContactCollector.Callbacks>(_pool, _sim.Shapes,
            _sim.NarrowPhase.CollisionTaskRegistry, 0f, new CapsuleContactCollector.Callbacks(_contactCollector));
        try
        {
            for (int i = 0; i < _contactCollector.PairCount && _contactCollector.Valid; i++)
                if (!CapsuleContactBatch.Submit(_sim.Shapes, _contactCollector.PairAt(i), queryShape,
                    pose, margin, _contactCollector, ref batcher)) return Uncertified();
        }
        finally
        {
            batcher.Flush();
        }
        if (!_contactCollector.Valid) return Uncertified();
        _contactCollector.FinishChildren(_sim.Shapes, _pool);
        if (!_contactCollector.Valid) return Uncertified();
        _contactCollector.SortAndDeduplicate();
        int count = _contactCollector.ContactCount;
        if (count > destination.Length) return new CapsuleContactResult(false, 0, count, error);
        _contactCollector.CopyTo(destination);
        return new CapsuleContactResult(true, count, count, error);
    }

    static CapsuleContactResult Uncertified() => new(false, 0, 0, float.PositiveInfinity);
    static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    struct ContactCandidates(List<CollidableReference> target, QueryMobility mobility,
        StaticQueryExclusions? exclusions) : IBreakableForEach<CollidableReference>
    {
        public bool Overflow;

        public bool LoopBody(CollidableReference reference)
        {
            bool isStatic = reference.Mobility == CollidableMobility.Static;
            if (mobility == QueryMobility.Statics && !isStatic || mobility == QueryMobility.Dynamics && isStatic)
                return true;
            if (exclusions is not null && !exclusions.Allows(reference)) return true;
            if (target.Count == MaximumContactCandidates)
            {
                Overflow = true;
                return false;
            }
            target.Add(reference);
            return true;
        }
    }
}
