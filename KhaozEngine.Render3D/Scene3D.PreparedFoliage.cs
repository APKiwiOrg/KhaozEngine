using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>Submit a published foliage stream in its authored anchor-local space. Only frame selection and
    /// uniforms vary. Model expansion, policy resolution, layout and instance uploads are already complete.</summary>
    public int DrawPreparedFoliage(SceneResourceHandle handle, Vector3 localFocus, in FoliageRenderSettings settings,
        RenderAnchor anchor, ReadOnlySpan<FoliageInteractor> interactors = default, bool colour = true, bool shadows = true,
        double? timeSeconds = null, int windSeed = 0)
    {
        var resource = ResourceOf(handle);
        if (resource.PublishedFoliage is not { } batch || batch.PreparedAnchor != anchor)
            throw new ArgumentException("Foliage requires a published product with the same anchor.", nameof(handle));
        int firstColour = _foliageDraws.Count, firstShadow = _foliageShadowDraws.Count;
        float previousTime = EffectTimeSeconds;
        try
        {
            // Seed selects a repeatable wind phase. Zero preserves the released phase and layout.
            if (timeSeconds is { } time) EffectTimeSeconds = (float)(time + (uint)windSeed % 65536 * (1d / 256));
            int count = DrawFoliage(batch, localFocus, settings, interactors);
            if (!colour) _foliageDraws.RemoveRange(firstColour, _foliageDraws.Count - firstColour);
            if (!shadows) _foliageShadowDraws.RemoveRange(firstShadow, _foliageShadowDraws.Count - firstShadow);
            return count;
        }
        finally { EffectTimeSeconds = previousTime; }
    }
    void UploadPreparedFoliageAnchors(IGpuCommandList cl)
    {
        foreach (var draw in _foliageDraws) Upload(draw.Batch);
        foreach (var draw in _foliageShadowDraws) Upload(draw.Batch);
        void Upload(FoliageBatch batch)
        {
            if (batch.PreparedAnchor is not { } anchor || batch.AnchorFrame == _frameIndex) return;
            batch.AnchorFrame = _frameIndex;
            Span<byte> bytes = stackalloc byte[256]; bytes.Clear();
            var relative = new Vector4((float)((double)anchor.X - _frameOrigin.X),
                (float)((double)anchor.Y - _frameOrigin.Y), (float)((double)anchor.Z - _frameOrigin.Z), 0);
            var absolute = new Vector4(anchor.X, anchor.Y, anchor.Z, 0);
            MemoryMarshal.Write(bytes, in relative); MemoryMarshal.Write(bytes[16..], in absolute);
            cl.UpdateBuffer(batch.AnchorBuffer!, 0, (ReadOnlySpan<byte>)bytes);
        }
    }
    MeshBounds FoliageAbsoluteBounds(FoliageBatch batch, MeshBounds local)
    {
        if (batch.PreparedAnchor is not { } anchor) return local;
        Vector3 Offset(Vector3 p) => new((float)((double)anchor.X + p.X),
            (float)((double)anchor.Y + p.Y), (float)((double)anchor.Z + p.Z));
        return new(Offset(local.Min), Offset(local.Max));
    }
}
