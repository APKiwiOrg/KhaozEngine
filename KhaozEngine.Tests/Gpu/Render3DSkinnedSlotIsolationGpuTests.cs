using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Probes whether each skinned draw's per-draw dynamic bone-buffer offset actually selects ITS OWN slot when
    // many draws of the SAME mesh share one resource set (Xcode flagged "Redundant Binding x37" in the live game,
    // which would mean the dynamic offset is dropped and every draw reads the first draw's slot).
    //
    // Slot 0 gets an OFF-SCREEN pose (bones translate the tube far away); the other N-1 draws get the normal rest
    // pose at the framed origin. If offsets work, the rest draws render (slot 0's off-screen pose stays off). If
    // the offset is stuck at slot 0, the rest draws read the off-screen pose too and NOTHING renders - matching the
    // live "tentacles never render" symptom. Skipped unless KE_GPU_TESTS=1.
    public sealed class Render3DSkinnedSlotIsolationGpuTests
    {
        const int W = 128, H = 128;
        static readonly Color Tint = new(0.8f, 0.4f, 0.3f, 1f);

        static int OpaqueCount(byte[] px) { int n = 0; for (int p = 0; p < px.Length / 4; p++) if (px[p * 4 + 3] > 200) n++; return n; }

        [GpuFact]
        public void EachDrawReadsItsOwnBoneSlot_NotTheFirst()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 8, 8, 6, Axis.Z);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

            // Off-screen pose: every joint translated far in +Y, so the skinned tube lands way outside the frame.
            var offscreen = new Matrix4x4[tube.BoneCount];
            for (int b = 0; b < offscreen.Length; b++) offscreen[b] = Matrix4x4.CreateTranslation(0, 1000f, 0);
            Matrix4x4[] rest = tube.RestPose;

            // Reference: a single rest draw renders this many opaque px.
            byte[] refPx = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint)).Handle, W, H);
            int refOpaque = OpaqueCount(refPx);
            Assert.True(refOpaque > 100, $"rest reference should render, got {refOpaque}");

            foreach (int n in new[] { 2, 8, 37 })
            {
                byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
                {
                    s.DrawSkinned(h, offscreen, Matrix4x4.Identity, Tint);                 // slot 0: off-screen
                    for (int i = 1; i < n; i++) s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint); // slots 1..n-1: on-screen rest
                }).Handle, W, H);
                int opaque = OpaqueCount(px);
                // The n-1 rest draws overlap at the origin, so a correct render is ~refOpaque. If the dynamic offset
                // is stuck at slot 0, every draw reads the off-screen pose and opaque collapses toward 0.
                Assert.True(opaque > 0.5 * refOpaque,
                    $"N={n}: draws past the first did not read their own bone slot (stuck offset?). opaque={opaque} vs ref {refOpaque}");
            }
            preview.Scene.UnloadSkinnedMesh(h);
        }
    }
}
