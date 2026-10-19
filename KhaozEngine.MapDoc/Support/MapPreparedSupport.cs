using System;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Support;

/// <summary>Point-independent support facts of one immutable acquisition: the acquisition itself, its membership
/// resolver, its support floors in ordinal order and, once first read, its records. Holds no request state, so one
/// instance answers any number of full requests. Preparing never compiles faces or reads records eagerly, so a
/// one-point query does exactly the released point-level work.</summary>
public sealed class MapPreparedSupport
{
    MapTopologyRecord[]? _records;
    MapSpaceFootprint[]? _footprints;
    MapSurfaceSeam[]? _seams;

    internal MapPreparedSupport(MapScopedSurfaces view)
    {
        ArgumentNullException.ThrowIfNull(view);
        View = view;
        Membership = new(view);
        SupportFloors = view.Surfaces.Where(s => s.Role == MapSurfaceRole.SupportFloor).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
    }

    internal MapScopedSurfaces View { get; }
    internal MapSpaceMembership Membership { get; }
    internal MapSurfaceRef[] SupportFloors { get; }

    /// <summary>The acquired topology records in ordinal ID order, read once on first use.</summary>
    internal MapTopologyRecord[] Records => _records ??= MapSpaceGeometry.Records(View).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
    internal MapSpaceFootprint[] Footprints => _footprints ??= Records.OfType<MapSpaceFootprint>().ToArray();
    internal MapSurfaceSeam[] Seams => _seams ??= Records.OfType<MapSurfaceSeam>().ToArray();

    /// <summary>Selects support for <paramref name="request"/> through the released kernel, honouring every request
    /// field. Named-owner mode samples only the request's SurfaceId and requires one. An exact world point, when given,
    /// replaces the request point's world position.</summary>
    public MapSupportResult Sample(MapSupportRequest request, bool namedSurfaceOnly = false, MapExactPoint? exactWorld = null)
    {
        t_samples++;
        return MapSupportSelection.Select(this, request, namedSurfaceOnly, exactWorld);
    }

    [ThreadStatic] static long t_samples;

    /// <summary>Support selections run through prepared support on the calling thread, counted at the call itself.</summary>
    internal static long ThreadSamples => t_samples;

    /// <summary><see cref="Sample(MapSupportRequest, bool, MapExactPoint?)"/> charging membership face work to
    /// <paramref name="work"/>, as the point query's counted overload does.</summary>
    internal MapSupportResult Sample(MapSupportRequest request, MapBoundFaceWork work)
    {
        t_samples++;
        return MapSupportSelection.Compute(this, request, work, false).Result;
    }

    /// <summary>The restricted request a support binding makes at an exact world XZ, as placements and markers seat. A
    /// Surface binding samples its named owner with no invented interval. A Space binding searches below and above its
    /// reference height in its space, with no current support and no slope limit.</summary>
    internal MapSupportResult SampleBinding(MapSupportBinding binding, MapExactXz world)
    {
        ArgumentNullException.ThrowIfNull(binding);
        t_samples++;
        if (MapSupportSelection.BindingRequest(View, binding, world) is not { } bound)
            return MapSupportSelection.Refusal(View, MapSupportStatus.Invalid, "support binding lacks its kind's members");
        return MapSupportSelection.Select(this, bound.Request, bound.NamedSurfaceOnly, bound.World);
    }
}
