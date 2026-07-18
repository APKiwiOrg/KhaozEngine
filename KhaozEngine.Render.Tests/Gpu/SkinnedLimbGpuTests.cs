using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // GPU integration for the turn-key SkinnedLimb: build a limb against a real Scene3D, Update, Draw, and assert it
    // renders opaque pixels (the tube was uploaded and the per-frame bones drew). The motion math itself is covered
    // headless in Render3D/SkinnedLimbTests; this proves the GPU mesh ownership (BuildTube load + DrawSkinned +
    // UnloadSkinnedMesh on Dispose) is wired. Skipped unless KE_GPU_TESTS=1.
    public sealed class SkinnedLimbGpuTests
    {
        const int W = 128, H = 128;
        static readonly Color Tint = new(0.8f, 0.4f, 0.3f, 1f);

        static int OpaqueCount(byte[] px) { int n = 0; for (int p = 0; p < px.Length / 4; p++) if (px[p * 4 + 3] > 200) n++; return n; }

        [GpuFact]
        public void Limb_BuildsUpdatesDrawsAndUnloadsOnDispose()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

            var limb = new SkinnedLimb(preview.Scene, radius: 0.5f, length: 4f, ringSegments: 10, radialSegments: 10,
                boneCount: 6, ChainConfig.Writhe, Axis.Z);
            Assert.NotEqual(0, limb.Handle.Generation); // a real GPU mesh was uploaded

            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 1.3f);
            byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s => limb.Draw(s, Matrix4x4.Identity, Tint)).Handle, W, H);
            Assert.True(OpaqueCount(px) > 100, $"limb should render opaque pixels, got {OpaqueCount(px)}");

            limb.Dispose();
            // After Dispose the handle is stale; DrawSkinned with it is a no-op, so the frame is empty.
            byte[] empty = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(limb.Handle, limb.Bones, Matrix4x4.Identity, Tint)).Handle, W, H);
            Assert.True(OpaqueCount(empty) == 0, $"disposed limb must not render, got {OpaqueCount(empty)}");
        }
    }
}
