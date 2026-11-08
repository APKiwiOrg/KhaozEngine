using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>The closed local marker payload rules, shared by prefab files, prefab children and document
/// validation. Payload 1 carries none of the payload-2 members. Payload 2 requires a height mode: Explicit forbids a
/// binding and keeps a finite Position.Y, Support requires a binding and Position.Y exactly +0.</summary>
public static class MapMarkerValidation
{
    public static IReadOnlyList<string> Validate(MapLocalMarkerDoc marker) => Validate(marker, null);

    internal static IReadOnlyList<string> Validate(MapLocalMarkerDoc marker, List<MapDocumentFinding>? findings)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var errors = new List<string>();
        string at = $"Marker '{marker.Id}': ";
        if (string.IsNullOrWhiteSpace(marker.Id) || string.IsNullOrWhiteSpace(marker.Name) || string.IsNullOrWhiteSpace(marker.Role))
            Add(MapDocumentRefusal.SchemaValueRequired,
                string.IsNullOrWhiteSpace(marker.Id) ? "$.id" : string.IsNullOrWhiteSpace(marker.Name) ? "$.name" : "$.role",
                "a nonempty ID, name and role are required.");
        if (!float.IsFinite(marker.Position.X) || !float.IsFinite(marker.Position.Z) || !float.IsFinite(marker.YawRadians))
            Add(MapDocumentRefusal.ValueNonfinite,
                !float.IsFinite(marker.Position.X) ? "$.position.x" : !float.IsFinite(marker.Position.Z) ? "$.position.z" : "$.yawRadians",
                "position X and Z and yaw must be finite.");
        if (marker.Tags is null || marker.Tags.Any(t => t is null))
            Add(MapDocumentRefusal.SchemaValueRequired, marker.Tags is null ? "$.tags" : $"$.tags[{Enumerable.Range(0, marker.Tags.Count).First(i => marker.Tags[i] is null)}]",
                "tags must not be null.");
        switch (marker.PayloadVersion)
        {
            case 1:
                if (marker.HeightMode is not null || marker.SupportBinding is not null || marker.ArchetypeId is not null)
                    Add(MapDocumentRefusal.AuthoredOwnershipMismatch,
                        marker.HeightMode is not null ? "$.heightMode" : marker.SupportBinding is not null ? "$.supportBinding" : "$.archetypeId",
                        "payload 1 carries no heightMode, supportBinding or archetypeId.");
                if (!float.IsFinite(marker.Position.Y)) Add(MapDocumentRefusal.ValueNonfinite, "$.position.y", "the explicit height must be finite.");
                break;
            case 2:
                // Blank like the ID, name and role rules above and the released spawn archetype rule.
                if (marker.ArchetypeId is not null && string.IsNullOrWhiteSpace(marker.ArchetypeId))
                    Add(MapDocumentRefusal.SchemaValueRequired, "$.archetypeId", "archetypeId must be nonblank when present.");
                Height();
                break;
            default:
                Add(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.payloadVersion", "unsupported local marker payload version.");
                break;
        }
        return errors;

        void Add(MapDocumentRefusal reason, string field, string message, double? actual = null)
        {
            errors.Add(at + message);
            findings?.Add(new(reason, at + message)
            {
                MarkerId = marker.Id, SubjectKind = "marker", FieldPath = field, ActualNumber = actual,
                ActualVersion = reason == MapDocumentRefusal.SchemaPayloadVersionUnsupported ? marker.PayloadVersion : null,
                SupportedVersions = reason == MapDocumentRefusal.SchemaPayloadVersionUnsupported ? [1, 2] : null,
                EnumType = reason == MapDocumentRefusal.SchemaEnumUnsupported ? typeof(MapMarkerHeightMode) : null,
                ActualInteger = reason == MapDocumentRefusal.SchemaEnumUnsupported ? (long?)marker.HeightMode : null,
            });
        }

        void Height()
        {
            switch (marker.HeightMode)
            {
                case MapMarkerHeightMode.Explicit:
                    if (marker.SupportBinding is not null) Add(MapDocumentRefusal.AuthoredOwnershipMismatch, "$.supportBinding", "an Explicit height has no support binding.");
                    if (!float.IsFinite(marker.Position.Y)) Add(MapDocumentRefusal.ValueNonfinite, "$.position.y", "the explicit height must be finite.");
                    break;
                case MapMarkerHeightMode.Support:
                    // The one canonical encoding. Negative zero is a second spelling and refuses.
                    if (BitConverter.SingleToUInt32Bits(marker.Position.Y) != 0)
                        Add(float.IsFinite(marker.Position.Y) ? MapDocumentRefusal.MarkerHeightChoiceInvalid : MapDocumentRefusal.ValueNonfinite,
                            "$.position.y", "a Support height stores Position.Y exactly +0.", float.IsFinite(marker.Position.Y) ? marker.Position.Y : null);
                    if (marker.SupportBinding is not { } binding) Add(MapDocumentRefusal.SchemaValueRequired, "$.supportBinding", "a Support height requires a support binding.");
                    else MapSupportBindingRules.Validate(binding, at, errors, findings, marker.Id);
                    break;
                default:
                    Add(marker.HeightMode is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaEnumUnsupported,
                        "$.heightMode", "payload 2 requires heightMode Explicit or Support.");
                    break;
            }
        }
    }

    internal static void Require(MapLocalMarkerDoc marker)
    {
        var findings = new List<MapDocumentFinding>();
        IReadOnlyList<string> errors = Validate(marker, findings);
        if (errors.Count != 0)
        {
            MapDocumentFinding first = findings[0];
            throw new MapDocumentException(first.Reason, string.Join("\n", errors))
            {
                MarkerId = first.MarkerId, SubjectKind = first.SubjectKind, FieldPath = first.FieldPath,
                ActualNumber = first.ActualNumber, MinimumNumber = first.MinimumNumber,
                ActualVersion = first.ActualVersion, SupportedVersions = first.SupportedVersions,
                EnumType = first.EnumType, ActualInteger = first.ActualInteger,
                SurfaceId = first.SurfaceId, SpaceId = first.SpaceId, Findings = findings,
            };
        }
    }
}
