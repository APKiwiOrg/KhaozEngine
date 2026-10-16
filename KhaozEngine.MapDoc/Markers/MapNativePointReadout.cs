using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>R6-P7. Native point readouts on authored support, never on analytic terrain.</summary>
public static class MapNativePointReadout
{
    /// <summary>Samples the caller-named Surface owner at <paramref name="point"/> through the shared seat query that
    /// support-bound placements and markers use, with the default scoped budgets. A Surface sample takes its owner
    /// directly with no reference height or search range. Every status is returned typed, refusals included, and only
    /// Supported carries a height. The engine never picks a topmost floor or a zero height in its place. An exact overflow
    /// refuses as NotRepresentable.</summary>
    public static MapSupportResult SurfaceAt(MapEffectiveResources resources, MapExactXz point, string surfaceId)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        var binding = new MapSupportBinding(MapSupportBindingKind.Surface, surfaceId, null, null, null, null);
        try
        {
            return MapResolverV2.SeatSupport(new MapExactPoint(point.X, default, point.Z), binding, resources.SurfaceSource,
                new[] { resources.Digest }, new MapQueryLimits());
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException($"Surface '{surfaceId}' readout status NotRepresentable: overflow.", error);
        }
    }
}
