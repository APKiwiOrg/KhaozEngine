using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>One producer-certified analytic field at an exact axis in a canonical occupied space.</summary>
public readonly record struct MovementAnalyticGround
{
    public Vector2 Axis { get; }
    public float Height { get; }
    public Vector3 Normal { get; }
    public MovementSupportKey Owner { get; }
    public MovementSpaceKey Space { get; }
    public string? TraversedLinkId { get; }
    public MovementQueryIdentity Identity { get; }
    internal Contacts.SupportHeightTranslation HeightTranslation { get; init; }
    public bool IsValid => float.IsFinite(Axis.X) && float.IsFinite(Axis.Y) && float.IsFinite(Height) &&
        MovementEnvironmentValidation.Unit(Normal) && Owner.IsValid && Space.IsValid && Identity.IsValid &&
        string.Equals(Owner.WorldId, Space.WorldId, StringComparison.Ordinal) &&
        (TraversedLinkId is null || MovementEnvironmentValidation.Name(TraversedLinkId)) &&
        HeightTranslation.IsValid && (HeightTranslation == default || HeightTranslation.LocalHeight == Height);

    public MovementAnalyticGround(Vector2 axis, float height, Vector3 normal, MovementSupportKey owner,
        MovementSpaceKey space, string? traversedLinkId, MovementQueryIdentity identity)
    {
        Axis = axis;
        Height = height;
        Normal = normal;
        Owner = owner;
        Space = space;
        TraversedLinkId = traversedLinkId;
        Identity = identity;
        HeightTranslation = default;
        MovementEnvironmentValidation.Require(IsValid, nameof(axis));
    }
}

/// <summary>The shared foot primitive's certified geometry and installed provenance, without reselecting height.
/// Axis is the body's XZ. Witness remains the touched point, including an off-axis bank contact.</summary>
public readonly record struct MovementContactSupportRequest
{
    public MovementBodyQuery Body { get; }
    public MovementTransitionContext Transition { get; }
    public Vector2 Axis { get; }
    public float Height { get; }
    public float HeightError { get; }
    public Vector3 Normal { get; }
    public Vector3 Witness { get; }
    public StaticHandle? Static { get; }
    public int FeatureId { get; }
    public bool IsValid => Body.IsValid && Transition.IsValid &&
        string.Equals(Body.CurrentSpace.WorldId, Transition.OriginSpace.WorldId, StringComparison.Ordinal) &&
        Axis.X == Body.Centre.X && Axis.Y == Body.Centre.Z && float.IsFinite(Height) &&
        MovementEnvironmentValidation.Nonnegative(HeightError) && MovementEnvironmentValidation.Unit(Normal) &&
        MovementEnvironmentValidation.Finite(Witness) &&
        (Static is { } installed ? installed.Value >= 0 && FeatureId >= 0 : FeatureId == -1);

    public MovementContactSupportRequest(MovementBodyQuery body, MovementTransitionContext transition,
        Vector2 axis, float height, float heightError, Vector3 normal, Vector3 witness, StaticHandle? @static, int featureId)
    {
        Body = body;
        Transition = transition;
        Axis = axis;
        Height = height;
        HeightError = heightError;
        Normal = normal;
        Witness = witness;
        Static = @static;
        FeatureId = featureId;
        MovementEnvironmentValidation.Require(IsValid, nameof(body));
    }
}

/// <summary>Canonical owner certified from the pin's captured registration, never from a local handle string.</summary>
public readonly record struct MovementContactSupportBinding
{
    public MovementSupportKey Owner { get; }
    public MovementSpaceKey Space { get; }
    public string? TraversedLinkId { get; }
    public MovementQueryIdentity Identity { get; }
    public bool IsValid => Owner.IsValid && Space.IsValid && Identity.IsValid &&
        string.Equals(Owner.WorldId, Space.WorldId, StringComparison.Ordinal) &&
        (TraversedLinkId is null || MovementEnvironmentValidation.Name(TraversedLinkId));

    public MovementContactSupportBinding(MovementSupportKey owner, MovementSpaceKey space,
        string? traversedLinkId, MovementQueryIdentity identity)
    {
        Owner = owner;
        Space = space;
        TraversedLinkId = traversedLinkId;
        Identity = identity;
        MovementEnvironmentValidation.Require(IsValid, nameof(owner));
    }
}
