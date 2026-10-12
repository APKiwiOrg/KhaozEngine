using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>A payload-1 semantic room. Its local prism intersects the canonical interval of its authored parent.</summary>
public sealed record MapPrismDomain(string Id, int PayloadVersion, MapRecordRef Space, MapRecordRef Parent,
    string InstanceId, string StoreyId, MapWaterDomainDoc Footprint, float LowerY, float UpperY) : MapTopologyRecord(Id);

/// <summary>An authored boundary owner. Geometry changes its semantic digest, not its logical identity.</summary>
public sealed record MapOccupiedBoundary(string OwnerId, string Role, string SemanticDigest);

/// <summary>A stable occupied slab identity and the provenance of both boundaries.</summary>
public sealed record MapOccupiedFragment(string Id, string SpaceId, MapOccupiedBoundary Lower, MapOccupiedBoundary Upper);
