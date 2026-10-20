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
    public static IReadOnlyList<string> Validate(MapLocalMarkerDoc marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var errors = new List<string>();
        string at = $"Marker '{marker.Id}': ";
        if (string.IsNullOrWhiteSpace(marker.Id) || string.IsNullOrWhiteSpace(marker.Name) || string.IsNullOrWhiteSpace(marker.Role))
            errors.Add(at + "a nonempty ID, name and role are required.");
        if (!float.IsFinite(marker.Position.X) || !float.IsFinite(marker.Position.Z) || !float.IsFinite(marker.YawRadians))
            errors.Add(at + "position X and Z and yaw must be finite.");
        if (marker.Tags is null || marker.Tags.Any(t => t is null)) errors.Add(at + "tags must not be null.");
        switch (marker.PayloadVersion)
        {
            case 1:
                if (marker.HeightMode is not null || marker.SupportBinding is not null || marker.ArchetypeId is not null)
                    errors.Add(at + "payload 1 carries no heightMode, supportBinding or archetypeId.");
                if (!float.IsFinite(marker.Position.Y)) errors.Add(at + "the explicit height must be finite.");
                break;
            case 2:
                // Blank like the ID, name and role rules above and the released spawn archetype rule.
                if (marker.ArchetypeId is not null && string.IsNullOrWhiteSpace(marker.ArchetypeId))
                    errors.Add(at + "archetypeId must be nonblank when present.");
                Height(marker, at, errors);
                break;
            default:
                errors.Add(at + "unsupported local marker payload version.");
                break;
        }
        return errors;
    }

    internal static void Require(MapLocalMarkerDoc marker)
    {
        IReadOnlyList<string> errors = Validate(marker);
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
    }

    static void Height(MapLocalMarkerDoc marker, string at, List<string> errors)
    {
        switch (marker.HeightMode)
        {
            case MapMarkerHeightMode.Explicit:
                if (marker.SupportBinding is not null) errors.Add(at + "an Explicit height has no support binding.");
                if (!float.IsFinite(marker.Position.Y)) errors.Add(at + "the explicit height must be finite.");
                break;
            case MapMarkerHeightMode.Support:
                // The one canonical encoding. Negative zero is a second spelling and refuses.
                if (BitConverter.SingleToUInt32Bits(marker.Position.Y) != 0) errors.Add(at + "a Support height stores Position.Y exactly +0.");
                if (marker.SupportBinding is not { } binding) errors.Add(at + "a Support height requires a support binding.");
                else MapSupportBindingRules.Validate(binding, at, errors);
                break;
            default:
                errors.Add(at + "payload 2 requires heightMode Explicit or Support.");
                break;
        }
    }
}
