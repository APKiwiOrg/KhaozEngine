using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Reproduces the live config: SpaceGame renders skinned tentacles through a MeshSceneRenderer that sets
    // RenderScale.MatchViewport (the engine resizes/recreates the model framebuffer to the viewport) + a large
    // (supersampled) target + a near-straight-down light. Rigid meshes render through it; skinned ones do NOT.
    // Bare Render3DPreview tests (FixedInternal) miss this. Draws a rigid box (top) and a skinned tube (bottom)
    // under MatchViewport and asserts BOTH render. Skipped unless KE_GPU_TESTS=1.
    public sealed class Render3DSkinnedMatchViewportGpuTests
    {
        const int W = 200, H = 200;
        static readonly Color Tint = new(0.8f, 0.4f, 0.3f, 1f);

        static (int top, int bot) Halves(byte[] px)
        {
            int top = 0, bot = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (px[(y * W + x) * 4 + 3] > 200) { if (y < H / 2) top++; else bot++; }
            return (top, bot);
        }

        static (int top, int bot) RenderBoth(IGpuDevice gd, RenderScale scale, bool straightDownLight)
        {
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.RenderScale = scale;
            if (straightDownLight) preview.Scene.Post.LightDirection = new Vector3(0.02f, -1f, 0.02f);

            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.0f));
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 8, 8, 6, Axis.Z);
            SkinnedMeshHandle skin = preview.Scene.LoadSkinnedMesh(tube);

            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(5f, 6f, 6f));
            Matrix4x4 top = Matrix4x4.CreateTranslation(0, 1.6f, 0);
            Matrix4x4 bottom = Matrix4x4.CreateTranslation(0, -1.6f, 0);

            byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
            {
                s.Draw(box, top, Tint);                        // rigid, top
                s.DrawSkinned(skin, tube.RestPose, bottom, Tint); // skinned, bottom
            }).Handle, W, H);
            var r = Halves(px);
            preview.Scene.UnloadSkinnedMesh(skin);
            return r;
        }

        // The live bone matrices have a det=-1 reflection in the X-Z block (symmetric, M11*M33-M13*M31=-1).
        // A reflection flips triangle winding AND normals. This checks whether a reflected skinned pose still
        // rasterizes (cull is None, so it should) - if it does NOT, the reflection is the cause of invisibility.
        [GpuFact]
        public void SkinnedWithReflectedBones_StillRasterizes()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 8, 8, 6, Axis.Z);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

            // Reflect each rest-pose bone across X (det -1), like the live SlathTentacle frames.
            var reflected = (Matrix4x4[])tube.RestPose.Clone();
            var reflect = Matrix4x4.CreateScale(-1f, 1f, 1f);
            for (int b = 0; b < reflected.Length; b++) reflected[b] = reflect * tube.RestPose[b];

            byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, reflected, Matrix4x4.Identity, Tint)).Handle, W, H);
            int opaque = 0; for (int p = 0; p < px.Length / 4; p++) if (px[p * 4 + 3] > 200) opaque++;
            Assert.True(opaque > 100, $"a reflected (det -1) skinned pose should still rasterize under cull None, got {opaque}");
            preview.Scene.UnloadSkinnedMesh(h);
        }

        [GpuFact]
        public void SkinnedRendersUnderMatchViewport_LikeRigid()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            // Baseline: the default fixed-internal path (bare-preview-like) renders both.
            var fixedRes = RenderBoth(gd, RenderScale.FixedInternal, straightDownLight: false);
            Assert.True(fixedRes.top > 50 && fixedRes.bot > 50,
                $"FixedInternal baseline: rigid(top)={fixedRes.top} skinned(bottom)={fixedRes.bot}");

            // The live config: MatchViewport + straight-down light. Rigid must still render; the report is that
            // skinned vanishes here.
            var match = RenderBoth(gd, RenderScale.MatchViewport, straightDownLight: true);
            Assert.True(match.top > 50, $"rigid should render under MatchViewport, got top={match.top}");
            Assert.True(match.bot > 50,
                $"SKINNED should render under MatchViewport+straight-down light, got bottom={match.bot} (rigid top={match.top})");
        }
    }
}
