using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldDocument
{
    readonly List<TileFoliageLayer> _foliageLayers = new();
    ReadOnlyCollection<TileFoliageLayer>? _foliageView;

    /// <summary>Optional cosmetic foliage layers. The layers are immutable and ordered by authoring order.</summary>
    public IReadOnlyList<TileFoliageLayer> FoliageLayers => _foliageView ??= _foliageLayers.AsReadOnly();

    /// <summary>Finds a foliage layer by ordinal id, or null when absent.</summary>
    public TileFoliageLayer? GetFoliageLayer(string id)
    {
        if (id is null) return null;
        for (int i = 0; i < _foliageLayers.Count; i++)
            if (string.Equals(_foliageLayers[i].Id, id, StringComparison.Ordinal)) return _foliageLayers[i];
        return null;
    }

    /// <summary>Adds or replaces one validated foliage layer while preserving its list position.</summary>
    public void SetFoliageLayer(TileFoliageLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if ((uint)layer.Plane >= (uint)PlaneCount)
            throw TileFoliageLayer.Invalid($"layer '{layer.Id}' uses plane {layer.Plane}, the world has planes 0 through {PlaneCount - 1}");
        for (int i = 0; i < _foliageLayers.Count; i++)
        {
            if (!string.Equals(_foliageLayers[i].Id, layer.Id, StringComparison.Ordinal)) continue;
            _foliageLayers[i] = layer;
            return;
        }
        _foliageLayers.Add(layer);
    }

    /// <summary>Removes one foliage layer. Returns false when it was absent.</summary>
    public bool RemoveFoliageLayer(string id)
    {
        if (id is null) return false;
        for (int i = 0; i < _foliageLayers.Count; i++)
        {
            if (!string.Equals(_foliageLayers[i].Id, id, StringComparison.Ordinal)) continue;
            _foliageLayers.RemoveAt(i);
            return true;
        }
        return false;
    }

    internal void ValidateFoliageLayers()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (TileFoliageLayer layer in _foliageLayers)
        {
            if (!ids.Add(layer.Id)) throw TileFoliageLayer.Invalid($"layer '{layer.Id}' is listed twice");
            if ((uint)layer.Plane >= (uint)PlaneCount)
                throw TileFoliageLayer.Invalid($"layer '{layer.Id}' uses plane {layer.Plane}, the world has planes 0 through {PlaneCount - 1}");
        }
    }
}
