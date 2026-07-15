using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // On-device proof of the opt-in GPU skinning path (Scene3D.UseGpuSkinning), built on the fold-matrix binding the
    // spike proved (GpuSkinningReproGpuTests variant 3): the skinned vertex reads ONE combined resource buffer at set
    // 0 ({Mvp;Model;P;bones[128]}), a skinned ModelFrag variant reads frame+material at set 1 (fragment only). These
    // render the SAME posed tube through both paths and assert pixel parity within the golden tolerance, that the GPU
    // reads EVERY bone (a bent pose deforms, not just bones[0]), the rest-pose identity check (palette=identity must
    // render the undeformed mesh - the check that caught the old attempt's corruption), multi-character same-mesh
    // scenes with the flag on, and shadow parity flag-on vs flag-off. Skipped unless KE_GPU_TESTS is set.
    public sealed class Render3DGpuSkinningGpuTests
    {
        const int W = 128, H = 128;
        static readonly Color Tint = new(0.8f, 0.4f, 0.3f, 1f);
        // Cross-path tolerance: the CPU and GPU deforms compute the same math in float32 but round independently (FMA,
        // sample order), so silhouette-edge cells can differ slightly. A hair above the golden default (0.06) absorbs
        // that while still catching a real corruption (which collapses whole columns of cells well past this).
        const float ParityTol = 0.08f;

        // Bend the tube into an arc by forward kinematics: walk bones base->tip accumulating a per-joint X rotation
        // applied at the previous joint, so every downstream bone (bones 1..N-1, not just bones[0]) swings further off
        // axis. Identical to Render3DSkinnedGpuTests / the multi-instance test's pose.
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

        static SkinnedGltfMesh Tube() => SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
        static void FrameTube(Render3DPreview p) => p.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

        static bool Op(byte[] px, int p) => px[p * 4 + 3] > 200;
        static int OpaqueCount(byte[] px) { int n = 0; for (int p = 0; p < px.Length / 4; p++) if (Op(px, p)) n++; return n; }
        static int OpaqueBeyond(byte[] a, byte[] b) { int n = 0; for (int p = 0; p < a.Length / 4; p++) if (Op(a, p) && !Op(b, p)) n++; return n; }

        static float FrameDiff(byte[] a, byte[] b)
        {
            float[] ga = GoldenGrid.Downsample(a, W, H);
            float[] gb = GoldenGrid.Downsample(b, W, H);
            return GoldenGrid.Compare(ga, gb, ParityTol).WorstDiff;
        }

        static void AssertClose(byte[] a, byte[] b, string label)
        {
            var cmp = GoldenGrid.Compare(GoldenGrid.Downsample(a, W, H), GoldenGrid.Downsample(b, W, H), ParityTol);
            Assert.True(cmp.Passed,
                $"{label}: GPU-skinned frame diverged from the CPU path beyond tol {ParityTol} ({cmp.Offenders.Count} cells, worst {cmp.WorstDiff:0.###}).");
        }

        // Evidence PNGs land in <worktree>/gpu-skinning-evidence/ (under the worktree, as required). Best-effort.
        static void Dump(byte[] rgba, string name)
        {
            try
            {
                string dir = EvidenceDir();
                Directory.CreateDirectory(dir);
                PngWriter.Save(Path.Combine(dir, name), rgba, W, H);
            }
            catch { /* diagnostic only */ }
        }

        static string EvidenceDir([CallerFilePath] string thisFile = "")
        {
            // thisFile = <worktree>/KhaozEngine.Tests/Gpu/<this>.cs -> up three dirs = worktree root.
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
            return Path.Combine(root, "gpu-skinning-evidence");
        }

        // ---- Parity + "reads every bone": the bent pose deforms on the GPU (bones 1..N matter) and matches the CPU path. ----
        [GpuFact]
        public void CpuVsGpu_BentPose_Parity_AndBonesMatter()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tube = Tube();
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            FrameTube(preview);

            Matrix4x4[] rest = tube.RestPose;
            Matrix4x4[] bent = BentPose(tube, 0.35f);

            preview.Scene.UseGpuSkinning = false;
            byte[] cpuBent = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, bent, Matrix4x4.Identity, Tint)).Handle, W, H);

            preview.Scene.UseGpuSkinning = true;
            byte[] gpuBent = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, bent, Matrix4x4.Identity, Tint)).Handle, W, H);
            byte[] gpuRest = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint)).Handle, W, H);

            Dump(cpuBent, "gpu-skinning-cpu-bent.png");
            Dump(gpuBent, "gpu-skinning-gpu-bent.png");
            Dump(gpuRest, "gpu-skinning-gpu-rest.png");

            Assert.True(OpaqueCount(gpuBent) > 100, $"GPU-skinned tube should render, got {OpaqueCount(gpuBent)} opaque px");
            // The GPU deform actually happened (bones 1..N read): the bent silhouette moves well beyond the rest one.
            // If only bones[0] survived, the tube would stay straight and this contribution would be ~0.
            Assert.True(OpaqueBeyond(gpuBent, gpuRest) > 100,
                $"GPU bent pose did not deform vs rest (only bones[0]?): contribution {OpaqueBeyond(gpuBent, gpuRest)} px");
            // Pixel parity with the CPU path.
            AssertClose(gpuBent, cpuBent, "bent-pose parity");

            preview.Scene.UnloadSkinnedMesh(h);
        }

        // ---- Rest-pose identity: an identity palette (RestPose) must render the UNDEFORMED mesh on the GPU, matching
        //      the CPU path. This is the decisive corruption catcher - a bad bone read garbles even the identity pose. ----
        [GpuFact]
        public void RestPoseIdentity_GpuMatchesCpu()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tube = Tube();
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            FrameTube(preview);

            preview.Scene.UseGpuSkinning = false;
            byte[] cpuRest = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, tube.RestPose, Matrix4x4.Identity, Tint)).Handle, W, H);
            preview.Scene.UseGpuSkinning = true;
            byte[] gpuRest = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, tube.RestPose, Matrix4x4.Identity, Tint)).Handle, W, H);

            Dump(cpuRest, "gpu-skinning-cpu-rest-identity.png");
            Dump(gpuRest, "gpu-skinning-gpu-rest-identity.png");

            Assert.True(OpaqueCount(gpuRest) > 100, $"GPU rest pose should render the undeformed tube, got {OpaqueCount(gpuRest)} px");
            AssertClose(gpuRest, cpuRest, "rest-pose identity parity");

            preview.Scene.UnloadSkinnedMesh(h);
        }

        // ---- Textured skinned parity: exercises the set-1 skinned material set (CreateSkinnedMaterialSet) with a real
        //      albedo bound, vs the CPU set-0 material. The lit result must match the CPU path. ----
        [GpuFact]
        public void TexturedSkinned_CpuVsGpu_Parity()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var tube = Tube();
            Scene3D.TextureHandle tex = preview.Scene.LoadTexture(Checker(), 4, 4);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube, tex);
            FrameTube(preview);
            Matrix4x4[] bent = BentPose(tube, 0.35f);

            preview.Scene.UseGpuSkinning = false;
            byte[] cpu = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, bent, Matrix4x4.Identity, Color.White)).Handle, W, H);
            preview.Scene.UseGpuSkinning = true;
            byte[] gpu = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, bent, Matrix4x4.Identity, Color.White)).Handle, W, H);

            Dump(cpu, "gpu-skinning-cpu-textured.png");
            Dump(gpu, "gpu-skinning-gpu-textured.png");
            Assert.True(OpaqueCount(gpu) > 100, "textured GPU skinned tube should render");
            AssertClose(gpu, cpu, "textured parity");
            preview.Scene.UnloadSkinnedMesh(h);
        }

        // ---- Multi-character same-mesh with the flag ON: two instances of one skinned mesh, each with its own palette
        //      (rest + bent), in one frame. Mirrors Render3DSkinnedMultiInstanceGpuTests but on the GPU path: each
        //      draw selects its own combined-UBO slot via a per-draw dynamic offset, so the bent instance's arc must
        //      still contribute the pixels the rest instance does not cover (no slot bleed / garbage fill). ----
        [GpuFact]
        public void MultiInstance_SameMesh_FlagOn()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.UseGpuSkinning = true;
            var tube = Tube();
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(tube);
            FrameTube(preview);

            Matrix4x4[] rest = tube.RestPose;
            Matrix4x4[] bent = BentPose(tube, 0.35f);

            byte[] restOnly = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint)).Handle, W, H);
            byte[] bentOnly = GpuReadback.ToRgba(gd, preview.Capture(s => s.DrawSkinned(h, bent, Matrix4x4.Identity, Tint)).Handle, W, H);
            int opaqueRest = OpaqueCount(restOnly);
            int bentContribRef = OpaqueBeyond(bentOnly, restOnly);
            Assert.True(opaqueRest > 100, $"rest reference should render, got {opaqueRest}");
            Assert.True(bentContribRef > 100, $"bent pose must differ from rest, got {bentContribRef}");

            byte[] both = GpuReadback.ToRgba(gd, preview.Capture(s =>
            {
                s.DrawSkinned(h, rest, Matrix4x4.Identity, Tint);   // instance 0, slot 0
                s.DrawSkinned(h, bent, Matrix4x4.Identity, Tint);   // instance 1, slot 1 (its own palette)
            }).Handle, W, H);

            int opaqueBoth = OpaqueCount(both);
            int bentContribBoth = OpaqueBeyond(both, restOnly);
            Assert.True(opaqueBoth <= opaqueRest + bentContribRef + 0.1 * W * H,
                $"garbage fill on the GPU path: opaqueBoth={opaqueBoth} >> union ~{opaqueRest + bentContribRef}");
            Assert.True(bentContribBoth >= 0.6 * bentContribRef,
                $"instance 1 (bent) did not read its own combined-UBO slot: {bentContribBoth} px vs reference {bentContribRef} px");

            preview.Scene.UnloadSkinnedMesh(h);
        }

        // ---- Shadow parity: a floor + a skinned caster under the shadow-map tier, flag-off vs flag-on. The GPU
        //      shadow depth mirror (SkinnedShadowDepthVert, its own combined slot) must land the same shadow, so the
        //      full frames match and both show clearly-dark (shadowed) floor pixels. ----
        [GpuFact]
        public void ShadowParity_FlagOnVsOff()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            var scene = preview.Scene;
            scene.Post.TransparentBackground = false;
            scene.Post.Starfield = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowFocusRadius = 5f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            var tube = Tube();
            SkinnedMeshHandle h = scene.LoadSkinnedMesh(tube);
            Matrix4x4[] bent = BentPose(tube, 0.3f);
            Matrix4x4 caster = Matrix4x4.CreateTranslation(0f, 0.6f, 0f);

            Action<Scene3D> draw = s =>
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                s.DrawSkinned(h, bent, caster, Tint);
            };

            scene.UseGpuSkinning = false;
            byte[] cpu = GpuReadback.ToRgba(gd, preview.Capture(draw).Handle, W, H);
            scene.UseGpuSkinning = true;
            byte[] gpu = GpuReadback.ToRgba(gd, preview.Capture(draw).Handle, W, H);

            Dump(cpu, "gpu-skinning-shadow-cpu.png");
            Dump(gpu, "gpu-skinning-shadow-gpu.png");

            Assert.True(DarkOpaque(cpu) > 40, $"CPU shadow scene should cast a visible shadow, dark px {DarkOpaque(cpu)}");
            Assert.True(DarkOpaque(gpu) > 40, $"GPU shadow scene should cast a visible shadow, dark px {DarkOpaque(gpu)}");
            AssertClose(gpu, cpu, "shadow parity");

            scene.UnloadSkinnedMesh(h);
            scene.UnloadMesh(floor);
        }

        // ---- The flag defaults OFF, so every existing golden and consumer render is byte-identical until opted in. ----
        [GpuFact]
        public void UseGpuSkinning_DefaultsOff()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);
            Assert.False(preview.Scene.UseGpuSkinning, "GPU skinning must default OFF (byte-identical to the CPU path until opted in)");
        }

        // Clearly-dark opaque pixels (a shadow proxy on the lit floor).
        static int DarkOpaque(byte[] px)
        {
            int n = 0;
            for (int p = 0; p < px.Length / 4; p++)
            {
                int b = p * 4;
                if (px[b + 3] > 200 && px[b] < 90 && px[b + 1] < 90 && px[b + 2] < 90) n++;
            }
            return n;
        }

        static byte[] Checker()
        {
            var px = new byte[4 * 4 * 4];
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 4 + x) * 4;
                byte v = (byte)(((x + y) & 1) == 0 ? 235 : 90);
                px[i] = v; px[i + 1] = (byte)(v / 2); px[i + 2] = (byte)(255 - v); px[i + 3] = 255;
            }
            return px;
        }
    }
}
