using System;
using System.Text.Json;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Includes the coordinate origin in the identity of an anchor-relative prepared payload.</summary>
internal static class MapAnchorRelativeDigest
{
    internal static string Hash(MapSubmissionAnchor anchor, Action<Utf8JsonWriter> write) => MapPlacementDigest.Hash(w =>
    {
        w.WriteStartArray();
        w.WriteNumberValue(anchor.X); w.WriteNumberValue(anchor.Y); w.WriteNumberValue(anchor.Z);
        write(w);
        w.WriteEndArray();
    });
}
