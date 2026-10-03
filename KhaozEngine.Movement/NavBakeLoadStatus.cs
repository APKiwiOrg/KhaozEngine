namespace KhaozEngine.Movement;

/// <summary>Outcome of loading a baked navigation set. Checks run in container, identity and payload order, and
/// the first failing check decides the status.</summary>
public enum NavBakeLoadStatus
{
    /// <summary>The bake matches the expectation and its payload is intact.</summary>
    Loaded,

    /// <summary>The bytes do not start with the bake magic.</summary>
    NotABake,

    /// <summary>The container format version or flags are not supported by this engine.</summary>
    UnsupportedFormat,

    /// <summary>The bytes are truncated, carry trailing data, are not canonical or fail a payload check.</summary>
    Corrupt,

    /// <summary>The bake was written by a different engine version.</summary>
    EngineChanged,

    /// <summary>A capture option differs from the expectation.</summary>
    OptionsChanged,

    /// <summary>A source label is missing, extra or has a different digest.</summary>
    SourcesChanged,

    /// <summary>A profile is missing, extra or has a different area filter or probe tuning.</summary>
    ProfilesChanged,
}
