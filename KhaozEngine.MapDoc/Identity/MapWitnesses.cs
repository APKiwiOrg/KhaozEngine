using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Identity;

public sealed record MapUnavailable(string What, MapPatchKey Key, MapPatchStatus Status);

/// <summary>Immutable acquisition-time coverage, constructed only by the scoped-view factory.</summary>
public sealed class MapCoverageWitness
{
    internal MapCoverageWitness(string snapshotId, string scopeDigest, WorldFrame frame, IEnumerable<string> surfaceIds,
        IEnumerable<KeyValuePair<MapPatchKey, string>> present, IEnumerable<MapCoveredRange> knownEmpty,
        IEnumerable<MapRecordRef> records, IEnumerable<MapUnavailable> unavailable, bool complete)
    {
        SnapshotId = snapshotId;
        ScopeDigest = scopeDigest;
        Frame = frame;
        SurfaceIds = Array.AsReadOnly(surfaceIds.ToArray());
        Present = Array.AsReadOnly(present.ToArray());
        KnownEmpty = Array.AsReadOnly(knownEmpty.ToArray());
        Records = Array.AsReadOnly(records.ToArray());
        Unavailable = Array.AsReadOnly(unavailable.ToArray());
        Complete = complete;
    }
    public string SnapshotId { get; }
    public string ScopeDigest { get; }
    public WorldFrame Frame { get; }
    public IReadOnlyList<string> SurfaceIds { get; }
    public IReadOnlyList<KeyValuePair<MapPatchKey, string>> Present { get; }
    public IReadOnlyList<MapCoveredRange> KnownEmpty { get; }
    public IReadOnlyList<MapRecordRef> Records { get; }
    public IReadOnlyList<MapUnavailable> Unavailable { get; }
    public bool Complete { get; }
}

/// <summary>Certifies the pinned acquisition, rather than current producer residency.</summary>
public sealed class MapReadWitness
{
    internal MapReadWitness(string snapshotId, string scopedDigest, WorldFrame frame, int queryPolicyVersion, bool complete)
    {
        SnapshotId = snapshotId;
        ScopedDigest = scopedDigest;
        Frame = frame;
        QueryPolicyVersion = queryPolicyVersion;
        Complete = complete;
    }
    public string SnapshotId { get; }
    public string ScopedDigest { get; }
    public WorldFrame Frame { get; }
    public int QueryPolicyVersion { get; }
    public bool Complete { get; }
}
