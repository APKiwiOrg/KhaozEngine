using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>
    /// Render every packed light in ONE pass: for each, clear its atlas row and draw every rigid caster whose
    /// world sphere touches that light's sphere into each of its six face cells. Returns how many caster DRAW
    /// CALLS were issued in total, which is what the integration half reports as its face draw count.
    /// <para>
    /// Records into <paramref name="cl"/> and does not submit. Requires this frame's instances to be grouped
    /// and uploaded already (the pass reuses that buffer), which is exactly where the key light's depth pass
    /// sits too, and requires <see cref="UploadPointShadowFaces"/> to have run.
    /// </para>
    /// </summary>
    internal int RenderPointShadowSlots(IGpuCommandList cl)
    {
        ArgumentNullException.ThrowIfNull(cl);
        if (_pointShadows is not { } renderer || _pointShadowAtlas is null
            || _packedPointSlots.Count == 0) return 0;

        int draws = 0;
        IGpuBuffer? instances = _model.InstanceBuffer;
        PointShadowAtlas? activeAtlas = null;
        ReportSkippedPointRows(cl);
        foreach (PackedPointShadowSlot packed in _packedPointSlots)
        {
            using var row = ProfilePointRow(cl, packed);
            using (ProfilePointPass(cl, packed, PointProfilePass.Setup))
            {
                if (!ReferenceEquals(activeAtlas, packed.Atlas))
                {
                    if (activeAtlas is not null) renderer.EndPass(cl);
                    renderer.BeginPass(cl, packed.Atlas);
                    activeAtlas = packed.Atlas;
                }
                renderer.ClearRow(cl, packed.Atlas, packed.Row);
            }

            bool rigidSet = (packed.Casters & PointShadowCasterSet.Rigid) != 0;
            bool drawRigid = rigidSet && instances is not null;
            if (drawRigid)
                using (ProfileCpu("cpu.shadow.point-culling")) BuildPointCasterSpans(packed.LightPosAbsolute, packed.Radius, packed.NearRadius,
                    packed.ExclusionMin, packed.ExclusionMax);
            for (int face = 0; face < PointShadowMath.FaceCount; face++)
            {
                using var facePass = ProfilePointPass(cl, packed, PointProfilePass.Face, face);
                using (ProfilePointPass(cl, packed, PointProfilePass.Rigid, face, executed: drawRigid && _pointCasterSpans.Count > 0))
                    if (drawRigid)
                    {
                        ShadowCastKind bound = ShadowCastKind.None;
                        foreach (ShadowCasterSpan span in _pointCasterSpans)
                        {
                            var m = _meshes[span.Index];
                            if (m is not { } mesh) continue;
                            if (span.Kind != bound)
                            {
                                renderer.BeginFace(cl, packed.Atlas,
                                    packed.PackedIndex * PointShadowMath.FaceCount + face, face,
                                    packed.Row, span.Kind);
                                bound = span.Kind;
                            }
                            renderer.DrawCasterRun(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                                instances!, span.Start, span.Count);
                            draws++;
                            if (_shadowProbeActive) _shadowProbeCounts.PointRigidIssuedInstances += span.Count;
                        }
                    }
                // Casting foliage at rest rides the rigid set only, from its own proxy stream.
                if (rigidSet)
                    draws += DrawFoliagePointShadowFace(cl, renderer, packed.Atlas, packed.PackedIndex, face,
                        packed.Row, packed.LightPosAbsolute, packed.Radius, packed.NearRadius,
                        packed.ExclusionMin, packed.ExclusionMax, packed);
                else ProfilePointPass(cl, packed, PointProfilePass.Foliage, face, executed: false);
                if ((packed.Casters & PointShadowCasterSet.Skinned) != 0 && packed.RequestIndex >= 0)
                    draws += DrawPointSkinnedCastersForFace(cl, renderer, packed, face);
                else ProfilePointPass(cl, packed, PointProfilePass.Skinned, face, executed: false);
            }
        }
        ReportSkippedTransientPointRows(cl);
        if (activeAtlas is not null)
            using (ProfilePass(cl, "shadow.point.finish")) renderer.EndPass(cl);
        return draws;
    }

}
