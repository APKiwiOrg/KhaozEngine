using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public sealed partial class MapScene3DSink
{
    /// <summary>Submits a selected roof's effective colour descriptor through the shared mesh-outline pass.</summary>
    public void RecordRoofOutline(MapRenderPlan plan, string roofId, MapFrameState frame, MeshOutlineGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Update(frame);
        plan.Inventory.Placements.RecordRoofOutline(roofId, frame, new OutlineSink(this, group), plan.RoofEvaluation, plan.RoofIndices);
    }
    readonly record struct OutlineSink(MapScene3DSink Owner, MeshOutlineGroup Group) : IMapDrawSink
    {
        public void Register(MapPreparedResource resource) => Owner.Register(resource);
        public void Write(in MapDrawRecord draw)
        {
            if (!Owner._references.TryGetValue(draw.Resource, out var handle)) throw new InvalidOperationException("Unpublished native outline.");
            var rigid = draw.Rigid ?? throw new MapDocumentException("Roof outlines require rigid geometry.");
            Owner._scene.DrawMeshOutlineAnchored(Group, Owner._access.MeshOf(new(handle.Value)), draw.LocalTransform,
                new(draw.Anchor.X, draw.Anchor.Y, draw.Anchor.Z), rigid.Dissolve, rigid.DissolveComplement > .5f);
        }
    }
}
