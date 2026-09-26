using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// THE JITTER REACHES THE GPU, AND ONLY WHERE IT SHOULD. The sweep proves which snapshot field each call site names.
/// This proves what the passes actually upload, with no device: a frame is recorded through
/// <see cref="RecordingGpuCommandList"/> with payload capture on, and every upload is searched for the 64 bytes of the
/// jittered and the unjittered view-projection. On the fake device <c>GpuClip.Correct</c> is the identity, so the
/// bytes a pass uploads are the matrix it was handed.
/// </summary>
public sealed class FrameViewUploadTests
{
    [Fact]
    public void TheRasterPassesUploadTheJitteredMatrixAndNeverTheUnjitteredOne()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        scene.Camera.Frame(new Vector3(0f, 0.5f, 0f), new Vector3(8f, 4f, 8f));
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        Scene3D.TextureHandle white = scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None);
        var trail = new[] { new TrailSample(new Vector3(-1f, 1f, 1f), 0.1f, 1f), new TrailSample(new Vector3(1f, 1f, 1f), 0.1f, 1f) };

        void Raster(Scene3D s)
        {
            s.Draw(box, Matrix4x4.Identity);                                                   // the model frame block
            s.DrawBillboard(white, new Vector3(1f, 1f, 0f), 0.5f, Color.White);                 // textured billboards
            s.DrawBeam(new Vector3(-1f, 0.5f, 0f), new Vector3(1f, 0.5f, 0f), 0.2f, Color.White);   // beams
            s.DrawTrail(trail, TrailStyle.Default);                                            // trails
            s.DrawParticle(new ParticleSprite
            {
                Position = new Vector3(0f, 1.5f, 0f), Size = 0.5f, Color = Color.White, Shape = ParticleShape.SoftGlow,
            });                                                                                // particles
            s.DebugWireSphere(new Vector3(0f, 0.5f, 0f), 1f, Color.White);                     // depth-tested wire
        }

        rig.Frame(Raster);   // primes every buffer and pipeline
        var recording = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        rig.Frame(Raster, recording);

        FrameView view = scene.CurrentFrameView;
        Assert.NotEqual(Vector2.Zero, view.JitterPixels);
        int jittered = Occurrences(recording, view.JitteredViewProjection);
        // The motion block carries the unjittered matrices by design, because motion is measured between unjittered
        // positions, so its uploads do not count.
        int unjittered = Occurrences(recording, view.ViewProjection, scene.MotionResourcesForTests?.FrameBuffer);
        Assert.True(unjittered == 0, $"a pass rasterising into the internal target uploaded the unjittered matrix {unjittered} time(s)");
        Assert.True(jittered >= 6, $"the jittered matrix reached {jittered} upload(s), expected one per raster pass drawn (6)");
    }

    [Fact]
    public void TheDisplaySizeOverlaysUploadTheUnjitteredMatrix()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;

        static void Overlays(Scene3D s)
        {
            s.DebugLine(new Vector3(-1f, 0f, 0f), new Vector3(1f, 1f, 0f), Color.White);         // debug lines
            s.DebugFilledQuad(new Vector3(0f, 0f, 0f), 0.5f, Color.White);                       // fills
            s.DrawBillboard(new Vector3(0f, 1f, 0f), 0.5f, Color.White, BillboardBlend.Additive);   // legacy additive
            s.DrawBillboard(new Vector3(0.5f, 1f, 0f), 0.5f, Color.White);                       // legacy alpha
        }

        rig.Frame(Overlays);
        var recording = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        rig.Frame(Overlays, recording);

        FrameView view = scene.CurrentFrameView;
        Assert.NotEqual(Vector2.Zero, view.JitterPixels);
        // The motion block's two unjittered matrices would stand in for two overlay passes, so they do not count.
        int unjittered = Occurrences(recording, view.ViewProjection, scene.MotionResourcesForTests?.FrameBuffer);
        Assert.True(unjittered >= 4, $"the unjittered matrix reached {unjittered} upload(s), expected one per overlay pass (4)");
        Assert.True(Occurrences(recording, view.JitteredViewProjection) >= 1, "the model frame block lost the jittered matrix");
    }

    // except: a buffer whose uploads do not count.
    static int Occurrences(RecordingGpuCommandList recording, in Matrix4x4 matrix, IGpuBuffer? except = null)
    {
        byte[] pattern = MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in matrix)).ToArray();
        int count = 0;
        foreach (RecordingGpuCommandList.Upload upload in recording.Uploads)
        {
            if (ReferenceEquals(upload.Buffer, except)) continue;
            ReadOnlySpan<byte> data = upload.Data;
            for (int at = data.IndexOf(pattern); at >= 0; at = data.IndexOf(pattern))
            {
                count++;
                data = data[(at + pattern.Length)..];
            }
        }
        return count;
    }
}
