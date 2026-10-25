using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Room broad-phase candidates in parent then authored-piece order.</summary>
internal sealed class MapPrismRoomCandidates : IDisposable
{
    readonly MapWaterPolygons.Bounds[] _parents;
    readonly MapBoundsIndex _index;
    readonly MapSceneAdmission? _admission;
    readonly MapPreparationReservation? _workspace;
    internal MapPrismRoomCandidates(MapWaterDomain domain, MapWaterPolygons.Bounds[] parents)
    {
        _parents = parents; _admission = MapPrismValidationWorkspace.Admission;
        _workspace = _admission?.Workspace();
        try
        {
            int count = parents.Length == 0 ? 0 : domain.Pieces.Count;
            if (count != 0) _admission?.BeforeGeometry(checked(512L + 512L * count), _workspace);
            _index = new(Enumerable.Range(0, count).Select(domain.PieceBounds).ToArray());
        }
        catch { _workspace?.Dispose(); throw; }
    }
    internal IEnumerable<int> Pieces(int parent)
    {
        // The query admits growth and sorting before allocation. Its receipt survives each yielded consumer.
        using var row = _admission?.BeginPreparation();
        var candidates = _index.Overlapping(_parents[parent], admission: _admission);
        MapPrismValidationWorkspace.Candidates(candidates.Count);
        foreach (int piece in candidates) yield return piece;
    }
    public void Dispose() => _workspace?.Dispose();
}
