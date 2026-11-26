using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// The closing position check supplies the active witnesses. This bounded pass
// removes approaching motion, then checks the same witnesses after coupled impulses.
internal sealed class ClothContactVelocity(int capacity)
{
    private const float SpeedRoundoff = .000001f;

    private readonly Witness[] _witnesses = new Witness[capacity];

    internal int Count { get; private set; }
    internal void Begin() => Count = 0;

    internal ClothStepStatus Add(ClothTriangle face, Vector3 weights, Vector3 normal, float supportSpeed,
        float[] inverseMasses)
    {
        var mass = new Vector3(inverseMasses[face.A], inverseMasses[face.B], inverseMasses[face.C]);
        Vector3 share = weights * mass;
        float inverseMass = Vector3.Dot(weights, share);
        if (inverseMass == 0) return ClothStepStatus.Accepted;
        if (!float.IsFinite(supportSpeed)) return ClothStepStatus.NonfiniteState;
        if (Count == _witnesses.Length) return ClothStepStatus.ContactCapacity;

        _witnesses[Count++] = new Witness(face, weights, share / inverseMass, normal, supportSpeed);
        return ClothStepStatus.Accepted;
    }

    internal ClothStepStatus ProjectAndCheck(Vector3[] velocities)
    {
        ReadOnlySpan<Witness> active = _witnesses.AsSpan(0, Count);
        foreach (ref readonly Witness witness in active)
        {
            float speed = witness.RelativeSpeed(velocities);
            if (!float.IsFinite(speed)) return ClothStepStatus.NonfiniteState;
            if (speed < 0)
                witness.Apply(velocities, -speed * witness.Normal);
        }

        foreach (ref readonly Witness witness in active)
        {
            float speed = witness.RelativeSpeed(velocities);
            if (!float.IsFinite(speed)) return ClothStepStatus.NonfiniteState;
            if (speed < -SpeedRoundoff) return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private readonly record struct Witness(ClothTriangle Face, Vector3 Weights, Vector3 Share, Vector3 Normal,
        float SupportSpeed)
    {
        internal float RelativeSpeed(Vector3[] velocities) =>
            Vector3.Dot(Face.Blend(velocities, Weights), Normal) - SupportSpeed;

        internal void Apply(Vector3[] velocities, Vector3 change)
        {
            velocities[Face.A] += Share.X * change;
            velocities[Face.B] += Share.Y * change;
            velocities[Face.C] += Share.Z * change;
        }
    }
}
