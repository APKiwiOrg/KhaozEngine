using System;
using System.Numerics;
using KhaozEngine.Determinism;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Caller-owned storage for the single wearer and garment receiving local cloth.</summary>
/// <remarks>Prepare at equip time and serialize calls that use this slot. To give it to a different wearer,
/// step the owner without a selection until the handoff completes, or release an owner leaving visibility.</remarks>
public sealed class WornClothSlot
{
    private WornClothState? _state;
    internal WornMotion? Owner => _state?.Owner;
    internal WornClothState State => _state!;

    /// <summary>Whether this slot holds a prewarmed material profile.</summary>
    public bool IsPrepared => _state is not null;

    /// <summary>Whether a wearer owns the slot, including an outgoing blend.</summary>
    public bool IsOccupied => Owner is not null;

    /// <summary>The pose and work last published by the owner, if any.</summary>
    public WornClothStatus Status => Owner?.ClothStatus ?? default;

    /// <summary>Reserve the material solver and all other cloth storage for a compatible fitted binding.
    /// Scale is the positive uniform wearer scale in the model transform.</summary>
    public void Prepare(BoundClothSurface surface, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!(scale > 0) || !float.IsFinite(scale))
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (_state is { } previous && previous.Compatible(surface) && previous.Scale == scale) return;
        if (Owner is not null)
            throw new InvalidOperationException("Release the occupied cloth slot before preparing another profile.");

        using var scope = DeterministicFpScope.Enter();
        ClothRig rig = surface.Rig;
        if (scale != 1)
        {
            Span<Vector3> rest = stackalloc Vector3[rig.RestPositions.Length];
            Span<Vector2> coordinates = stackalloc Vector2[rig.MaterialCoordinates.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                rest[i] = rig.RestPositions[i] * scale;
                coordinates[i] = rig.MaterialCoordinates[i] * scale;
            }
            rig = ClothRig.Create(rest, coordinates, rig.Triangles, rig.Pins, surface.Description.Hinges.AsSpan(),
                rig.Material with { HalfThickness = rig.Material.HalfThickness * scale });
        }
        _state = new(surface, rig, scale);
    }

    /// <summary>Immediately withdraw a wearer that has left visibility.</summary>
    public void Release()
    {
        if (_state?.Owner is { } owner)
        {
            using var scope = DeterministicFpScope.Enter();
            owner.DetachCloth(this);
            _state.Release();
        }
    }

    /// <summary>Move this wearer's displayed material to the new outfit motion after an equipment change.
    /// The next step rebinds pins and contacts. Returns false and releases the old view if the new part is
    /// hidden or has a different mesh or sidecar.</summary>
    public bool Rebind(WornMotion motion, int wornIndex, uint partId)
    {
        ArgumentNullException.ThrowIfNull(motion);
        if (_state?.Owner is not { } owner)
            return false;
        using var scope = DeterministicFpScope.Enter();
        if (motion.ActiveClothSlot is not null)
            throw new InvalidOperationException("The new motion already owns a cloth slot.");
        var binding = motion.FindClothBinding(wornIndex, partId);
        if (binding is null || !binding.HasCapacity || !_state.Compatible(binding.Surface))
        {
            Release();
            return false;
        }

        owner.DetachCloth(this, retainPresentation: true);
        _state.Rebind(motion, binding);
        motion.AttachCloth(this);
        return true;
    }

    internal bool ModelFits(in Matrix4x4 model) => _state is { } state &&
        ClothHullPose.TryCreate(model, out var pose) && MathF.Abs(pose.Scale - state.Scale) <= state.Scale * .00001f;

    internal WornClothFallback Admission(WornMotion wearer, WornClothBinding binding, in Matrix4x4 model,
        ChainSimulationOptions options)
    {
        if (_state is null) return WornClothFallback.Unprepared;
        if (!binding.HasCapacity) return WornClothFallback.SupportCapacity;
        if (!_state.Compatible(binding.Surface))
            return WornClothFallback.IncompatibleProfile;
        if (!ModelFits(model)) return WornClothFallback.UnsupportedTransform;
        if (options.MaxStepSeconds != ClothWorkLimits.LayeredStepSeconds || options.MaxStepsPerFrame > ClothWorkLimits.StepsPerFrameLimit)
            return WornClothFallback.UnsupportedClock;
        return Owner is not null && !ReferenceEquals(Owner, wearer)
            ? WornClothFallback.SlotOccupied : WornClothFallback.None;
    }
}
