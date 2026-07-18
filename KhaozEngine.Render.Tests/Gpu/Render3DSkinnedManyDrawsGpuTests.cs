using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Classifies the "skinned draws past the first drop out" symptom: is it single-frame (many DrawSkinned in one
    // Capture) or multi-frame? For each N, draws N skinned meshes in ONE Capture with the LAST one alone at the
    // bottom of the frame and the other N-1 stacked at the top, then asserts the bottom half rendered (i.e. the
    // Nth draw survived). A weak aggregate-pixel test would pass even if only the first few draws render; this
    // does not. Skipped unless KE_GPU_TESTS=1.
    public sealed class Render3DSkinnedManyDrawsGpuTests
    {
        const int W = 128, H = 128;
        static readonly Color Tint = new(0.8f, 0.4f, 0.3f, 1f);

        static int BottomOpaque(byte[] px)
        {
            int n = 0;
            for (int y = H / 2; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (px[(y * W + x) * 4 + 3] > 200) n++;
            return n;
        }

        [GpuFact]
        public void LastOfManySkinnedDraws_StillRenders()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 5f, 5f));
            Matrix4x4[] rest = tube.RestPose;
            Matrix4x4 top = Matrix4x4.CreateTranslation(0, 1.2f, 0);
            Matrix4x4 bottom = Matrix4x4.CreateTranslation(0, -1.2f, 0);

            var results = new List<string>();
            bool ok = true;
            foreach (int n in new[] { 2, 8, 32, 64, 128, 192 })
            {
                byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
                {
                    for (int i = 0; i < n - 1; i++) s.DrawSkinned(h, rest, top, Tint);  // N-1 at top
                    s.DrawSkinned(h, rest, bottom, Tint);                                // the Nth, alone at bottom
                }).Handle, W, H);
                int bot = BottomOpaque(px);
                results.Add($"N={n}: bottom(lastDraw)={bot}");
                if (bot <= 100) ok = false;
            }

            Assert.True(ok, "the LAST skinned draw must render at every N. " + string.Join("  ", results));
            preview.Scene.UnloadSkinnedMesh(h);
        }

        // Mirrors the live structure: one fixed draw of mesh A, then many draws of a DIFFERENT mesh B. Asserts B's
        // draws render (B's last, alone at the bottom).
        [GpuFact]
        public void SecondMesh_AfterAFirstMeshDraw_StillRenders()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tubeA = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 8, 8, 6, Axis.Z);
            var tubeB = SkinnedMeshBuilder.BuildTube(0.4f, 4f, 10, 10, 8, Axis.Z);
            SkinnedMeshHandle a = preview.Scene.LoadSkinnedMesh(tubeA);
            SkinnedMeshHandle b = preview.Scene.LoadSkinnedMesh(tubeB);
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 5f, 5f));
            Matrix4x4 top = Matrix4x4.CreateTranslation(0, 1.2f, 0);
            Matrix4x4 bottom = Matrix4x4.CreateTranslation(0, -1.2f, 0);

            var results = new List<string>();
            bool ok = true;
            foreach (int n in new[] { 2, 32, 128, 192 })
            {
                byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
                {
                    s.DrawSkinned(a, tubeA.RestPose, top, Tint);                       // fixed draw of mesh A (top)
                    for (int i = 0; i < n - 1; i++) s.DrawSkinned(b, tubeB.RestPose, top, Tint);
                    s.DrawSkinned(b, tubeB.RestPose, bottom, Tint);                    // mesh B's last, alone at bottom
                }).Handle, W, H);
                int bot = BottomOpaque(px);
                results.Add($"N={n}: meshB_lastDraw={bot}");
                if (bot <= 100) ok = false;
            }
            Assert.True(ok, "mesh B's draws (after a mesh-A draw) must render. " + string.Join("  ", results));
            preview.Scene.UnloadSkinnedMesh(a);
            preview.Scene.UnloadSkinnedMesh(b);
        }
    }
}
