using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Catalog;

public readonly record struct ChunkRecord(
    ushort TypeId,
    int ChunkIndex,
    byte Visibility,
    string Hash,
    int StoredBytes,
    int UncompressedBytes,
    int RowCount);

public readonly record struct TextChunkRecord(
    string Tag,
    string Hash,
    int StoredBytes,
    int UncompressedBytes,
    int EntryCount);

/// <summary>Everything one publish produced, which is what the pack size budgets are read off.</summary>
public sealed class PublishOutcome
{
    public required int VersionNumber { get; init; }
    public required ContentManifest ServerManifest { get; init; }
    public required ContentManifest ClientManifest { get; init; }
    public required string ServerManifestHash { get; init; }
    public required string ClientManifestHash { get; init; }
    public required int ServerManifestBytes { get; init; }
    public required int ClientManifestBytes { get; init; }
    public required Dictionary<(ushort TypeId, int ChunkIndex), ChunkRecord> Chunks { get; init; }
    public required List<TextChunkRecord> TextChunks { get; init; }
    public required string RuleChunkHash { get; init; }
    public required int RuleChunkStoredBytes { get; init; }

    /// <summary>Bytes the content key cost across every row, so P1 can be read with and without it.</summary>
    public long KeyBodyBytes { get; init; }

    public long ServerChunkStoredBytes { get; set; }
    public long ClientChunkStoredBytes { get; set; }
    public long ServerUncompressedBytes { get; set; }
    public long ClientUncompressedBytes { get; set; }
    public long TextStoredBytes { get; set; }
    public long TextUncompressedBytes { get; set; }
    public long TotalRows { get; set; }
    public long ItemRowBodyBytes { get; set; }
    public long ItemRowCount { get; set; }
    public int LargestRowBytes { get; set; }
    public int LargestChunkUncompressedBytes { get; set; }
    public double EncodeMs { get; set; }
    public double TextMs { get; set; }
    public double ManifestMs { get; set; }
    public double WriteMs { get; set; }
    public double TotalMs { get; set; }

    /// <summary>The server pack as STORED: every chunk, every text shard, the rule chunk and the manifest.</summary>
    public long ServerStoredBytes =>
        ServerChunkStoredBytes + TextStoredBytes + RuleChunkStoredBytes + ServerManifestBytes;

    /// <summary>The client pack as STORED, which is smaller by exactly the ServerOnly families.</summary>
    public long ClientStoredBytes =>
        ClientChunkStoredBytes + TextStoredBytes + RuleChunkStoredBytes + ClientManifestBytes;

    public double MeanItemRowBytes => ItemRowCount == 0 ? 0 : (double)ItemRowBodyBytes / ItemRowCount;
}

/// <summary>What a one-item edit republish cost and what a client has to refetch because of it.</summary>
public sealed class EditOutcome
{
    public required int VersionNumber { get; init; }
    public required int ChunkSlots { get; init; }
    public required int AffectedChunkCount { get; init; }
    public required long ChunkBytesWritten { get; init; }
    public required int ClientManifestBytes { get; init; }
    public required int ServerManifestBytes { get; init; }
    public required double PublishMs { get; init; }
    public required double ValidateMs { get; init; }
    public required double EncodeMs { get; init; }
    public required double WriteMs { get; init; }
    public required double ManifestMs { get; init; }

    /// <summary>Budget P6: the chunk the client refetches plus the manifest, and nothing else.</summary>
    public long ClientDownloadBytes => ChunkBytesWritten + ClientManifestBytes;
}
