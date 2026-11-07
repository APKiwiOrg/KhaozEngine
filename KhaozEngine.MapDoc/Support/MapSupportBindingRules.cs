using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Support;

/// <summary>The one closed support-binding rule shared by marker payload 2 and foliage. A Surface binding names only a
/// nonblank surfaceId. A Space binding names a nonblank spaceId, no surfaceId, a finite referenceY and finite nonnegative
/// searchBelow and searchAbove. The placement binding keeps its released nullable rule and is not covered here.</summary>
internal static class MapSupportBindingRules
{
    /// <summary>Adds each violated rule to <paramref name="errors"/>, every message prefixed with <paramref name="at"/>,
    /// and a matching typed finding when requested.</summary>
    internal static void Validate(MapSupportBinding binding, string at, List<string> errors,
        List<MapDocumentFinding>? findings = null, string? markerId = null)
    {
        switch (binding.Kind)
        {
            case MapSupportBindingKind.Surface:
                if (string.IsNullOrWhiteSpace(binding.SurfaceId) || binding.SpaceId is not null ||
                    binding.ReferenceY is not null || binding.SearchBelow is not null || binding.SearchAbove is not null)
                    Add(string.IsNullOrWhiteSpace(binding.SurfaceId) ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.AuthoredOwnershipMismatch,
                        string.IsNullOrWhiteSpace(binding.SurfaceId) ? "surfaceId" : binding.SpaceId is not null ? "spaceId" :
                        binding.ReferenceY is not null ? "referenceY" : binding.SearchBelow is not null ? "searchBelow" : "searchAbove",
                        "a Surface binding names only a surfaceId.");
                break;
            case MapSupportBindingKind.Space:
                if (string.IsNullOrWhiteSpace(binding.SpaceId) || binding.SurfaceId is not null)
                    Add(string.IsNullOrWhiteSpace(binding.SpaceId) ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.AuthoredOwnershipMismatch,
                        string.IsNullOrWhiteSpace(binding.SpaceId) ? "spaceId" : "surfaceId", "a Space binding names a spaceId and no surfaceId.");
                if (binding.ReferenceY is not { } y || !float.IsFinite(y) ||
                    binding.SearchBelow is not { } below || !float.IsFinite(below) || below < 0 ||
                    binding.SearchAbove is not { } above || !float.IsFinite(above) || above < 0)
                {
                    (string field, float? value) = binding.ReferenceY is not { } reference || !float.IsFinite(reference)
                        ? ("referenceY", binding.ReferenceY)
                        : binding.SearchBelow is not { } lower || !float.IsFinite(lower) || lower < 0
                            ? ("searchBelow", binding.SearchBelow) : ("searchAbove", binding.SearchAbove);
                    Add(value is null ? MapDocumentRefusal.SchemaValueRequired : !float.IsFinite(value.Value)
                            ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                        field, "a Space binding requires a finite referenceY and finite nonnegative searchBelow and searchAbove.", value);
                }
                break;
            default:
                Add(MapDocumentRefusal.SchemaEnumUnsupported, "kind", "unsupported support binding kind.");
                break;
        }

        void Add(MapDocumentRefusal reason, string field, string message, float? actual = null)
        {
            errors.Add(at + message);
            findings?.Add(new(reason, at + message)
            {
                MarkerId = markerId, SubjectKind = markerId is null ? "support binding" : "marker",
                FieldPath = "$.supportBinding." + field, SurfaceId = binding.SurfaceId, SpaceId = binding.SpaceId,
                ActualNumber = actual is { } value && float.IsFinite(value) ? value : null,
                MinimumNumber = reason == MapDocumentRefusal.ValueOutsideRange ? 0 : null,
                EnumType = reason == MapDocumentRefusal.SchemaEnumUnsupported ? typeof(MapSupportBindingKind) : null,
                ActualInteger = reason == MapDocumentRefusal.SchemaEnumUnsupported ? (long)binding.Kind : null,
            });
        }
    }

    /// <summary>Writes the canonical object: kind first, then only that kind's members. Returns false, writing nothing,
    /// when the binding carries members of another kind or lacks one of its own, so each caller refuses in its own
    /// terms.</summary>
    internal static bool TryWrite(Utf8JsonWriter w, MapSupportBinding value)
    {
        switch (value.Kind)
        {
            case MapSupportBindingKind.Surface when value.SurfaceId is { } surface && value.SpaceId is null &&
                value.ReferenceY is null && value.SearchBelow is null && value.SearchAbove is null:
                w.WriteStartObject();
                w.WriteString("kind", "Surface");
                w.WriteString("surfaceId", surface);
                w.WriteEndObject();
                return true;
            case MapSupportBindingKind.Space when value.SpaceId is { } space && value.SurfaceId is null &&
                value.ReferenceY is { } y && value.SearchBelow is { } below && value.SearchAbove is { } above:
                w.WriteStartObject();
                w.WriteString("kind", "Space");
                w.WriteString("spaceId", space);
                w.WriteNumber("referenceY", y);
                w.WriteNumber("searchBelow", below);
                w.WriteNumber("searchAbove", above);
                w.WriteEndObject();
                return true;
            default:
                return false;
        }
    }

    // Describes only the shape rejected by TryWrite. Its released writer admits blank names and signed ranges,
    // so the stricter semantic validator above cannot classify this refusal without changing admission.
    internal static MapDocumentFinding WriteRefusal(MapSupportBinding value, string message)
    {
        (MapDocumentRefusal reason, string field) = value.Kind switch
        {
            MapSupportBindingKind.Surface when value.SurfaceId is null => (MapDocumentRefusal.SchemaValueRequired, "surfaceId"),
            MapSupportBindingKind.Surface => (MapDocumentRefusal.AuthoredOwnershipMismatch,
                value.SpaceId is not null ? "spaceId" : value.ReferenceY is not null ? "referenceY" :
                value.SearchBelow is not null ? "searchBelow" : "searchAbove"),
            MapSupportBindingKind.Space when value.SpaceId is null => (MapDocumentRefusal.SchemaValueRequired, "spaceId"),
            MapSupportBindingKind.Space when value.SurfaceId is not null => (MapDocumentRefusal.AuthoredOwnershipMismatch, "surfaceId"),
            MapSupportBindingKind.Space => (MapDocumentRefusal.SchemaValueRequired,
                value.ReferenceY is null ? "referenceY" : value.SearchBelow is null ? "searchBelow" : "searchAbove"),
            _ => (MapDocumentRefusal.SchemaEnumUnsupported, "kind"),
        };
        return new(reason, message)
        {
            FieldPath = "$." + field, SurfaceId = value.SurfaceId, SpaceId = value.SpaceId,
            EnumType = reason == MapDocumentRefusal.SchemaEnumUnsupported ? typeof(MapSupportBindingKind) : null,
            ActualInteger = reason == MapDocumentRefusal.SchemaEnumUnsupported ? (long)value.Kind : null,
        };
    }
}
