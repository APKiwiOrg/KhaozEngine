using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

public sealed partial class MapPlacementRenderBatch
{
    /// <summary>Records effective colour geometry for one clustered roof into an optional outline consumer.
    /// Proxy outlines reuse only that roof's cached flattened HLOD source, never its neighbours' merged mesh.</summary>
    public void RecordRoofOutline<TSink>(string roofId, MapFrameState frame, TSink sink) where TSink : IMapDrawSink =>
        RecordRoofOutline(roofId, frame, sink, null, null);

    internal void RecordRoofOutline<TSink>(string roofId, MapFrameState frame, TSink sink,
        MapRoofEvaluationBuffer? roofs, IReadOnlyDictionary<string, int>? indices) where TSink : IMapDrawSink
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentException.ThrowIfNullOrWhiteSpace(roofId);
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (_registered is null) throw new InvalidOperationException("Register placements before recording outlines.");
        var emission = new OutlineEmission<TSink>(this, roofId, frame, sink);
        if (roofs is not null && indices!.TryGetValue(roofId, out int roof) && roofs.Roofs[roof].ColourVisible)
            foreach (var layer in _roofs) EmitOutline(layer, emission, 0);
        for (int i = 0; i < Clusters.Count; i++)
        {
            if (_roofCohorts[i] is not { } cohort) continue;
            var request = Clusters[i].Request;
            int member = -1;
            for (int j = 0; j < request.Instances.Count; j++) if (request.Instances[j].Id == roofId) { member = j; break; }
            if (member < 0) continue;
            cohort.Evaluate(frame);
            var decision = ResolveCluster(i, frame, out _);
            if (decision.DrawsIndividualColour(request.Key, member, cohort))
                EmitOutline(request.BorrowLayer, emission, decision.State.IndividualDissolveFloor);
            if (decision.DrawsMergedColour)
            {
                var instance = request.Instances[member];
                var source = _parts[_roofOutlineParts[roofId]];
                WriteOutline(emission, instance, new(source.Handle, instance.LocalTransform)
                { Dissolve = decision.State.MergedDissolve, DissolveComplement = decision.State.MergedComplement ? 1 : 0 });
            }
        }
    }
    readonly record struct OutlineEmission<TSink>(MapPlacementRenderBatch Batch, string Id, MapFrameState Frame, TSink Sink) where TSink : IMapDrawSink;
    static void EmitOutline<TSink>(PropLayer layer, OutlineEmission<TSink> emission, float floor) where TSink : IMapDrawSink =>
        PropRenderer.EmitNative(layer, default, emission, static (e, instance, draw) =>
        { if (instance.Id == e.Id) WriteOutline(e, instance, draw); }, floor,
        resolveFocus: static (e, anchor) => MapRenderTransform.FocusLocal(e.Frame.Focus, new(anchor.X, anchor.Y, anchor.Z)));
    static void WriteOutline<TSink>(OutlineEmission<TSink> emission, NativePropInstance instance, RigidInstanceDraw draw) where TSink : IMapDrawSink
    {
        int part = emission.Batch._partIndices[(instance.Id, draw.Mesh.Index, draw.Mesh.Generation)];
        var anchor = new MapSubmissionAnchor(instance.Anchor.X, instance.Anchor.Y, instance.Anchor.Z);
        emission.Sink.Write(new(emission.Batch._registered![part].Reference, MapDrawChannels.Colour | MapDrawChannels.Selection,
            anchor, draw.World, MapRenderTransform.FocusLocal(emission.Frame.Focus, anchor),
            new MapRigidFrame(draw.Tint, draw.Material, draw.Dissolve, draw.DissolveEdgeWidth, draw.DissolveEdgeColor,
                draw.InvertShadowDissolve, draw.DissolveComplement, draw.Motion), null, null, null));
    }
}
