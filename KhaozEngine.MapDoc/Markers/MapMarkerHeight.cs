using System;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>The closed payload-2 marker height authority.</summary>
public enum MapMarkerHeightMode { Explicit, Support }

/// <summary>A command or adoption height choice. Exactly one of an explicit Y and a support binding is set.</summary>
public sealed record MapMarkerHeight(float? ExplicitY, MapSupportBinding? Binding);

/// <summary>The one canonical payload-2 encoding of a height choice.</summary>
public static class MapMarkerHeights
{
    /// <summary>Returns the marker as payload 2 with the chosen height. Explicit writes its Y into Position.Y and no
    /// binding. Support writes a detached binding and Position.Y exactly +0. Every other member is kept.</summary>
    public static MapLocalMarkerDoc Apply(MapLocalMarkerDoc marker, MapMarkerHeight height)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(height);
        if ((height.ExplicitY is null) == (height.Binding is null))
            throw new MapDocumentException($"Marker '{marker.Id}' requires exactly one explicit Y or support binding.");
        MapLocalMarkerDoc result = height.ExplicitY is { } y
            ? marker with { PayloadVersion = 2, HeightMode = MapMarkerHeightMode.Explicit, SupportBinding = null, Position = marker.Position with { Y = y } }
            : marker with { PayloadVersion = 2, HeightMode = MapMarkerHeightMode.Support, SupportBinding = height.Binding! with { }, Position = marker.Position with { Y = 0f } };
        MapMarkerValidation.Require(result);
        return result;
    }
}
