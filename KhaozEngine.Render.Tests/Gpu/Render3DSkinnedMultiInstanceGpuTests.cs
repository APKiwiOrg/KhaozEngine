using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Regression for the multi-instance skinned bug: two instances of the SAME skinned mesh in ONE frame, each
    // with its OWN bone palette. The single-instance path (Render3DSkinnedGpuTests) never drew more than one
    // skinned mesh per frame; SpaceGame is the first consumer to draw many, and the 2nd+ rendered invisible /
    // garbage (each skinned draw now selects its bone slot via a per-draw dynamic offset). Skipped unless KE_GPU_TESTS=1.
    //
    // Framing-free design: both instances draw at Identity (proven single-tube framing), overlapping at the shared
    // base but the bent instance's arc swings into pixels the rest instance does not cover. instance 0 = rest,
    // instance 1 = bent. We measure instance 1's CONTRIBUTION = opaque pixels the rest-only frame does not cover.
    public sealed class Render3DSkinnedMultiInstanceGpuTests
    {
        const int W = 128, H = 128;
        static readonly Color Tint = new(0.8f, 0.4f, 0.3f, 1f);

        static Matrix4x4[] BentPose(SkinnedGltfMesh tube, float perJoint)
        {
            var bent = (Matrix4x4[])tube.RestPose.Clone();
            Matrix4x4 accum = Matrix4x4.Identity;
            Vector3 prevRest = tube.RestPose[0].Translation;
            Vector3 tip = prevRest;
            for (int b = 0; b < tube.BoneCount; b++)
            {
                Vector3 restPos = tube.RestPose[b].Translation;
                tip += Vector3.Transform(restPos - prevRest, accum);
                accum = Matrix4x4.CreateRotationX(perJoint) * accum;
                bent[b] = Matrix4x4.CreateTranslation(-restPos) * accum * Matrix4x4.CreateTranslation(tip);
                prevRest = restPos;
            }
            return bent;
        }

        static bool Op(byte[] px, int p) => px[p * 4 + 3] > 200;
        static int OpaqueCount(byte[] px) { int n = 0; for (int p = 0; p < px.Length / 4; p++) if (Op(px, p)) n++; return n; }
        static int OpaqueBeyond(byte[] a, byte[] b) { int n = 0; for (int p = 0; p < a.Length / 4; p++) if (Op(a, p) && !Op(b, p)) n++; return n; }

        [GpuFact]
        public void TwoInstancesOfSameMesh_EachReadsItsOwnBonePalette()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            using var preview = new Render3DPreview(gd, W, H);
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

            Matrix4x4[] rest = tube.RestPose;
            Matrix4x4[] bent = BentPose(tube, 0.35f);

            byte[] restOnly = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint)).Handle, W, H);
            byte[] bentOnly = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, bent, Matrix4x4.Identity, Tint)).Handle, W, H);

            int opaqueRest = OpaqueCount(restOnly);
            int bentContribRef = OpaqueBeyond(bentOnly, restOnly);
            Assert.True(opaqueRest > 100, $"rest reference should render, got {opaqueRest}");
            Assert.True(bentContribRef > 100, $"bent pose must differ from rest, got contribution {bentContribRef}");

            byte[] both = GpuReadback.ToRgba(gd, preview.Capture(s =>
            {
                s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint);   // instance 0
                s.DrawSkinned(h, bent, Matrix4x4.Identity, Tint);   // instance 1
            }).Handle, W, H);

            int opaqueBoth = OpaqueCount(both);
            int bentContribBoth = OpaqueBeyond(both, restOnly);

            Assert.True(opaqueBoth <= opaqueRest + bentContribRef + 0.1 * W * H,
                $"garbage fill: opaqueBoth={opaqueBoth} >> union ~{opaqueRest + bentContribRef}");
            Assert.True(bentContribBoth >= 0.6 * bentContribRef,
                $"instance 1 (bent) did not render its palette: contributed {bentContribBoth} px vs reference {bentContribRef} px");

            preview.Scene.UnloadSkinnedMesh(h);
        }
    }
}
