using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc.Prefabs;

public enum MapLightShadowExclusionKind { None, NearSphere, FixtureBox }

/// <summary>An optional authored shadow exclusion. Sphere radius and box margin are world metres.
/// Box corners are metres in the owning placement's local frame, independent of asset source units.</summary>
public sealed record MapLightShadowExclusion(MapLightShadowExclusionKind Kind)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Radius { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Vector3? Min { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Vector3? Max { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Margin { get; init; }

    /// <summary>Refuses an unsupported kind, another kind's members, invalid clearance or a box without an owner.</summary>
    public void Validate(bool hasOwner)
    {
        switch (Kind)
        {
            case MapLightShadowExclusionKind.None:
                if (Radius is not null || Min is not null || Max is not null || Margin is not null)
                    Refuse(MapDocumentRefusal.AuthoredValueMismatch, "None carries no clearance fields");
                break;
            case MapLightShadowExclusionKind.NearSphere:
                if (Radius is not { } radius || !float.IsFinite(radius) || radius < 0 || Min is not null || Max is not null || Margin is not null)
                    Refuse(Radius is null ? MapDocumentRefusal.SchemaValueRequired : !float.IsFinite(Radius.Value) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange, "NearSphere requires only a finite nonnegative radius");
                break;
            case MapLightShadowExclusionKind.FixtureBox:
                if (!hasOwner) Refuse(MapDocumentRefusal.ReferenceMissing, "FixtureBox requires an owner placement");
                if (Radius is not null || Min is not { } min || Max is not { } max || Margin is not { } margin ||
                    !Finite(min) || !Finite(max) || min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z || !float.IsFinite(margin) || margin < 0)
                    Refuse(MapDocumentRefusal.BoundsInvalid, "FixtureBox requires finite ordered corners and a finite nonnegative margin");
                break;
            default:
                Refuse(MapDocumentRefusal.SchemaEnumUnsupported, "unsupported kind");
                break;
        }
    }

    internal string Digest() => MapPrefabJson.Digest("kemap/light-shadow-exclusion/1", this);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    void Refuse(MapDocumentRefusal reason, string detail) => throw new MapDocumentException(reason, "Invalid light shadow exclusion: " + detail + ".")
    { FieldPath = "ShadowExclusion", EnumType = typeof(MapLightShadowExclusionKind), ActualInteger = (int)Kind };
}
