using System;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Scene;

[Flags]
public enum MapSceneChannels { Terrain = 1, Placements = 2, Water = 4, Annotations = 8, Foliage = 16, Rooms = 32, All = 63 }

/// <summary>Independent physical acquisition and draw domains, with an explicit caller motion allowance.</summary>
public sealed record MapSceneScope(MapSurfaceScope Surfaces, MapBox3 DrawBounds, MapSceneChannels Channels, double MotionPaddingMetres = 0)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Surfaces);
        Surfaces.Validate();
        var b = DrawBounds;
        if (!double.IsFinite(MotionPaddingMetres) || MotionPaddingMetres < 0 || (Channels & ~MapSceneChannels.All) != 0 ||
            !double.IsFinite(b.MinX) || !double.IsFinite(b.MinY) || !double.IsFinite(b.MinZ) ||
            !double.IsFinite(b.MaxX) || !double.IsFinite(b.MaxY) || !double.IsFinite(b.MaxZ) ||
            b.MinX > b.MaxX || b.MinY > b.MaxY || b.MinZ > b.MaxZ)
            throw new ArgumentException("Invalid scene scope.");
        var expanded = DiscoveryBounds;
        if (!double.IsFinite(expanded.MinX) || !double.IsFinite(expanded.MinZ) ||
            !double.IsFinite(expanded.MaxX) || !double.IsFinite(expanded.MaxZ))
            throw new ArgumentException("Scene motion allowance is not representable.");
    }

    internal MapBox3 DiscoveryBounds => new(DrawBounds.MinX - MotionPaddingMetres, double.NegativeInfinity,
        DrawBounds.MinZ - MotionPaddingMetres, DrawBounds.MaxX + MotionPaddingMetres, double.PositiveInfinity,
        DrawBounds.MaxZ + MotionPaddingMetres);

    public string Digest
    {
        get
        {
            Validate();
            string surfaces = Surfaces.Digest;
            return MapCanonical.HashHex(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/scene-scope/1"); w.WriteStringValue(surfaces);
                foreach (double value in new[] { DrawBounds.MinX, DrawBounds.MinY, DrawBounds.MinZ, DrawBounds.MaxX,
                    DrawBounds.MaxY, DrawBounds.MaxZ, MotionPaddingMetres }) w.WriteNumberValue(BitConverter.DoubleToInt64Bits(value));
                w.WriteNumberValue((int)Channels); w.WriteEndArray();
            });
        }
    }
}
