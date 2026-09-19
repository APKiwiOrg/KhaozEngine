using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

public abstract partial class EditorCommand
{
    internal virtual RectArea? DirtyRegionFor(MapDocument doc) => DirtyRegion;
}

public sealed partial class AddExclusionCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion => ShapeGeometry.TryBounds(_exclusion.Shape, out RectArea area) ? area : null;
    internal override RectArea? DirtyRegionFor(MapDocument doc) =>
        ShapeGeometry.TryBounds(_exclusion.Shape, ShapeGeometry.BoundsMarginFor(doc), out RectArea area) ? area : null;
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class RemoveExclusionCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion =>
        _removed is not null && ShapeGeometry.TryBounds(_removed.Shape, out RectArea area) ? area : null;
    internal override RectArea? DirtyRegionFor(MapDocument doc) => _removed is not null
        && ShapeGeometry.TryBounds(_removed.Shape, ShapeGeometry.BoundsMarginFor(doc), out RectArea area) ? area : null;
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class EditExclusionShapeCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion => EditorInvalidationBounds.Of(_oldShape, _newShape);
    internal override RectArea? DirtyRegionFor(MapDocument doc) =>
        EditorInvalidationBounds.Of(_oldShape, _newShape, ShapeGeometry.BoundsMarginFor(doc));
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class EditExclusionLayersCommand
{
    MapShapeDoc? _shape;
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion => ShapeGeometry.TryBounds(_shape, out RectArea area) ? area : null;
    internal override RectArea? DirtyRegionFor(MapDocument doc) =>
        ShapeGeometry.TryBounds(_shape, ShapeGeometry.BoundsMarginFor(doc), out RectArea area) ? area : null;
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class AddScatterOverrideCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion => ShapeGeometry.TryBounds(_override.Shape, out RectArea area) ? area : null;
    internal override RectArea? DirtyRegionFor(MapDocument doc) =>
        ShapeGeometry.TryBounds(_override.Shape, ShapeGeometry.BoundsMarginFor(doc), out RectArea area) ? area : null;
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class RemoveScatterOverrideCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion =>
        _removed is not null && ShapeGeometry.TryBounds(_removed.Shape, out RectArea area) ? area : null;
    internal override RectArea? DirtyRegionFor(MapDocument doc) => _removed is not null
        && ShapeGeometry.TryBounds(_removed.Shape, ShapeGeometry.BoundsMarginFor(doc), out RectArea area) ? area : null;
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class EditScatterOverrideShapeCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion => EditorInvalidationBounds.Of(_oldShape, _newShape);
    internal override RectArea? DirtyRegionFor(MapDocument doc) =>
        EditorInvalidationBounds.Of(_oldShape, _newShape, ShapeGeometry.BoundsMarginFor(doc));
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class EditScatterOverrideValuesCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override RectArea? DirtyRegion => ShapeGeometry.TryBounds(_newValue.Shape, out RectArea area) ? area : null;
    internal override RectArea? DirtyRegionFor(MapDocument doc) =>
        ShapeGeometry.TryBounds(_newValue.Shape, ShapeGeometry.BoundsMarginFor(doc), out RectArea area) ? area : null;
    internal override bool InvalidatesAllLoaded => DirtyRegion is null;
}

public sealed partial class ReorderScatterOverrideCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

public sealed partial class EditScatterLayerCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

public sealed partial class EditCompanionLayerCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

public sealed partial class EditTerrainCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

public sealed partial class AddBiomeBandCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

public sealed partial class RemoveBiomeBandCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

public sealed partial class EditBiomeBandCommand
{
    internal override bool RefreshesLayerConfig => true;
    internal override bool InvalidatesAllLoaded => true;
}

static class EditorInvalidationBounds
{
    internal static RectArea? Of(MapShapeDoc oldShape, MapShapeDoc newShape)
        => Of(oldShape, newShape, ShapeGeometry.ShapeBoundsMargin);

    internal static RectArea? Of(MapShapeDoc oldShape, MapShapeDoc newShape, float margin)
    {
        if (!ShapeGeometry.TryBounds(oldShape, margin, out RectArea oldArea)) return null;
        if (!ShapeGeometry.TryBounds(newShape, margin, out RectArea newArea)) return null;
        return FeatureGeometry.Union(oldArea, newArea);
    }
}
