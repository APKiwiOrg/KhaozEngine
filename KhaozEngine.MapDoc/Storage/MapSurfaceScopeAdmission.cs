using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Selects the first failed scope rule without repeating reads of caller-owned collections.</summary>
internal static class MapSurfaceScopeAdmission
{
    internal static void Validate(Vector2 min, Vector2 max, float? minY, float? maxY,
        IReadOnlyList<MapSurfaceRole> roles, IReadOnlyList<string>? spaces, MapQueryLimits limits)
    {
        MapSurfaceRole? invalidRole = null;
        string? invalidSpace = null;
        bool invalidSpaceFound = false;
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y) ||
            min.X > max.X || min.Y > max.Y ||
            (minY is { } low && !float.IsFinite(low)) || (maxY is { } high && !float.IsFinite(high)) || minY > maxY ||
            roles is null || roles.Any(r =>
            {
                if (Enum.IsDefined(r)) return false;
                invalidRole = r;
                return true;
            }) || spaces?.Any(s =>
            {
                if (!string.IsNullOrWhiteSpace(s)) return false;
                invalidSpaceFound = true;
                invalidSpace = s;
                return true;
            }) == true || limits is null ||
            limits.MaxCandidatePatches < 0 || limits.MaxPageReads < 0 || limits.MaxInspectedFaces < 0 ||
            limits.MaxSupportIntersections < 0 || limits.MaxRecordReads < 0 || limits.MaxRecordDepth < 0 ||
            limits.Refinement is { } reads && (reads.MaxSourceFacesPerBound < 0 || reads.MaxPairChecks < 0 ||
                reads.MaxRefinementFaces < 0 || reads.MaxRefinementVertices < 0 || reads.MaxContextPatches < 0 || reads.MaxContextFaces < 0))
            throw Refusal();

        MapSurfaceScopeArgumentException Refusal()
        {
            const string message = "invalid surface scope bounds or limits";
            foreach (var (field, value) in new[] { ("LocalMin.X", min.X), ("LocalMin.Y", min.Y), ("LocalMax.X", max.X), ("LocalMax.Y", max.Y) })
                if (!float.IsFinite(value)) return Number(MapDocumentRefusal.ValueNonfinite, field, value);
            if (min.X > max.X) return Number(MapDocumentRefusal.BoundsInvalid, "LocalMax.X", max.X, min.X);
            if (min.Y > max.Y) return Number(MapDocumentRefusal.BoundsInvalid, "LocalMax.Y", max.Y, min.Y);
            if (minY is { } low && !float.IsFinite(low)) return Number(MapDocumentRefusal.ValueNonfinite, "MinY", low);
            if (maxY is { } high && !float.IsFinite(high)) return Number(MapDocumentRefusal.ValueNonfinite, "MaxY", high);
            if (minY > maxY) return Number(MapDocumentRefusal.BoundsInvalid, "MaxY", maxY!.Value, minY);
            if (roles is null) return new(MapDocumentRefusal.SchemaValueRequired, message) { FieldPath = "Roles" };
            if (invalidRole is { } role) return new(MapDocumentRefusal.SchemaEnumUnsupported, message)
            { FieldPath = "Roles", ActualInteger = (long)role, EnumType = typeof(MapSurfaceRole) };
            if (invalidSpaceFound) return new(MapDocumentRefusal.SchemaValueRequired, message)
            { FieldPath = "SpaceIds", ActualText = invalidSpace };
            if (limits is null) return new(MapDocumentRefusal.SchemaValueRequired, message) { FieldPath = "Limits" };
            var (limit, actual) = new[] { ("MaxCandidatePatches", limits.MaxCandidatePatches), ("MaxPageReads", limits.MaxPageReads),
                ("MaxInspectedFaces", limits.MaxInspectedFaces), ("MaxSupportIntersections", limits.MaxSupportIntersections),
                ("MaxRecordReads", limits.MaxRecordReads), ("MaxRecordDepth", limits.MaxRecordDepth) }.FirstOrDefault(v => v.Item2 < 0);
            if (limit is null && limits.Refinement is { } refinement)
                (limit, actual) = new[] { ("Refinement.MaxSourceFacesPerBound", refinement.MaxSourceFacesPerBound),
                    ("Refinement.MaxPairChecks", refinement.MaxPairChecks), ("Refinement.MaxRefinementFaces", refinement.MaxRefinementFaces),
                    ("Refinement.MaxRefinementVertices", refinement.MaxRefinementVertices), ("Refinement.MaxContextPatches", refinement.MaxContextPatches),
                    ("Refinement.MaxContextFaces", refinement.MaxContextFaces) }.First(v => v.Item2 < 0);
            return new(MapDocumentRefusal.ValueOutsideRange, message)
            { FieldPath = "Limits." + limit, ActualInteger = actual, MinimumInteger = 0 };

            static MapSurfaceScopeArgumentException Number(MapDocumentRefusal reason, string field, float value, float? minimum = null) =>
                new(reason, message) { FieldPath = field, ActualNumber = value, MinimumNumber = minimum };
        }
    }
}
