using System.Collections.Generic;

namespace KhaozEngine.WorldMap;

/// <summary>Requests intersecting footprints with optional size and ordinal any-of tag filters.</summary>
/// <param name="Area">The map-space area intersected by each footprint's oriented rectangle.</param>
/// <param name="MinExtentMetres">The minimum full larger extent in metres, or zero to include every size.</param>
/// <param name="AnyTags">At least one required tag, or null to include every tag set. An empty collection matches none.</param>
/// <remarks>
/// Sources must return every intersecting footprint whose twice-largest half extent meets the minimum
/// and whose tags include an ordinal match from AnyTags when it is not null. Sources may return extra
/// footprints that fail these filters. Callers retain their own exact matching and size checks.
/// </remarks>
public readonly record struct WorldMapFootprintQuery(
    MapRect Area, float MinExtentMetres = 0f, IReadOnlyCollection<string>? AnyTags = null);
