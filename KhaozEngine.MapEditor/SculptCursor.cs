using System;
using System.Numerics;
using KhaozEngine.App;

namespace KhaozEngine.MapEditor;

/// <summary>The sculpt brush preview, picked and sampled from the same live field as a stroke.</summary>
internal static class SculptCursor
{
    internal static int Build(EditorToolController controller, in EditorFrameInput input,
        in SculptBounds bounds, float cellSize, bool pointerInViewport, bool navigationOwnsPointer,
        bool modalOpen, Func<float, float, bool> isLoaded, Func<Vector3, float> markerHalfSize,
        Span<SculptOverlayLine> lines,
        out SculptOverlayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(isLoaded);
        ArgumentNullException.ThrowIfNull(markerHalfSize);
        if (controller.Mode != EditorToolMode.SculptTerrain || !pointerInViewport
            || navigationOwnsPointer || modalOpen || controller.Field is not { } field)
        {
            frame = SculptOverlayFrame.Hidden;
            return 0;
        }
        StringId operationLabel = MapEditorStrings.SculptOperation(controller.Brush);
        if (!EditorPicking.PickTerrain(field, input.RayOrigin, input.RayDirection,
                EditorToolController.PickDistance, out Vector3 center)
            || !CenterInside(center, bounds, cellSize) || !isLoaded(center.X, center.Z))
        {
            frame = new SculptOverlayFrame(true, SculptOverlayState.Invalid, operationLabel,
                MapEditorStrings.SculptState(SculptOverlayState.Invalid), default, false);
            return 0;
        }

        SculptOverlayState state = controller.IsSculpting ? SculptOverlayState.Active : SculptOverlayState.Hover;
        frame = new SculptOverlayFrame(true, state, operationLabel,
            MapEditorStrings.SculptState(state), center, true);
        return SculptBrushOverlay.Build(center, controller.BrushRadius, markerHalfSize(center), bounds, cellSize,
            field.SampleHeight, isLoaded, lines);
    }

    static bool CenterInside(Vector3 center, in SculptBounds bounds, float cellSize) =>
        bounds.HasArea && float.IsFinite(cellSize) && cellSize > 0f
        && center.X >= bounds.MinCellX * cellSize && center.X <= bounds.MaxCellX * cellSize
        && center.Z >= bounds.MinCellZ * cellSize && center.Z <= bounds.MaxCellZ * cellSize;
}
