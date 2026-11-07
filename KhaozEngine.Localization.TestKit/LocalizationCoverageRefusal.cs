namespace KhaozEngine.Localization.TestKit;

/// <summary>Rules a <see cref="LocalizationCoverage"/> check refuses. A coverage failure carries the first finding's
/// rule as <see cref="LocalizationCoverageException.Reason"/> and every finding in
/// <see cref="LocalizationCoverageException.Findings"/>.</summary>
public enum LocalizationCoverageRefusal
{
    // Key universe and neutral resx.

    /// <summary>Refuses a key universe with no keys: a keys class with no key fields, a neutral resx with no string
    /// entries or an explicit sequence with no non-empty key.</summary>
    KeyUniverseEmpty,

    /// <summary>Refuses a <see cref="System.Resources.ResourceManager"/> whose neutral (invariant) resource set
    /// cannot be loaded.</summary>
    NeutralResourcesUnavailable,

    /// <summary>Refuses keys the neutral resx does not hold.</summary>
    NeutralKeysMissing,

    // Satellite cultures.

    /// <summary>Refuses a satellite culture name that is not a valid culture name.</summary>
    CultureNameInvalid,

    /// <summary>Refuses a satellite culture with no resource set of its own, which leaves every key
    /// unresolved.</summary>
    SatelliteResourcesUnavailable,

    /// <summary>Refuses keys a satellite resource set does not hold, read with parent fallback off.</summary>
    TranslationsMissing,

    /// <summary>Refuses a translation whose set of placeholder indices differs from its neutral template's.</summary>
    PlaceholderSetMismatch,
}
