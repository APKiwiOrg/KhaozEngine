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
    internal uint? PartId { get; private set; }
    internal void Begin()
    {
        Array.Clear(_multipliers, 0, Count);
        Count = 0;
        Checks = 0;
        PartId = null;
    }

    internal ClothStepStatus Add(ClothTriangle face, Vector3 weights, Vector3 normal, float supportSpeed,
        float[] inverseMasses, uint? partId = null) =>
        Add(face, weights, default, Vector3.Zero, normal, supportSpeed, inverseMasses, partId);

    internal ClothStepStatus Add(ClothTriangle face, Vector3 weights, ClothTriangle opposing, Vector3 opposingWeights,
        Vector3 normal, float supportSpeed, float[] inverseMasses, uint? partId = null)
    {
        var first = new Influence(face, weights, inverseMasses);
        var second = new Influence(opposing, -opposingWeights, inverseMasses);
        float inverseMass = first.InverseMass + second.InverseMass;
        if (inverseMass == 0) return ClothStepStatus.Accepted;
        if (!float.IsFinite(supportSpeed) || !float.IsFinite(inverseMass)) return ClothStepStatus.NonfiniteState;
        if (Count == _witnesses.Length) return ClothStepStatus.ContactCapacity;

        _witnesses[Count++] = new Witness(first, second, inverseMass, normal, supportSpeed, partId);
        return ClothStepStatus.Accepted;
    }

    internal ClothStepStatus ProjectAndCheck(Vector3[] velocities)
    {
        if (Count == 0) return ClothStepStatus.Accepted;
        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            for (int i = 0; i < Count; i++)
            {
                ref readonly Witness witness = ref _witnesses[i];
                PartId = witness.PartId;
                if (!CountCheck()) return ClothStepStatus.NormalVelocityWork;
                float speed = witness.RelativeSpeed(velocities);
                float next = MathF.Max(0, _multipliers[i] - speed / witness.InverseMass);
                if (!float.IsFinite(next)) return ClothStepStatus.NonfiniteState;

                float change = next - _multipliers[i];
                if (change != 0)
                    witness.Apply(velocities, change * witness.Normal);
                _multipliers[i] = next;
            }

            bool converged = true;
            uint? failedPart = null;
            for (int i = 0; i < Count; i++)
            {
                ref readonly Witness witness = ref _witnesses[i];
                PartId = witness.PartId;
                if (!CountCheck()) return ClothStepStatus.NormalVelocityWork;
                float speed = witness.RelativeSpeed(velocities);
                if (!float.IsFinite(speed)) return ClothStepStatus.NonfiniteState;
                if (speed < -SpeedRoundoff || (_multipliers[i] > 0 && speed > SpeedRoundoff))
                {
                    converged = false;
                    failedPart = witness.PartId;
                }
            }
            PartId = failedPart;
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

    private readonly record struct Witness(Influence First, Influence Second, float InverseMass,
        Vector3 Normal, float SupportSpeed, uint? PartId)
    {
        internal float RelativeSpeed(Vector3[] velocities) =>
            Vector3.Dot(First.Speed(velocities) + Second.Speed(velocities), Normal) - SupportSpeed;

        internal void Apply(Vector3[] velocities, Vector3 change)
        {
            First.Apply(velocities, change);
            Second.Apply(velocities, change);
        }
    }

    private readonly record struct Influence(ClothTriangle Face, Vector3 Weights, Vector3 Share)
    {
        internal Influence(ClothTriangle face, Vector3 weights, float[] inverseMasses)
            : this(face, weights, weights * new Vector3(inverseMasses[face.A], inverseMasses[face.B], inverseMasses[face.C])) { }

        internal float InverseMass => Vector3.Dot(Weights, Share);
        internal Vector3 Speed(Vector3[] velocities) => Face.Blend(velocities, Weights);

        internal void Apply(Vector3[] velocities, Vector3 change)
        {
            velocities[Face.A] += Share.X * change;
            velocities[Face.B] += Share.Y * change;
            velocities[Face.C] += Share.Z * change;
        }
    }
}
