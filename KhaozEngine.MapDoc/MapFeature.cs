using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Base of the serializable terrain-feature DTOs. Open set: built-ins (lake/flatten/ridge/rim) register
/// in <see cref="MapDocRegistry.CreateDefault"/>, and games register custom types with their own discriminator.
/// Serialized polymorphically via a "type" property (handled by the registry-driven converter, so the set stays
/// extensible at runtime, unlike the closed <see cref="MapShapeDoc"/> hierarchy).</summary>
public abstract class MapFeature
{
    /// <summary>The JSON discriminator this DTO serializes under. Must match its registry registration.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>DTO for <see cref="LakeFeature"/>.</summary>
public sealed class LakeFeatureDoc : MapFeature
{
    public override string Type => "lake";
    public float CenterX { get; set; }
    public float CenterZ { get; set; }
    public float Radius { get; set; }
    public float Depth { get; set; }
    public float InnerFraction { get; set; } = 0.45f;
    public float OuterFraction { get; set; } = 1.30f;

    internal LakeFeature Build() => new(CenterX, CenterZ, Radius, Depth, InnerFraction, OuterFraction);
}

/// <summary>DTO for <see cref="FlattenFeature"/>.</summary>
public sealed class FlattenFeatureDoc : MapFeature
{
    public override string Type => "flatten";
    public float CenterX { get; set; }
    public float CenterZ { get; set; }
    public float Radius { get; set; }
    public float TargetHeight { get; set; }
    public float Blend { get; set; } = 0.4f;

    internal FlattenFeature Build() => new(CenterX, CenterZ, Radius, TargetHeight, Blend);
}

/// <summary>DTO for <see cref="RidgeFeature"/>. <see cref="PassWidth"/> defaults to 0 (no pass, a solid wall): a
/// bare ridge with no pass configured must not carve a dip anywhere along its own crest, and 0 is the documented
/// <see cref="RidgeFeature"/> no-pass sentinel. Set a positive <see cref="PassWidth"/> to open a gated corridor
/// at <see cref="PassAlong"/>.</summary>
public sealed class RidgeFeatureDoc : MapFeature
{
    public override string Type => "ridge";
    public float PointX { get; set; }
    public float PointZ { get; set; }
    public float DirectionX { get; set; } = 1f;
    public float DirectionZ { get; set; }
    public float Height { get; set; }
    public float Width { get; set; } = 1f;
    public float PassAlong { get; set; }
    public float PassWidth { get; set; }

    internal RidgeFeature Build() =>
        new(new Vector2(PointX, PointZ), new Vector2(DirectionX, DirectionZ), Height, Width, PassAlong, PassWidth);
}

/// <summary>DTO for one <see cref="RimPass"/> corridor.</summary>
public sealed class RimPassDoc
{
    public float AngleRadians { get; set; }
    public float HalfWidth { get; set; }
    public float Falloff { get; set; } = 1f;
}

/// <summary>DTO for <see cref="RimFeature"/>.</summary>
public sealed class RimFeatureDoc : MapFeature
{
    public override string Type => "rim";
    public float CenterX { get; set; }
    public float CenterZ { get; set; }
    public float InnerRadius { get; set; }
    public float OuterRadius { get; set; }
    public float WallHeight { get; set; }
    public float Ruggedness { get; set; } = 0.25f;
    public int Seed { get; set; } = 1;
    public float CrestFrequency { get; set; } = 0.05f;
    public List<RimPassDoc> Passes { get; set; } = new();

    internal RimFeature Build()
    {
        var passes = new RimPass[Passes.Count];
        for (int i = 0; i < Passes.Count; i++)
            passes[i] = new RimPass(Passes[i].AngleRadians, Passes[i].HalfWidth, Passes[i].Falloff);
        return new RimFeature(new Vector2(CenterX, CenterZ), InnerRadius, OuterRadius, WallHeight,
            Ruggedness, passes, Seed, CrestFrequency);
    }
}
