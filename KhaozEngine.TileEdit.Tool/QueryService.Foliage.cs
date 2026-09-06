using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>The cosmetic foliage query surface.</summary>
public sealed partial class QueryService
{
    /// <summary>Reads one detached layer by id.</summary>
    public FoliageLayerInfo FoliageGet(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return session.Read(e => FoliageLayerInfo.Of(e.Document.GetFoliageLayer(id)
            ?? throw new TileWorldException($"foliage layer '{id}' does not exist")));
    }

    /// <summary>Lists detached layers in authoring order.</summary>
    public IReadOnlyList<FoliageLayerInfo> FoliageGet() => session.Read(e =>
        e.Document.FoliageLayers.Select(FoliageLayerInfo.Of).ToArray());
}
