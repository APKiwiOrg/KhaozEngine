using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>The requested operation on one canonical runtime static owner.</summary>
public enum RuntimeStaticChangeKind { Set = 1, Remove = 2 }

/// <summary>An immutable runtime-static request. The default value is invalid.
/// Set privately copies and validates all shape data. Local handles are never portable identities.</summary>
public readonly struct RuntimeStaticChange
{
    public RuntimeStaticChangeKind Kind { get; }
    public MovementSupportKey Owner { get; }
    internal PhysicsShape? Shape { get; }
    internal Pose AbsolutePose { get; }
    internal PhysicsMaterial? Material { get; }

    RuntimeStaticChange(RuntimeStaticChangeKind kind, MovementSupportKey owner, PhysicsShape? shape,
        Pose pose, PhysicsMaterial? material)
    { Kind = kind; Owner = owner; Shape = shape; AbsolutePose = pose; Material = material; }

    /// <summary>Adds or replaces one complete static for owner at an absolute world pose.
    /// Uses ordinary physics placement, including base-aligned cylinders. Copies mutable arrays and
    /// compound descendants. The batch validates the owner's world, uniqueness and fixed extent.</summary>
    public static RuntimeStaticChange Set(MovementSupportKey owner, PhysicsShape shape, Pose absolutePose,
        PhysicsMaterial? material = null)
    {
        MovementEnvironmentValidation.Require(owner.IsValid, nameof(owner));
        RuntimeStaticGeometry.ValidatePose(absolutePose);
        if (material is { } value)
            MovementEnvironmentValidation.Require(float.IsFinite(value.Friction) && value.Friction >= 0 &&
                float.IsFinite(value.Restitution) && value.Restitution >= 0 && value.Restitution <= 1, nameof(material));
        return new(RuntimeStaticChangeKind.Set, owner, RuntimeStaticGeometry.Copy(shape), absolutePose, material);
    }

    /// <summary>Removes a live runtime owner. Baked owners and absent targets cannot be removed.</summary>
    public static RuntimeStaticChange Remove(MovementSupportKey owner)
    {
        MovementEnvironmentValidation.Require(owner.IsValid, nameof(owner));
        return new(RuntimeStaticChangeKind.Remove, owner, null, default, null);
    }
}
