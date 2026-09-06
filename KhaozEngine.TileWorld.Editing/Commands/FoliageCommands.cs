using System;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>Adds or replaces one immutable cosmetic foliage layer as one reversible edit.</summary>
public sealed class SetFoliageLayerCommand : TileCommandBase
{
    readonly TileFoliageLayer _layer;
    readonly TileFoliageLayer? _old;

    /// <summary>Captures the current layer and the old and new raster extents before the command applies.</summary>
    public SetFoliageLayerCommand(TileWorldDocument doc, TileFoliageLayer layer)
        : base("Set foliage layer")
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(layer);
        if ((uint)layer.Plane >= (uint)doc.PlaneCount)
            throw new ArgumentOutOfRangeException(nameof(layer), layer.Plane,
                $"layer plane {layer.Plane} is outside 0..{doc.PlaneCount - 1}.");
        _layer = layer;
        _old = doc.GetFoliageLayer(layer.Id);
        TileDirtyRect current = Extent(layer, doc.TileSize);
        if (_old is null)
        {
            Dirty.Add(current);
        }
        else
        {
            TileDirtyRect previous = Extent(_old, doc.TileSize);
            if (previous.Plane == current.Plane)
                Dirty.Add(new TileDirtyRect(previous.Rect.Union(current.Rect), current.Plane));
            else
            {
                Dirty.Add(previous);
                Dirty.Add(current);
            }
        }
    }

    /// <inheritdoc/>
    public override void Apply(TileWorldDocument doc) => doc.SetFoliageLayer(_layer);

    /// <inheritdoc/>
    public override void Revert(TileWorldDocument doc)
    {
        if (_old is null) doc.RemoveFoliageLayer(_layer.Id);
        else doc.SetFoliageLayer(_old);
    }

    internal static TileDirtyRect Extent(TileFoliageLayer layer, float tileSize)
    {
        float x1 = layer.OriginX + ((layer.Width - 1) * layer.CellSize);
        float z1 = layer.OriginZ + ((layer.Height - 1) * layer.CellSize);
        int minX = (int)MathF.Floor(MathF.Min(TileWorldSpace.TileX(layer.OriginX, tileSize),
            TileWorldSpace.TileX(x1, tileSize)));
        int maxX = (int)MathF.Floor(MathF.Max(TileWorldSpace.TileX(layer.OriginX, tileSize),
            TileWorldSpace.TileX(x1, tileSize)));
        int minZ = (int)MathF.Floor(MathF.Min(TileWorldSpace.TileZ(layer.OriginZ, tileSize),
            TileWorldSpace.TileZ(z1, tileSize)));
        int maxZ = (int)MathF.Floor(MathF.Max(TileWorldSpace.TileZ(layer.OriginZ, tileSize),
            TileWorldSpace.TileZ(z1, tileSize)));
        return new TileDirtyRect(TileRect.FromCorners(minX, minZ, maxX, maxZ), layer.Plane);
    }
}

/// <summary>Removes one cosmetic foliage layer as one reversible edit.</summary>
public sealed class RemoveFoliageLayerCommand : TileCommandBase
{
    readonly TileFoliageLayer _old;

    /// <summary>Captures the layer before removal, including the dirty raster extent.</summary>
    public RemoveFoliageLayerCommand(TileWorldDocument doc, string id)
        : base("Remove foliage layer")
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _old = doc.GetFoliageLayer(id) ?? throw new TileWorldException($"foliage layer '{id}' does not exist");
        Dirty.Add(SetFoliageLayerCommand.Extent(_old, doc.TileSize));
    }

    /// <inheritdoc/>
    public override void Apply(TileWorldDocument doc) => doc.RemoveFoliageLayer(_old.Id);

    /// <inheritdoc/>
    public override void Revert(TileWorldDocument doc) => doc.SetFoliageLayer(_old);
}
