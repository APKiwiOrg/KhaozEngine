using System;
using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Thrown by a cell-blob migration when the stored body walks cleanly at more than one wire generation AND those
/// walks disagree about the bytes they produce, so there is no honest way to pick one.
/// <para>
/// This is the case the shipped 17.38.0 heuristic used to resolve by scoring: it kept the parse that recovered the
/// most component frames, on the argument that an over-long read can only swallow frames. That argument only covers
/// candidates NEWER than the truth. A candidate OLDER than the truth UNDER-reads a built-in payload, and the bytes it
/// leaves behind re-sync into extra frames, so it can outscore the truth and win - producing a structurally valid
/// current-generation body that decodes silently into wrong movement fields and phantom components. Refusing to pick
/// is the only safe answer: the driver quarantines the cell with its bytes preserved
/// (<see cref="CellPersistenceIssueKind.QuarantinedAmbiguous"/>) and an operator who knows the save's provenance
/// resolves it with <see cref="CellPersistenceConfig.AssumedWireGeneration"/>.
/// </para>
/// </summary>
public sealed class AmbiguousCellBlobGenerationException : InvalidOperationException
{
    /// <summary>Names the candidate generations that walked the body cleanly, and the knob that resolves it.</summary>
    public AmbiguousCellBlobGenerationException(string schemaLabel, IReadOnlyList<int> candidateGenerations)
        : base($"Snapshot body walks as a {schemaLabel} blob at wire generations " +
               $"{string.Join(", ", candidateGenerations ?? Array.Empty<int>())} and they do not agree on the result, " +
               "so the generation cannot be inferred. Set CellPersistenceConfig.AssumedWireGeneration (or " +
               "CellBlobMigrationOptions.AssumedWireGeneration) to the generation this save was written at.")
    {
        SchemaLabel = schemaLabel;
        CandidateGenerations = candidateGenerations ?? Array.Empty<int>();
    }

    /// <summary>The schema the body was being read as, in the same words the migration reports elsewhere.</summary>
    public string SchemaLabel { get; }

    /// <summary>Every wire generation the body walked cleanly at, ascending. Two or more, by construction.</summary>
    public IReadOnlyList<int> CandidateGenerations { get; }
}
