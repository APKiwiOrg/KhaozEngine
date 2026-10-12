using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc.Surfaces;

public enum MapSurfaceRole : byte { SupportFloor, Ceiling, PaintOverride }
public enum MapPresencePolicy : byte { Native, LegacyTileWorld }
public sealed record MapRecordRef(string Id, MapPatchKey Anchor);
public sealed record MapIndoorSpan(string Id, MapRecordRef ParentSpace, int LowerOffsetUnits,
    int UpperOffsetUnits, IReadOnlyList<string> DomainTags);
public sealed record MapSurfaceRef(string Id, MapLatticeFrame Frame, MapSurfaceRole Role,
    MapPresencePolicy PresencePolicy, string? PaintTargetSurfaceId, MapIndoorSpan? IndoorSpan, string SemanticSha256)
{
    /// <summary>Higher values own overlapping paint. Equal values require identical material coverage.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PaintPriority { get; init; }
}
