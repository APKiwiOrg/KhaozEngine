using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Allocation-free cosmetic agitation from horizontal movement, with settling and teleport resets.
/// A default-initialized value uses a 2 m/s reference speed, a .6 s settle time and a 5 m teleport distance.</summary>
public struct FoliageAgitation
{
    readonly float _referenceSpeed, _settleTime, _teleportResetDistance;
    Vector3 _position;
    bool _seeded;

    /// <summary>Creates a tracker with the documented defaults.</summary>
    public FoliageAgitation() : this(2f, .6f, 5f) { }

    /// <summary>Creates a tracker with finite, strictly positive movement and settling settings.</summary>
    public FoliageAgitation(float referenceSpeed = 2f, float settleTime = .6f, float teleportResetDistance = 5f)
    {
        ValidateSetting(referenceSpeed, nameof(referenceSpeed));
        ValidateSetting(settleTime, nameof(settleTime));
        ValidateSetting(teleportResetDistance, nameof(teleportResetDistance));
        _referenceSpeed = referenceSpeed;
        _settleTime = settleTime;
        _teleportResetDistance = teleportResetDistance;
        _position = default;
        _seeded = false;
        Value = 0f;
    }

    /// <summary>Horizontal speed in metres per second that produces full agitation.</summary>
    public readonly float ReferenceSpeed => _referenceSpeed > 0f ? _referenceSpeed : 2f;
    /// <summary>Seconds for full agitation to settle to rest when movement stops.</summary>
    public readonly float SettleTime => _settleTime > 0f ? _settleTime : .6f;
    /// <summary>A full 3D move beyond this distance in metres resets agitation instead of spiking it.</summary>
    public readonly float TeleportResetDistance => _teleportResetDistance > 0f ? _teleportResetDistance : 5f;
    /// <summary>Current agitation, from zero through one.</summary>
    public float Value { get; private set; }

    /// <summary>Samples a finite position. First samples seed the tracker. Zero time rebases without changing
    /// agitation. Invalid input leaves all state intact.</summary>
    public float Update(Vector3 position, float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime < 0f)
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Frame time must be finite and non-negative.");
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentException("Position must be finite.", nameof(position));
        if (!_seeded || deltaTime == 0f)
        {
            _position = position;
            _seeded = true;
            return Value;
        }

        // Double intermediates keep differences of opposite finite float coordinates and tiny times finite.
        double dx = (double)position.X - _position.X;
        double dy = (double)position.Y - _position.Y;
        double dz = (double)position.Z - _position.Z;
        _position = position;
        double resetDistance = TeleportResetDistance;
        if (dx * dx + dy * dy + dz * dz > resetDistance * resetDistance)
            return Value = 0f;
        double speed = Math.Sqrt(dx * dx + dz * dz) / deltaTime;
        double target = Math.Clamp(speed / ReferenceSpeed, 0d, 1d);
        Value = (float)Math.Max(target, Math.Max(0d, Value - (double)deltaTime / SettleTime));
        return Value;
    }

    /// <summary>Clears agitation and forgets the sampled position, preserving configuration.</summary>
    public void Reset()
    {
        _position = default;
        _seeded = false;
        Value = 0f;
    }

    static void ValidateSetting(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(parameterName, "Setting must be finite and strictly positive.");
    }
}
