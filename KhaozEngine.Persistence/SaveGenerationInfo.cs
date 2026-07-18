using System;

namespace KhaozEngine.Persistence;

/// <summary>
/// One probed generation of a save file, as reported by <see cref="GameStorage.ListGenerations"/>.
/// Generation 0 is the primary path, generation n (n &gt;= 1) is its nth backup (see
/// <see cref="SaveBackups.GenerationPath"/>). A missing generation carries a null
/// <see cref="LastWriteTimeUtc"/> and <see cref="Metadata"/>, everything else reflects what the probe
/// found there.
/// </summary>
/// <param name="Generation">The generation index (0 = primary, 1..N = backups).</param>
/// <param name="Path">The on-disk path this generation was probed at.</param>
/// <param name="LastWriteTimeUtc">The file's last write time, or null when the generation is missing.</param>
/// <param name="Validity">How the generation classified: valid, tampered, corrupt, or missing.</param>
/// <param name="Metadata">The decoded envelope metadata, or null when the generation is missing, invalid, or unencoded.</param>
public sealed record SaveGenerationInfo(int Generation, string Path, DateTime? LastWriteTimeUtc, SaveGenerationValidity Validity, SaveMetadata? Metadata);
