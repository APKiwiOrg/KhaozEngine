using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.MapEdit;

public sealed record NativeWireReadiness(int Pending, int Ready, long CpuBytes, long GpuBytes, long QueuedBytes);
public sealed record NativeRenderWireResult(int SchemaVersion, string Status, string? Backend, NativeScope RequestedScope,
    IReadOnlyList<NativeScope> RequiredScopes, IReadOnlyList<NativeScope> AcquiredScopes, NativeFraming Framing,
    NativeFramePoint Observer, string? ObserverSpaceId, NativeFramePoint? Focus, string FocusSource, string RoofMode,
    double TimeSeconds, int WindSeed, string Aa, string StyleId, string Requirement, int WarmupFrames, string Route,
    bool CompleteForScope, bool WholeWorldComplete, bool Capped, NativeWireReadiness Readiness, IReadOnlyList<string> Findings);
public static class NativeRenderWire
{
    public static NativeRenderWireResult ToWire(MapSnapshotResult result)
    {
        var e = result.Echo; var w = result.Witness; var r = result.Readiness;
        return new(1, result.Status.ToString(), result.Backend, NativeScope.From(w.RequestedScope), w.RequiredScopes.Select(NativeScope.From).ToArray(),
            w.AcquiredScopes.Select(NativeScope.From).ToArray(), NativeFraming.From(e.Framing), NativeFramePoint.From(e.Observer), e.ObserverSpaceId,
            result.FocusSource == MapCaptureFocusSource.Unresolved || e.Focus is null ? null : NativeFramePoint.From(e.Focus.Value), result.FocusSource.ToString(),
            e.RoofMode.ToString(), e.TimeSeconds, e.WindSeed, e.Policy.Aa.ToString(), e.StyleId, e.Requirement.ToString(), e.Policy.WarmupFrames,
            e.Route.ToString(), w.Scene.CompleteForScope, w.Scene.WholeWorldComplete, w.Capped,
            new(r.Pending, r.Ready, r.CpuBytes, r.GpuBytes, r.QueuedBytes), result.Findings);
    }
}
