namespace KhaozEngine.Attachments;

/// <summary>Identifies the measured lift footprint file for a worn attachment set.</summary>
/// <param name="Key">The relative, forward-slash asset key of the footprint file.</param>
/// <param name="Sha256">The 64 lowercase hexadecimal digits of the file's SHA-256.</param>
public readonly record struct LiftFootprintRef(string Key, string Sha256);
