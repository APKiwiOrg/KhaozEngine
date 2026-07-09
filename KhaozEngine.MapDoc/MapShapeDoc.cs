using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>A serializable XZ shape used by exclusions, scatter overrides, and regions. Closed set with a JSON
/// "type" discriminator (disc/rect/polygon). <see cref="ToArea"/> converts to the runtime
/// <see cref="IArea2D"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DiscShapeDoc), "disc")]
[JsonDerivedType(typeof(RectShapeDoc), "rect")]
[JsonDerivedType(typeof(PolygonShapeDoc), "polygon")]
public abstract class MapShapeDoc
{
    public abstract IArea2D ToArea();
}

/// <summary>A disc shape (radius inclusive).</summary>
public sealed class DiscShapeDoc : MapShapeDoc
{
    public float CenterX { get; set; }
    public float CenterZ { get; set; }
    public float Radius { get; set; }
    public override IArea2D ToArea() => new DiscArea2D(CenterX, CenterZ, Radius);
}

/// <summary>An axis-aligned rectangle shape (edges inclusive).</summary>
public sealed class RectShapeDoc : MapShapeDoc
{
    public float MinX { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxZ { get; set; }
    public override IArea2D ToArea() => new BoxArea2D(MinX, MinZ, MaxX, MaxZ);
}

/// <summary>A simple polygon shape. Each point is a two-element [x, z] array in JSON.</summary>
public sealed class PolygonShapeDoc : MapShapeDoc
{
    public List<float[]> Points { get; set; } = new();

    public override IArea2D ToArea()
    {
        var pts = new Vector2[Points.Count];
        for (int i = 0; i < Points.Count; i++)
        {
            float[] p = Points[i];
            pts[i] = new Vector2(p.Length > 0 ? p[0] : 0f, p.Length > 1 ? p[1] : 0f);
        }
        return new PolygonArea2D(pts);
    }
}
