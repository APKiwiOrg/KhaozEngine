namespace KhaozEngine.Render3D;

/// <summary>Opaque consumer-defined animation state. Zero is a valid key.</summary>
public readonly record struct CoverageStateKey(ulong Value);

/// <summary>Maps a state to a table entry, or to an explicit hole when EntryIndex is -1.</summary>
public readonly record struct CoverageStateEntry(CoverageStateKey Key, int EntryIndex);
