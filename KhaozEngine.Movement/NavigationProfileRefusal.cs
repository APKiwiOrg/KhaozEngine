namespace KhaozEngine.Movement;

/// <summary>The compatibility or policy rule that refused a navigation profile.</summary>
public enum NavigationProfileRefusal
{
    /// <summary>Ordinary contact navigation does not yet support aquatic profiles.</summary>
    ContactAquaticUnsupported,
    /// <summary>Drop navigation requires non-aquatic ordinary contact movement.</summary>
    DropProfileUnsupported,
    /// <summary>Navigation does not yet support public explicit movement bindings.</summary>
    PublicExplicitProfileUnsupported,
    /// <summary>The runtime movement binding kind differs from the baked kind.</summary>
    BindingKindMismatch,
    /// <summary>The profile slope differs from the capture slope.</summary>
    CaptureSlopeMismatch,
    /// <summary>An aquatic profile requires capture water samples.</summary>
    CaptureWaterNotSampled,
    /// <summary>A retained legacy geometry field differs from the baked field.</summary>
    GeometryMismatch,
    /// <summary>A retained legacy aquatic swim fraction differs from the baked fraction.</summary>
    SwimFractionsMismatch,
    /// <summary>Aquatic swim fractions are not ordered exit, submersion, enter.</summary>
    SwimFractionsUnordered,
    /// <summary>Terrain descents require positive Drop permission.</summary>
    TerrainDropPermissionRequired,
    /// <summary>A retained contact tuning field differs from the baked field.</summary>
    TuningFieldMismatch,
}
