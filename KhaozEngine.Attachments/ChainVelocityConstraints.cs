using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal ref struct ChainVelocityConstraints
{
    private readonly ChainParticles _particles;
    private readonly int _root;
    private readonly ChainCarry _carry;
    private readonly ReadOnlySpan<ColliderCapsule> _colliders;
    private readonly ReadOnlySpan<ColliderCapsule> _previousColliders;
    private readonly float _radius;
    private readonly float _seconds;
    private Vector3 _frameTurn;

    internal ChainVelocityConstraints(ChainParticles particles, int root, in ChainCarry carry,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders, float radius, float seconds)
    {
        _particles = particles;
        _root = root;
        _carry = carry;
        _colliders = colliders;
        _previousColliders = previousColliders;
        _radius = radius;
        _seconds = seconds;
    }

    internal void Start(Vector3 previousRest, Vector3 rest)
    {
        if (_seconds == 0)
            return;

        previousRest = _carry.TransformDirection(previousRest);
        // A cone's axis determines its angular motion perpendicular to that axis.
        var axis = Vector3.Cross(previousRest, rest);
        float sine = axis.Length();
        float cosine = Vector3.Dot(previousRest, rest);
        if (sine > 0)
            _frameTurn = axis / sine * (MathF.Atan2(sine, cosine) / _seconds);
        else if (cosine < 0)
            _frameTurn = ChainMath.Perpendicular(previousRest) * (MathF.PI / _seconds);
    }

    internal void Apply(int parent, float length, Vector3 direction, Vector3 rest, bool clamped)
    {
        if (_seconds == 0)
            return;

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
        StopAtCapsules(parent, length, direction);

        // The next cone inherits twist about this segment and swings with its endpoints.
        var swingTurn = Vector3.Cross(direction, velocities[child] - velocities[parent]) / length;
        _frameTurn = Vector3.Dot(_frameTurn, direction) * direction + swingTurn;
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

    private void StopAtCapsules(int parent, float length, Vector3 direction)
    {
        var root = _particles.Positions[parent];
        ref var velocity = ref _particles.Velocities[parent + 1];
        for (int index = 0; index < _colliders.Length; index++)
        {
            ref readonly var capsule = ref _colliders[index];
            if (!ChainCapsuleCollision.TryContact(root, length, direction, _radius, capsule, out var normal, out double along))
                continue;

            var tangent = ChainMath.Tangent(normal, direction);
            if (tangent == Vector3.Zero)
                continue;

            var surface = ChainCapsuleCollision.SurfaceVelocity(capsule, _previousColliders[index], along, _carry, _seconds);
            // Like length and bend, contact projects velocity along one unit direction.
            // The position solve handles a push along the segment so it cannot inject swing speed.
            float approach = Vector3.Dot(velocity - surface, tangent);
            if (approach < 0)
                velocity -= approach * tangent;
        }
    }
}
