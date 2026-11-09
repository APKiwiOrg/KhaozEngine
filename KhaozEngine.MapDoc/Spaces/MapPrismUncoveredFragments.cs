using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Owns uncovered polygon replacements through their final containment consumer.</summary>
internal sealed class MapPrismUncoveredFragments : IDisposable
{
    readonly MapPreparationReservation? _workspace = MapPrismValidationWorkspace.Admission?.Workspace();
    internal IReadOnlyList<IReadOnlyList<MapWideXz>> Polygons { get; private set; }
    internal MapPrismUncoveredFragments(IReadOnlyList<MapWideXz> polygon)
    {
        try
        {
            _workspace?.Reserve(128);
            Polygons = new List<IReadOnlyList<MapWideXz>> { polygon };
        }
        catch { _workspace?.Dispose(); throw; }
        MapPrismValidationWorkspace.Candidates(Polygons.Count, uncovered: true);
    }
    internal void Subtract(IReadOnlyList<MapWideXz> parent)
    {
        long nextBytes = checked(128L + Polygons.Sum(p => 128L + 256L * parent.Count * (p.Count + parent.Count)));
        _workspace?.Reserve(nextBytes);
        List<IReadOnlyList<MapWideXz>> next;
        long inventory;
        try
        {
            next = new List<IReadOnlyList<MapWideXz>>();
            foreach (var remaining in Polygons) next.AddRange(MapOccupiedPartitions.Difference(remaining, parent));
            inventory = checked(128L + next.Sum(p => 128L + 256L * p.Count));
            if (inventory > nextBytes) throw new InvalidOperationException("Uncovered fragment storage exceeds its admitted replacement.");
        }
        catch { _workspace?.Release(nextBytes); throw; }
        Polygons = next;
        // The old arrays die here. The new envelope can now settle to the surviving polygon storage.
        if (_workspace is not null && _workspace.Bytes > inventory) _workspace.Release(_workspace.Bytes - inventory);
        MapPrismValidationWorkspace.Candidates(Polygons.Count, uncovered: true);
    }
    public void Dispose() { Polygons = Array.Empty<IReadOnlyList<MapWideXz>>(); _workspace?.Dispose(); }
}
