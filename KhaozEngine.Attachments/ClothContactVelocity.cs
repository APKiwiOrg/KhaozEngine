using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Projected Gauss-Seidel solves the unilateral normal impulses. A positive
// multiplier requires a seated contact even if a later impulse pushes it apart.
internal sealed class ClothContactVelocity(int capacity, int maxChecks = ClothWorkLimits.NormalVelocityCheckLimit)
{
    private const float SpeedRoundoff = .000001f;
    private const int MaxSweeps = 48;

    private readonly Witness[] _witnesses = new Witness[capacity];
    private readonly float[] _multipliers = new float[capacity];

    internal int Count { get; private set; }
    internal int Checks { get; private set; }
    internal void Begin()
    {
        Array.Clear(_multipliers, 0, Count);
        Count = 0;
        Checks = 0;
    }

    internal ClothStepStatus Add(ClothTriangle face, Vector3 weights, Vector3 normal, float supportSpeed,
        float[] inverseMasses)
    {
        var mass = new Vector3(inverseMasses[face.A], inverseMasses[face.B], inverseMasses[face.C]);
        Vector3 share = weights * mass;
        float inverseMass = Vector3.Dot(weights, share);
        if (inverseMass == 0) return ClothStepStatus.Accepted;
        if (!float.IsFinite(supportSpeed)) return ClothStepStatus.NonfiniteState;
        if (Count == _witnesses.Length) return ClothStepStatus.ContactCapacity;

        _witnesses[Count++] = new Witness(face, weights, share, inverseMass, normal, supportSpeed);
        return ClothStepStatus.Accepted;
    }

    internal ClothStepStatus ProjectAndCheck(Vector3[] velocities)
    {
        if (Count == 0) return ClothStepStatus.Accepted;
        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            for (int i = 0; i < Count; i++)
            {
                if (!CountCheck()) return ClothStepStatus.NormalVelocityWork;
                ref readonly Witness witness = ref _witnesses[i];
                float speed = witness.RelativeSpeed(velocities);
                float next = MathF.Max(0, _multipliers[i] - speed / witness.InverseMass);
                if (!float.IsFinite(next)) return ClothStepStatus.NonfiniteState;

                float change = next - _multipliers[i];
                if (change != 0)
                    witness.Apply(velocities, change * witness.Normal);
                _multipliers[i] = next;
            }

            bool converged = true;
            for (int i = 0; i < Count; i++)
            {
                if (!CountCheck()) return ClothStepStatus.NormalVelocityWork;
                float speed = _witnesses[i].RelativeSpeed(velocities);
                if (!float.IsFinite(speed)) return ClothStepStatus.NonfiniteState;
                converged &= speed >= -SpeedRoundoff && (_multipliers[i] == 0 || speed <= SpeedRoundoff);
            }
            if (converged) return ClothStepStatus.Accepted;
        }
        return ClothStepStatus.UnresolvedContact;
    }

    private bool CountCheck()
    {
        if (Checks == maxChecks) return false;
        Checks++;
        return true;
    }

    private readonly record struct Witness(ClothTriangle Face, Vector3 Weights, Vector3 Share, float InverseMass,
        Vector3 Normal, float SupportSpeed)
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
