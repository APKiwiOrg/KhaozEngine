namespace KhaozEngine.Locomotion;

/// <summary>The configuration or capability rule that refused movement.</summary>
public enum MovementConfigurationRefusal
{
    /// <summary>The physics view does not provide complete capsule contacts.</summary>
    CapsuleContactsRequired,
    /// <summary>The physics view does not provide certified capsule sweeps.</summary>
    CapsuleSweepRequired,
    /// <summary>Point-sampled water cannot certify contact movement.</summary>
    PointSampledWaterUnsupported,
    /// <summary>The physics world does not provide query read leases.</summary>
    QueryLeaseSourceRequired,
    /// <summary>Contact movement requires the stateful movement overload.</summary>
    StatefulOverloadRequired,
    /// <summary>The physics world does not provide complete support footprints.</summary>
    SupportFootprintRequired,
    /// <summary>Ordinary contact movement has no certified boundary.</summary>
    BoundaryRequired,
    /// <summary>Explicit contact movement requires a non-Legacy water mode.</summary>
    ExplicitWaterModeRequired,
    /// <summary>A kernel fraction exceeds one.</summary>
    KernelFractionExceeded,
    /// <summary>The capsule half height is below its radius.</summary>
    KernelHalfHeightBelowRadius,
    /// <summary>A kernel value that permits zero is negative.</summary>
    KernelNonnegativeValueRequired,
    /// <summary>A kernel number is not finite.</summary>
    KernelNumberNonfinite,
    /// <summary>A strictly positive kernel value is zero or negative.</summary>
    KernelPositiveValueRequired,
    /// <summary>The maximum kernel slope is at or above half pi.</summary>
    KernelSlopeAtOrAboveHalfPi,
    /// <summary>The swim exit fraction exceeds its enter fraction.</summary>
    KernelSwimFractionsUnordered,
    /// <summary>The wade end fraction does not exceed its start fraction.</summary>
    KernelWadeFractionsUnordered,
    /// <summary>Legacy movement requires the default foot radius fraction.</summary>
    LegacyFootRadiusNondefault,
    /// <summary>Legacy movement requires the complete default water policy.</summary>
    LegacyWaterNondefault,
    /// <summary>Ordinary contact movement has no movement registration.</summary>
    RegistrationRequired,
    /// <summary>An ordinary contact medium delegate has no certified water field.</summary>
    WaterFieldRequired,
    /// <summary>The tuning water policy conflicts with the supplied explicit policy.</summary>
    WaterPoliciesConflict,
    /// <summary>A Legacy binding requires the Legacy controller.</summary>
    LegacyControllerRequired,
    /// <summary>An ordinary contact binding requires the Contact controller.</summary>
    ContactControllerRequired,
    /// <summary>Public explicit binding and Contact are two movement selections.</summary>
    ConflictingMovementSelections,
    /// <summary>Ordinary contact movement has no physics world.</summary>
    PhysicsRequired,
    /// <summary>Contact cells require a physics world factory.</summary>
    PhysicsFactoryRequired,
    /// <summary>Contact cells require a movement registration factory.</summary>
    RegistrationFactoryRequired,
    /// <summary>Contact cells require a boundary factory or certified bounds.</summary>
    BoundaryFactoryRequired,
    /// <summary>A contact cell medium delegate requires a water field factory.</summary>
    WaterFieldFactoryRequired
}
