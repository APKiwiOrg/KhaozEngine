using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal ref struct ChainVelocityConstraints
{
    private readonly ChainParticles _particles;
    private readonly int _root;
    private readonly ChainCarry _carry;
    private readonly ReadOnlySpan<ColliderCapsule> _previousColliders;
    private readonly float _radius;
    private readonly float _seconds;
    private Vector3 _frameTurn;

    internal ChainVelocityConstraints(ChainParticles particles, int root, in ChainCarry carry,
        ReadOnlySpan<ColliderCapsule> previousColliders, float radius, float seconds)
    {
        _particles = particles;
        _root = root;
        _carry = carry;
        _previousColliders = previousColliders;
        _radius = radius;
        _seconds = seconds;
    }

    internal Vector3 FrameTurn { get => _frameTurn; set => _frameTurn = value; }

    internal void Start(int anchor)
    {
        if (_seconds == 0)
            return;

        var previous = _particles.PreviousAnchorRotations[anchor];
        var carried = _carry.Turn == Quaternion.Identity ? previous : _carry.Turn * previous;
        var current = _particles.StepAnchorRotations[anchor];
        if (current == carried || current == -carried)
            return;

        // Keep the full carried-relative turn so downstream cones inherit twist.
        var turn = current * Quaternion.Conjugate(carried);
        var axis = new Vector3(turn.X, turn.Y, turn.Z);
        float sine = axis.Length();
        if (sine > 0)
        {
            float angle = 2 * MathF.Atan2(sine, MathF.Abs(turn.W));
            _frameTurn = (turn.W < 0 ? -axis : axis) / sine * (angle / _seconds);
        }
    }

    internal void Apply(int parent, float length, Vector3 direction, Vector3 rest, bool clamped, ChainCapsuleCandidates capsules)
    {
        if (_seconds == 0)
            return;

        ApplyLocal(parent, length, direction, rest, clamped, capsules);
        _frameTurn = NextTurn(_particles, parent, length, direction, _frameTurn);
    }

    internal void ApplyLocal(int parent, float length, Vector3 direction, Vector3 rest, bool clamped, ChainCapsuleCandidates capsules)
    {
        var velocities = _particles.Velocities;
        int child = parent + 1;
        if (length == 0)
        {
            velocities[child] = velocities[parent];
            return;
        }

        var relative = velocities[child] - velocities[parent];
        velocities[child] -= Vector3.Dot(relative, direction) * direction;
        if (clamped)
            StopAtCone(parent, length, direction, rest);
        StopAtCapsules(parent, length, direction, capsules);
    }

    internal static Vector3 NextTurn(ChainParticles particles, int parent, float length, Vector3 direction, Vector3 frameTurn)
    {
        if (length == 0)
            return frameTurn;

        // The next cone inherits twist about this segment and swings with its endpoints.
        var velocities = particles.Velocities;
        var swingTurn = Vector3.Cross(direction, velocities[parent + 1] - velocities[parent]) / length;
        return Vector3.Dot(frameTurn, direction) * direction + swingTurn;
    }

    private void StopAtCone(int parent, float length, Vector3 direction, Vector3 rest)
    {
        var outward = ChainMath.Tangent(-rest, direction);
        var velocities = _particles.Velocities;
        int child = parent + 1;
        var wall = velocities[parent] + Vector3.Cross(_frameTurn, length * direction);
        float away = Vector3.Dot(velocities[child] - wall, outward);
        if (away <= 0)
            return;

        // A free parent shares the stop as an equal mass. The anchor cannot take a reaction.
        var stop = away * outward;
        if (parent != _root)
        {
            stop *= 0.5f;
            velocities[parent] += stop;
        }
        velocities[child] -= stop;
    }

    private void StopAtCapsules(int parent, float length, Vector3 direction, ChainCapsuleCandidates capsules)
    {
        if (capsules.Count == 0)
            return;

        var tip = new ChainCapsuleCollision.Tip(_particles.Positions[parent], length, direction);
        ref var velocity = ref _particles.Velocities[parent + 1];
        ChainCapsuleCollision.Axis scratch = default;
        for (int index = 0; index < capsules.Count; index++)
        {
            ref readonly var capsule = ref capsules[index];
            ref readonly var axis = ref capsules.ReadAxis(index, ref scratch);
            if (!ChainCapsuleCollision.TryContact(tip, _radius, capsule, axis, out var normal, out double along))
                continue;

            var tangent = ChainMath.Tangent(normal, direction);
            if (tangent == Vector3.Zero)
                continue;

            var surface = ChainCapsuleCollision.SurfaceVelocity(capsule,
                _previousColliders[capsules.OriginalIndex(index)], along, normal, _carry, _seconds);
            // Like length and bend, contact projects velocity along one unit direction.
            // The position solve handles a push along the segment so it cannot inject swing speed.
            float approach = Vector3.Dot(velocity - surface, tangent);
            if (approach < 0)
                velocity -= approach * tangent;
        }
    }
}
