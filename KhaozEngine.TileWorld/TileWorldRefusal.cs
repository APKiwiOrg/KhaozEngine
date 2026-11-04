namespace KhaozEngine.TileWorld;

/// <summary>Stable reasons authored tile-world content or an operation on it is refused.</summary>
public enum TileWorldRefusal
{
    // Region residency.
    /// <summary>Refuses an operation requiring a region that does not exist.</summary>
    RegionMissing,
    /// <summary>Refuses an operation requiring a known region that is not loaded.</summary>
    RegionNotLoaded,
    /// <summary>Refuses unloading a region with unsaved changes.</summary>
    RegionDirty,

    // Manifest regions.
    /// <summary>Refuses a manifest region list that repeats a region coordinate.</summary>
    ManifestRegionDuplicate,
    /// <summary>Refuses a manifest region entry whose hash is null.</summary>
    ManifestRegionHashMissing,
    /// <summary>Refuses region bytes whose hash differs from the manifest's stored hash.</summary>
    RegionHashMismatch,
}
