using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Exercises Scene3D's CPU skinning path on a live headless device: a skinned tube loads, the per-frame bone
    // palette composes, and a bent FK pose deforms the mesh (a bent capture differs from the rest-pose capture).
    // Scene3D skins on the CPU through the rigid ModelRenderer pipeline; the dormant GPU SkinnedModelRenderer /
    // SkinnedModelVert path was removed (the GPU bone read corrupted past element 0 on windowed Veldrid/Metal).
    // Skipped unless KE_GPU_TESTS=1.
    public sealed class Render3DSkinnedGpuTests
    {
        const int W = 128, H = 128;

        [GpuFact]
        public void SkinnedTube_Renders_AndBendingDeformsIt()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            using var preview = new Render3DPreview(gd, W, H);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z));
            // Frame the camera on the tube (it runs 0..4 along Z, centred ~ (0,0,2)).
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z); // same layout, for poses
            // Bend the tube into an arc via forward kinematics: walk the bones from base to tip accumulating a
            // small per-joint rotation about X, applied at the previous joint's position, so each downstream bone
            // (and its rings) swings progressively further off the Z axis. Rotating each bone in place about its
            // own centre does NOT bend the tube (the ring centres stay on the axis); the cumulative chain does.
            var bent = (Matrix4x4[])tube.RestPose.Clone();
            float perJoint = 0.35f; // radians added at each joint; over 6 bones the tip swings ~1.75 rad
            Matrix4x4 accum = Matrix4x4.Identity; // accumulated rotation of the chain so far
            Vector3 prevRest = tube.RestPose[0].Translation; // base joint rest position
            Vector3 tip = prevRest;                           // running deformed joint position
            for (int b = 0; b < tube.BoneCount; b++)
            {
                Vector3 restPos = tube.RestPose[b].Translation;
                // Advance the deformed tip along the current (already rotated) bone direction by the rest segment.
                Vector3 seg = Vector3.Transform(restPos - prevRest, accum);
                tip += seg;
                accum = Matrix4x4.CreateRotationX(perJoint) * accum; // add this joint's bend for downstream bones
                // World transform that maps the bone's rest origin to its new deformed position with the chain
                // rotation: translate the rest origin to the origin, rotate, then translate to the deformed tip.
                bent[b] = Matrix4x4.CreateTranslation(-restPos) * accum * Matrix4x4.CreateTranslation(tip);
                prevRest = restPos;
            }

            Texture2D restTex = preview.Capture(scene =>
                scene.DrawSkinned(h, tube.RestPose, Matrix4x4.Identity, new Color(0.8f, 0.4f, 0.3f, 1f)));
            byte[] rest = GpuReadback.ToRgba(gd, restTex.Handle, W, H);

            Texture2D bentTex = preview.Capture(scene =>
                scene.DrawSkinned(h, bent, Matrix4x4.Identity, new Color(0.8f, 0.4f, 0.3f, 1f)));
            byte[] bentPixels = GpuReadback.ToRgba(gd, bentTex.Handle, W, H);

            // The tube renders (some opaque pixels), and bending changes the silhouette (the two frames differ).
            int opaque = 0, diff = 0;
            for (int i = 0; i < rest.Length; i += 4)
            {
                if (rest[i + 3] > 200) opaque++;
                if (Math.Abs(rest[i + 3] - bentPixels[i + 3]) > 32) diff++;
            }
            Assert.True(opaque > 100, $"skinned tube should render opaque pixels, got {opaque}");
            Assert.True(diff > 50, $"bending should change the silhouette vs rest pose, differing pixels {diff}");

            preview.Scene.UnloadSkinnedMesh(h);
        }
    }
}
