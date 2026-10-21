using System;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>The sole emitter of registered roof parts. Concealment changes channels and never resources.</summary>
public static class NativeMapRoofs
{
    public static void Record(MapRenderPlan plan, MapFrameState frame, IMapDrawSink sink)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(sink);
        plan.Update(frame);
        RecordEvaluated(plan, frame, sink);
    }
    // The composite evaluates once before any pass emits a frame.
    internal static void RecordEvaluated(MapRenderPlan plan, MapFrameState frame, IMapDrawSink sink) =>
        plan.Inventory.Placements.RecordRoofs(frame, plan.RoofEvaluation, plan.RoofIndices, sink);
}
