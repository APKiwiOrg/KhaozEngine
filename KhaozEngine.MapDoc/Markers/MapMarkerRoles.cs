using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>One stable finding about a marker. Findings are advisory and never make a document structurally invalid.</summary>
public sealed record MapMarkerFinding(string MarkerId, string Code, string Detail);

/// <summary>A caller's role vocabulary. The engine defines no marker roles. Roles compare ordinally.</summary>
public sealed class MapMarkerRoleCatalog
{
    readonly HashSet<string> _roles = new(StringComparer.Ordinal);

    public MapMarkerRoleCatalog(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (string role in roles)
        {
            if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("Catalog roles must be nonempty.", nameof(roles));
            _roles.Add(role);
        }
    }

    /// <summary>One <c>unknown-role</c> finding for every marker, disabled ones included, whose role the catalog does
    /// not list, in ordinal marker ID order.</summary>
    public IReadOnlyList<MapMarkerFinding> Validate(MapMarkerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Array.AsReadOnly(registry.All.Where(m => !_roles.Contains(m.Role))
            .Select(m => new MapMarkerFinding(m.Id, "unknown-role", $"Marker '{m.Id}' role '{m.Role}' is not in the caller role catalog."))
            .ToArray());
    }
}
