using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Support;

/// <summary>The one closed support-binding rule shared by marker payload 2 and foliage. A Surface binding names only a
/// nonblank surfaceId. A Space binding names a nonblank spaceId, no surfaceId, a finite referenceY and finite nonnegative
/// searchBelow and searchAbove. The placement binding keeps its released nullable rule and is not covered here.</summary>
internal static class MapSupportBindingRules
{
    /// <summary>Adds each violated rule to <paramref name="errors"/>, every message prefixed with <paramref name="at"/>.</summary>
    internal static void Validate(MapSupportBinding binding, string at, List<string> errors)
    {
        switch (binding.Kind)
        {
            case MapSupportBindingKind.Surface:
                if (string.IsNullOrWhiteSpace(binding.SurfaceId) || binding.SpaceId is not null ||
                    binding.ReferenceY is not null || binding.SearchBelow is not null || binding.SearchAbove is not null)
                    errors.Add(at + "a Surface binding names only a surfaceId.");
                break;
            case MapSupportBindingKind.Space:
                if (string.IsNullOrWhiteSpace(binding.SpaceId) || binding.SurfaceId is not null)
                    errors.Add(at + "a Space binding names a spaceId and no surfaceId.");
                if (binding.ReferenceY is not { } y || !float.IsFinite(y) ||
                    binding.SearchBelow is not { } below || !float.IsFinite(below) || below < 0 ||
                    binding.SearchAbove is not { } above || !float.IsFinite(above) || above < 0)
                    errors.Add(at + "a Space binding requires a finite referenceY and finite nonnegative searchBelow and searchAbove.");
                break;
            default:
                errors.Add(at + "unsupported support binding kind.");
                break;
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
}
