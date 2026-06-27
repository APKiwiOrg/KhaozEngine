# Perspective-correct toon outline + 2 outline bugs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three `KhaozEngine.Render3D` outline defects exposed by the perspective `FollowCamera3D` (Bug A vertical-flip on pass-count parity, Bug B dead normal-edge term, Fix C non-linear depth threshold) plus an optional distance fade, keeping the orthographic path output-stable.

**Architecture:** All changes live in `Render3D`. A new headless `OutlineMath` helper (camera-depth extraction + NDC->view linearization) is TDD'd and mirrored by the GLSL `EdgeFrag`. `EdgeFrag` is reordered (Bug B) and gains a perspective-gated linearized relative depth test (Fix C) + optional distance fade (D); the `Edge` UBO grows from 3 to 4 `vec4`. `BlitFrag` gains a parity-driven V-flip (Bug A). `Scene3D` derives near/far/perspective from `ActiveCamera.Projection` (no public interface change) and feeds them through `PixelPostProcess.PrepareUniforms`.

**Tech Stack:** C# / net10.0, GLSL #version 450 cross-compiled to SPIR-V via the `KhaozEngine.Gpu` seam (Veldrid), xUnit (headless + gated `[GpuFact]` golden tests on Metal; D3D11/Vulkan via `cross-platform-gpu.yml`).

## Global Constraints

- **Version bump: MINOR** (adds the public `OutlineDistanceFade` knob + fade params to `PixelPostProcessSettings`). `7.50.0` -> `7.51.0` in `Directory.Build.props` `<KhaozEngine5xVersion>`.
- **Orthographic output must stay stable for the OUTLINE-ON goldens.** The 6 outline-on ortho goldens (`scene3d`, `scene3d_fill`, `telegraph_ground`, `scene3d_textured`, `scene3d_texbillboard`, `scene3d_beam`) MUST still pass without a re-bake. The perspective branch + fade are gated off for ortho; Bug B's Metal sampler reorder must not move them beyond the 0.06 golden tolerance. Verify, do not re-bake.
- **The 2 outline-OFF ortho goldens WILL be corrected by Bug A** (`scene3d_normalmap`, `scene3d_skinned_normalmap`, both `UseSmoothPreset()` => `Outline=false`). They currently encode the upside-down (parity-flipped) image; the Bug A fix flips them upright, so they MUST be re-baked on all 3 backends. This is the fix working, not a regression. (Empirically confirmed: outline-off renders vertically flipped.)
- **GPU golden discipline:** a golden baked only on Metal turns `main` RED on D3D11/Vulkan (`cross-platform-gpu.yml` is blocking on every push). Every new or re-baked golden is baked on all three: Metal locally (`KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1`), D3D11+Vulkan via `cross-platform-gpu.yml` `workflow_dispatch` `bake=true`, download artifacts, commit.
- **No em-dashes** anywhere (code comments, docs, commit messages, CHANGELOG/CHANGENOTES).
- **Release ritual (KhaozEngine/CLAUDE.md):** bump version -> CHANGELOG.md entry (newest-first, detailed) -> CHANGENOTES.md one-line digest -> update the 3 guard declarations (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example) -> `docs/USING-KHAOZENGINE.md` for the new knob -> `scripts/check-doc-versions.sh` -> `dotnet pack -c Release -o ./local-feed`.

---

## File Structure

- `KhaozEngine.Render3D/Internal/OutlineMath.cs` (CREATE) — internal helper: `CameraDepth` struct + `ExtractCameraDepth(Matrix4x4)` + `LinearizeDepth(float,float,float)`. Headless-testable; the GLSL `EdgeFrag` mirrors it (kept in sync, like `SurfaceShading.cs` mirrors `ModelFrag`).
- `KhaozEngine.Render3D/PixelPostProcessSettings.cs` (MODIFY) — add `OutlineDistanceFade` (bool, default false), `OutlineFadeStart`, `OutlineFadeEnd` (view-space distances).
- `KhaozEngine.Render3D/Internal/ShaderSources.cs` (MODIFY) — rewrite `EdgeFrag` (Bug B reorder + Fix C linearized relative depth + D fade); patch `BlitFrag` (Bug A parity V-flip).
- `KhaozEngine.Render3D/Rendering/PixelPostProcess.cs` (MODIFY) — grow `EdgeUbo` to 4 `vec4` (48->64 bytes); extend `PrepareUniforms` to take `CameraDepth`; set the new UBO fields; compute + pass `flipV` to `FinalUbo` in `Run`.
- `KhaozEngine.Render3D/Scene3D.cs` (MODIFY) — in `RenderInternal`, derive `CameraDepth` from `ActiveCamera.Projection` and pass it to `PrepareUniforms`.
- `KhaozEngine.Tests/OutlineMathTests.cs` (CREATE) — headless TDD for `OutlineMath`.
- `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` (MODIFY) — add `Golden3D_PerspectiveOutline` ([GpuFact]) + an outline-on/outline-off orientation A/B assert.
- `KhaozEngine.Tests/Gpu/goldens/perspective_outline.{metal,direct3d11,vulkan}.txt` (CREATE) — new golden, 3 backends.
- `KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.*.txt`, `scene3d_skinned_normalmap.*.txt` (RE-BAKE, 3 backends each) — corrected by Bug A.
- Docs: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

### Reference: confirmed root causes (from live probe)

- **Bug A:** every fullscreen post pass flips vertically; the on-screen orientation depends on the parity of (quantize + outline + blit) passes. outline-on = 2 passes (even) = upright; outline-off = 1 pass (odd) = flipped. Confirmed by PNG dump (offscreen path). Fix: blit flips its sampled V iff the preceding post-pass count (quantize + outline) is EVEN, which leaves outline-on byte-identical and makes every config upright.
- **Bug B:** `EdgeFrag` first-samples textures in order Color, Depth, Normal, but the resource layout binds Color(0), Normal(1), Depth(2). On Metal, SPIRV-Cross assigns MSL texture indices by first-sample order, so Normal/Depth are swapped (the exact bug documented at length in `ModelFrag` for Albedo/NormalMap/Roughness). The "normal term" reads depth data => contributes nothing. Fix: first-sample in binding order (Color, Normal, Depth). D3D11/Vulkan bind by decoration and are unaffected.
- **Fix C:** stored depth is `gl_Position.z / gl_Position.w` (NDC, non-linear under perspective). Linearize to view-space eye distance via near/far and use a depth delta RELATIVE to view depth. Ortho keeps the raw `abs(d-d0) > thresh` test, gated by a perspective flag.

---

## Task 1: `OutlineMath` headless helper (camera-depth + linearization)

**Files:**
- Create: `KhaozEngine.Render3D/Internal/OutlineMath.cs`
- Test: `KhaozEngine.Tests/OutlineMathTests.cs`

**Interfaces:**
- Produces:
  - `readonly struct CameraDepth { bool IsPerspective; float Near; float Far; }` (namespace `KhaozEngine.Render3D.Internal`)
  - `static CameraDepth OutlineMath.ExtractCameraDepth(Matrix4x4 projection)`
  - `static float OutlineMath.LinearizeDepth(float ndcDepth, float near, float far)`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/OutlineMathTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests
{
    public class OutlineMathTests
    {
        // NDC depth (System.Numerics perspective, D3D/[0,1] convention) for a given view-space eye distance.
        static float NdcFromView(float viewDist, float near, float far)
            => far * (viewDist - near) / (viewDist * (far - near));

        [Fact]
        public void Ortho_projection_is_flagged_non_perspective()
        {
            var ortho = Matrix4x4.CreateOrthographic(10f, 6f, 0.5f, 100f);
            CameraDepth cam = OutlineMath.ExtractCameraDepth(ortho);
            Assert.False(cam.IsPerspective);
        }

        [Fact]
        public void Perspective_projection_recovers_near_and_far()
        {
            var persp = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 500f);
            CameraDepth cam = OutlineMath.ExtractCameraDepth(persp);
            Assert.True(cam.IsPerspective);
            Assert.Equal(0.1f, cam.Near, 3);
            Assert.Equal(500f, cam.Far, 0);
        }

        [Fact]
        public void Linearize_maps_ndc_endpoints_to_near_and_far()
        {
            float n = 0.1f, f = 500f;
            Assert.Equal(n, OutlineMath.LinearizeDepth(NdcFromView(n, n, f), n, f), 3);
            Assert.Equal(f, OutlineMath.LinearizeDepth(NdcFromView(f, n, f), n, f), 0);
            // Round-trips an interior point.
            Assert.Equal(37.5f, OutlineMath.LinearizeDepth(NdcFromView(37.5f, n, f), n, f), 1);
        }

        [Fact]
        public void Relative_depth_metric_is_stable_across_zoom_while_raw_is_not()
        {
            // A receding plane: equal screen steps map to a ~constant MULTIPLICATIVE view-depth step (5%/px),
            // so the relative metric |dLin|/lin is constant with distance, but the raw NDC delta collapses far.
            const float n = 0.1f, f = 500f, step = 1.05f;
            float NearLo = 5f, NearHi = 5f * step;
            float FarLo = 300f, FarHi = 300f * step;

            float rawNear = MathF.Abs(NdcFromView(NearHi, n, f) - NdcFromView(NearLo, n, f));
            float rawFar = MathF.Abs(NdcFromView(FarHi, n, f) - NdcFromView(FarLo, n, f));

            float relNear = MathF.Abs(OutlineMath.LinearizeDepth(NdcFromView(NearHi, n, f), n, f)
                                    - OutlineMath.LinearizeDepth(NdcFromView(NearLo, n, f), n, f))
                            / OutlineMath.LinearizeDepth(NdcFromView(NearLo, n, f), n, f);
            float relFar = MathF.Abs(OutlineMath.LinearizeDepth(NdcFromView(FarHi, n, f), n, f)
                                   - OutlineMath.LinearizeDepth(NdcFromView(FarLo, n, f), n, f))
                           / OutlineMath.LinearizeDepth(NdcFromView(FarLo, n, f), n, f);

            // Raw NDC delta near is many times the far delta (non-linear compression) -> a fixed threshold flickers.
            Assert.True(rawNear > rawFar * 8f, $"raw not collapsing: near={rawNear}, far={rawFar}");
            // Relative linear metric is ~equal near and far (both ~0.05) -> a fixed threshold is stable.
            Assert.Equal(relNear, relFar, 2);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~OutlineMathTests"`
Expected: FAIL to compile (`OutlineMath` / `CameraDepth` do not exist).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render3D/Internal/OutlineMath.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>Camera depth parameters for the edge pass: whether the projection is perspective and its
    /// near/far planes. Extracted from the projection matrix so no camera-interface change is needed.</summary>
    internal readonly struct CameraDepth
    {
        public readonly bool IsPerspective;
        public readonly float Near;
        public readonly float Far;
        public CameraDepth(bool isPerspective, float near, float far)
        {
            IsPerspective = isPerspective; Near = near; Far = far;
        }
    }

    /// <summary>
    /// Pure depth math shared between the C# host (UBO plumbing + tests) and the GLSL <c>EdgeFrag</c>, which
    /// mirrors <see cref="LinearizeDepth"/> exactly (keep in sync, like SurfaceShading.cs mirrors ModelFrag).
    /// System.Numerics <c>CreatePerspectiveFieldOfView</c>/<c>CreatePerspective</c> produce a [0,1] NDC depth
    /// range with M34 == -1; orthographic projections have M34 == 0 (and M44 == 1).
    /// </summary>
    internal static class OutlineMath
    {
        /// <summary>Detect perspective vs orthographic and recover the near/far planes from a System.Numerics
        /// projection matrix. Perspective: M34 != 0, near = M43/M33, far = M43/(M33+1). Orthographic returns
        /// (false, 0, 0) - the edge pass uses the raw linear depth there and never calls
        /// <see cref="LinearizeDepth"/>.</summary>
        public static CameraDepth ExtractCameraDepth(Matrix4x4 p)
        {
            bool perspective = MathF.Abs(p.M34) > 1e-6f;
            if (!perspective) return new CameraDepth(false, 0f, 0f);
            float near = p.M43 / p.M33;
            float far = p.M43 / (p.M33 + 1f);
            return new CameraDepth(true, near, far);
        }

        /// <summary>Convert a stored NDC depth (gl_Position.z/gl_Position.w, [0,1] near->far) to view-space eye
        /// distance. Inverse of the perspective projection's depth mapping.</summary>
        public static float LinearizeDepth(float ndcDepth, float near, float far)
            => (near * far) / (far - ndcDepth * (far - near));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~OutlineMathTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Internal/OutlineMath.cs KhaozEngine.Tests/OutlineMathTests.cs
git commit -m "render3d: OutlineMath camera-depth + NDC->view linearization (Fix C math, headless-tested)"
```

---

## Task 2: New `PixelPostProcessSettings` knobs (Optional D)

**Files:**
- Modify: `KhaozEngine.Render3D/PixelPostProcessSettings.cs`
- Test: `KhaozEngine.Tests/OutlineMathTests.cs` (append a defaults test) — or a new small test file.

**Interfaces:**
- Produces (public fields on `PixelPostProcessSettings`):
  - `bool OutlineDistanceFade = false;`
  - `float OutlineFadeStart = 40f;`
  - `float OutlineFadeEnd = 120f;`

- [ ] **Step 1: Write the failing test**

Append to `KhaozEngine.Tests/OutlineMathTests.cs` (inside the class):

```csharp
        [Fact]
        public void Outline_distance_fade_defaults_off()
        {
            var s = new KhaozEngine.Render3D.PixelPostProcessSettings();
            Assert.False(s.OutlineDistanceFade);
            Assert.True(s.OutlineFadeStart < s.OutlineFadeEnd);
        }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Outline_distance_fade_defaults_off"`
Expected: FAIL to compile (fields do not exist).

- [ ] **Step 3: Add the fields**

In `KhaozEngine.Render3D/PixelPostProcessSettings.cs`, after the `OutlineNormalThreshold` field (around line 72), add:

```csharp
        /// <summary>Fade the edge outline out with distance so far foliage/terrain stops aliasing into mush.
        /// Default OFF (the ortho path and existing look are unchanged). When on, outline strength ramps from
        /// full at <see cref="OutlineFadeStart"/> view-space units to zero at <see cref="OutlineFadeEnd"/>.
        /// Only meaningful under a perspective camera.</summary>
        public bool OutlineDistanceFade = false;
        /// <summary>View-space eye distance where the outline begins to fade (see <see cref="OutlineDistanceFade"/>).</summary>
        public float OutlineFadeStart = 40f;
        /// <summary>View-space eye distance where the outline has fully faded (see <see cref="OutlineDistanceFade"/>).</summary>
        public float OutlineFadeEnd = 120f;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Outline_distance_fade_defaults_off"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/PixelPostProcessSettings.cs KhaozEngine.Tests/OutlineMathTests.cs
git commit -m "render3d: add OutlineDistanceFade knob (default off) to PixelPostProcessSettings"
```

---

## Task 3: Plumb camera depth + grow the Edge UBO (C# side; shader still old)

This task wires `CameraDepth` and the fade params through to the GPU WITHOUT changing the shader yet, so every render stays byte-identical (the new UBO fields are ignored by the current `EdgeFrag`). It is a safe checkpoint.

**Files:**
- Modify: `KhaozEngine.Render3D/Rendering/PixelPostProcess.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.cs:726` (the `_post.PrepareUniforms(...)` call in `RenderInternal`)

**Interfaces:**
- Consumes: `OutlineMath.ExtractCameraDepth`, `CameraDepth` (Task 1).
- Produces: `PixelPostProcess.PrepareUniforms(IGpuCommandList, RenderResources, PixelPostProcessSettings, in CameraDepth)`.

- [ ] **Step 1: Grow the Edge UBO struct + buffer**

In `PixelPostProcess.cs`, change the `EdgeUbo` struct (line 17) from 3 to 4 `vec4`:

```csharp
        struct EdgeUbo { public Vector4 OutlineColor; public Vector4 Texel; public Vector4 Thresh; public Vector4 Fade; }
```

Change the `_edgeBuf` allocation (line 39) from 48 to 64 bytes:

```csharp
            _edgeBuf = f.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer));
```

- [ ] **Step 2: Extend `PrepareUniforms` to take `CameraDepth` and fill the new fields**

In `PixelPostProcess.cs`, add the `using` if needed (`using KhaozEngine.Render3D.Internal;`) and change the `PrepareUniforms` signature + edge UBO build (lines 103-124):

```csharp
        public void PrepareUniforms(IGpuCommandList cl, RenderResources res, PixelPostProcessSettings s, in CameraDepth cam)
        {
            // ... palette block unchanged ...

            var edge = new EdgeUbo
            {
                OutlineColor = s.OutlineColor,
                // Texel.xy = 1/size; .z = isPerspective (gates Fix C); .w = distance-fade on.
                Texel = new Vector4(1f / res.Width, 1f / res.Height,
                                    cam.IsPerspective ? 1f : 0f,
                                    (cam.IsPerspective && s.OutlineDistanceFade) ? 1f : 0f),
                // Thresh.x = depth threshold; .y = normal threshold; .z = near; .w = far.
                Thresh = new Vector4(s.OutlineDepthThreshold, s.OutlineNormalThreshold, cam.Near, cam.Far),
                // Fade.x = fade start (view depth); .y = fade end.
                Fade = new Vector4(s.OutlineFadeStart, s.OutlineFadeEnd, 0f, 0f),
            };
            cl.UpdateBuffer(_edgeBuf, 0, in edge);

            // ... final UBO block unchanged for now (Bug A flip added in Task 5) ...
        }
```

(Keep the palette and final-UBO blocks exactly as they are; only the `EdgeUbo` build and the method signature change.)

- [ ] **Step 3: Pass `CameraDepth` from `Scene3D`**

In `Scene3D.cs` `RenderInternal` (line ~726), replace:

```csharp
            _post.PrepareUniforms(cl, _res, Post);
```

with:

```csharp
            var camDepth = Internal.OutlineMath.ExtractCameraDepth(ActiveCamera.Projection);
            _post.PrepareUniforms(cl, _res, Post, camDepth);
```

- [ ] **Step 4: Build + run the full headless suite (no behaviour change expected)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (GPU goldens skipped without `KE_GPU_TESTS=1`; everything else green).

- [ ] **Step 5: Verify the outline-on ortho goldens are still byte-identical (the shader is unchanged here)**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GoldenSnapshotTests"`
Expected: PASS (all existing goldens unchanged - the shader has not changed, only the ignored UBO fields).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Rendering/PixelPostProcess.cs KhaozEngine.Render3D/Scene3D.cs
git commit -m "render3d: plumb camera near/far + perspective flag into the Edge UBO (no behaviour change yet)"
```

---

## Task 4: `EdgeFrag` rewrite (Bug B reorder + Fix C linearized relative depth + D fade)

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs` (the `EdgeFrag` constant)

**Interfaces:**
- Consumes: the Edge UBO layout from Task 3 (`Texel.z`=isPerspective, `Texel.w`=fadeOn, `Thresh.zw`=near/far, `Fade.xy`=start/end).

- [ ] **Step 1: Replace `EdgeFrag`**

In `ShaderSources.cs`, replace the entire `EdgeFrag` constant with:

```csharp
        // ---- Depth/normal edge outline ----
        // Bug B fix: sample ColorTex, NormalTex, DepthTex UP FRONT in BINDING ORDER. On Metal SPIRV-Cross assigns
        // MSL texture indices by first-sample order, so sampling Depth before Normal (the old order) swapped the
        // two samplers and the normal-edge term silently read depth data (mirrors the ModelFrag Albedo/NormalMap/
        // Roughness fix; D3D11/Vulkan bind by decoration and are order-insensitive).
        // Fix C: under perspective the stored z/w is non-linear, so a fixed threshold pops on zoom/distance.
        // Linearize to view-space eye distance (Edge.Thresh.zw = near/far) and compare a depth delta RELATIVE to
        // view depth. Orthographic (Texel.z == 0) keeps the original raw abs(d-d0) > Thresh.x test, byte-identical.
        public const string EdgeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D ColorTex;
layout(set=0, binding=1) uniform texture2D NormalTex;
layout(set=0, binding=2) uniform texture2D DepthTex;
layout(set=0, binding=3) uniform sampler Samp;
layout(set=0, binding=4) uniform Edge { vec4 OutlineColor; vec4 Texel; vec4 Thresh; vec4 Fade; };
// Texel.xy=1/size, .z=isPerspective, .w=distanceFadeOn; Thresh.x=depth, .y=normal, .z=near, .w=far; Fade.xy=start/end
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float linearizeDepth(float d, float near, float far) { return (near * far) / (far - d * (far - near)); }
void main() {
    // Up-front, in binding order (Color, Normal, Depth) - see Bug B note above.
    vec4 baseSrc = texture(sampler2D(ColorTex, Samp), vUv);
    vec3 base = baseSrc.rgb;
    vec3 n0 = texture(sampler2D(NormalTex, Samp), vUv).rgb * 2.0 - 1.0;
    float d0 = texture(sampler2D(DepthTex, Samp), vUv).r;

    bool persp = Texel.z > 0.5;
    float near = Thresh.z, far = Thresh.w;
    float lin0 = persp ? linearizeDepth(d0, near, far) : d0;

    float edge = 0.0;
    vec2 offs[4] = vec2[4](vec2(Texel.x, 0), vec2(-Texel.x, 0), vec2(0, Texel.y), vec2(0, -Texel.y));
    for (int i = 0; i < 4; i++) {
        vec3 n = texture(sampler2D(NormalTex, Samp), vUv + offs[i]).rgb * 2.0 - 1.0;
        float d = texture(sampler2D(DepthTex, Samp), vUv + offs[i]).r;
        if (persp) {
            float lin = linearizeDepth(d, near, far);
            if (abs(lin - lin0) > Thresh.x * lin0) edge = 1.0;   // distance-relative depth edge
        } else {
            if (abs(d - d0) > Thresh.x) edge = 1.0;              // ortho: raw linear z/w (UNCHANGED)
        }
        if ((1.0 - dot(n, n0)) > Thresh.y) edge = 1.0;          // normal-crease edge (now reads real normals)
    }

    if (Texel.w > 0.5) edge *= 1.0 - smoothstep(Fade.x, Fade.y, lin0);   // optional distance fade (default off)

    oColor = vec4(mix(base, OutlineColor.rgb, edge), baseSrc.a); // preserve background alpha marker
}";
```

- [ ] **Step 2: Build**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: build succeeds (GLSL is cross-compiled at runtime, not build time, so a syntax slip surfaces in Step 3, not here).

- [ ] **Step 3: Verify the 6 OUTLINE-ON ortho goldens still pass (the critical "ortho stable" gate)**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GoldenSnapshotTests"`
Expected: the 6 outline-on goldens (`scene3d`, `scene3d_fill`, `telegraph_ground`, `scene3d_textured`, `scene3d_texbillboard`, `scene3d_beam`) and the 2D goldens PASS. The 2 outline-off normalmap goldens are still on the OLD (flipped) image and will only move once Task 5 lands - they should still PASS here (Bug A not yet applied; the outline pass does not run for them).
If any outline-on golden FAILS: the Metal Bug B reorder moved it beyond tolerance. STOP and report - this means the ortho outline visibly changed and needs a decision (it is a real correction, but the spec wants outline-on stable).

- [ ] **Step 4: Eyeball the perspective fix with a throwaway PNG dump (sanity, not committed)**

Write a temporary `[GpuFact]` (delete after) that captures a perspective scene with `Post.OutlineDepthThreshold = 0.06f` (medium) and dumps a PNG via `KhaozEngine.Imaging.PngWriter.Save`, then `Read` it: confirm a clean outline on silhouettes AND interior creases (Bug B), stable. Remove the file before committing.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Internal/ShaderSources.cs
git commit -m "render3d: EdgeFrag perspective-correct depth + Metal normal/depth sampler reorder (Bug B, Fix C, fade)"
```

---

## Task 5: `BlitFrag` parity V-flip (Bug A)

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs` (the `BlitFrag` constant)
- Modify: `KhaozEngine.Render3D/Rendering/PixelPostProcess.cs` (`FinalUbo` build in `PrepareUniforms`, and `Run`)

**Interfaces:**
- Produces: `FinalUbo.Params.z` carries `flipV` (1 = flip the sampled V).

- [ ] **Step 1: Patch `BlitFrag` to flip the sampled V when `Params.z > 0.5`**

In `ShaderSources.cs`, in `BlitFrag`, change the `Final` UBO comment + the sample. Replace the `main()` body's first lines:

```glsl
void main() {
    vec2 suv = (Params.z > 0.5) ? vec2(vUv.x, 1.0 - vUv.y) : vUv;   // Bug A: cancel the pass-parity flip
    vec4 s = texture(sampler2D(Src, Samp), suv);
    vec3 col = s.rgb;
    if (Params.x > 0.5 && s.a < 0.5) {                   // background (alpha marker) -> stars
        vec2 cell = floor(vUv * vec2(220.0, 124.0));
```

(Keep the rest of `BlitFrag` unchanged. `Params.x`=starsOn, `.y`=transparentBg, now `.z`=flipV. The starfield still uses screen-space `vUv`.)

Also update the `Final` UBO comment line to: `// Params.x=starsOn, .y=transparentBg, .z=flipV`.

- [ ] **Step 2: Set `flipV` in the Final UBO from the pass-count parity**

In `PixelPostProcess.cs` `PrepareUniforms`, the Final UBO is currently built there but the flip depends on `s.Quantize`/`s.Outline`. Build `flipV` from the preceding post-pass count parity:

```csharp
            // Bug A: each fullscreen post pass flips vertically; the on-screen orientation depends on the parity of
            // (quantize + outline + blit). The blit cancels it so EVERY config is upright: flip the sampled V iff
            // the number of preceding post passes (quantize + outline) is EVEN. Outline-on/quantize-off (the default
            // and the committed outline-on goldens) has 1 preceding pass (odd) => no flip => byte-identical.
            int precedingPasses = (s.Quantize ? 1 : 0) + (s.Outline ? 1 : 0);
            bool flipV = (precedingPasses % 2) == 0;

            var final = new FinalUbo
            {
                BgColor = s.BackgroundColor,
                Params = new Vector4(s.Starfield ? 1f : 0f, s.TransparentBackground ? 1f : 0f, flipV ? 1f : 0f, 0f),
            };
            cl.UpdateBuffer(_finalBuf, 0, in final);
```

(The `flipV` rule depends only on `s`, so computing it in `PrepareUniforms` matches `Run`'s pass sequence exactly. No change needed in `Run`.)

- [ ] **Step 3: Add a GPU orientation A/B test (outline-on and outline-off both upright)**

Append to `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs`:

```csharp
        // Bug A: outline-on and outline-off must render the SAME vertical orientation. Render a vertically
        // asymmetric scene (bright emissive sphere high in the world => near the TOP of the frame) both ways and
        // assert the bright band lands in the top third in BOTH (top mean brightness > bottom mean brightness).
        [GpuFact]
        public void Golden3D_OutlineToggle_DoesNotFlip()
        {
            float TopMinusBottom(bool outline)
            {
                MeshHandle floor = default, sphere = default;
                byte[] rgba = Render3DSnapshot.Capture(W, H,
                    setup: scene =>
                    {
                        floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                        sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.8f));
                        scene.Post.Outline = outline;
                        scene.Post.Starfield = false;
                        scene.Camera.Frame(new Vector3(0, 0.5f, 0), new Vector3(6f, 5f, 6f));
                    },
                    drawFrame: scene =>
                    {
                        scene.Draw(floor, Matrix4x4.Identity);
                        scene.Draw(sphere, Matrix4x4.CreateTranslation(0, 3f, 0),
                            new Color(1f, 0.1f, 0.1f, 1f), Material.Glowing(new Color(1f, 0.1f, 0.1f, 1f)));
                    },
                    frames: 2);
                double top = 0, bot = 0; int nt = 0, nb = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W + x) * 4;
                        double r = rgba[i] / 255.0;
                        if (y < H / 3) { top += r; nt++; } else if (y >= 2 * H / 3) { bot += r; nb++; }
                    }
                return (float)(top / nt - bot / nb);
            }
            // The bright red sphere sits high => the top third is redder than the bottom in BOTH configs.
            Assert.True(TopMinusBottom(true) > 0.05f, "outline-on is upside down");
            Assert.True(TopMinusBottom(false) > 0.05f, "outline-off is upside down (Bug A)");
        }
```

- [ ] **Step 4: Run the A/B orientation test**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_OutlineToggle_DoesNotFlip"`
Expected: PASS (before this task the outline-off branch would have failed; now both are upright).

- [ ] **Step 5: Re-bake the 2 outline-OFF ortho goldens on Metal (corrected by Bug A)**

The normalmap goldens encode the old upside-down image and now fail. Re-bake ONLY those two on Metal:

Run: `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_NormalRoughness|FullyQualifiedName~Golden3D_SkinnedNormalRoughness"`
Then confirm: `KE_GPU_TESTS=1 dotnet test ... --filter "FullyQualifiedName~GoldenSnapshotTests"` PASSES for all on Metal.

- [ ] **Step 6: Commit (Metal goldens; D3D11/Vulkan re-bake happens in Task 7)**

```bash
git add KhaozEngine.Render3D/Internal/ShaderSources.cs KhaozEngine.Render3D/Rendering/PixelPostProcess.cs \
        KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs \
        KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.metal.txt \
        KhaozEngine.Tests/Gpu/goldens/scene3d_skinned_normalmap.metal.txt
git commit -m "render3d: BlitFrag parity V-flip so outline on/off are both upright (Bug A); rebake outline-off metal goldens"
```

---

## Task 6: New perspective-outline golden

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs`
- Create: `KhaozEngine.Tests/Gpu/goldens/perspective_outline.metal.txt`

- [ ] **Step 1: Add the perspective golden test**

Append to `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` (uses a perspective `FollowCamera3D` via `CameraOverride`, a medium depth threshold, and an interior crease via a box so Bug B's normal term is exercised):

```csharp
        // Perspective-camera outline: locks the corrected stable outline (Fix C linearized relative depth) AND
        // Bug B's interior-crease normal term under a perspective FollowCamera3D. Upright (Bug A) by construction.
        [GpuFact]
        public void Golden3D_PerspectiveOutline()
        {
            MeshHandle floor = default, box = default, sphere = default;
            var follow = new FollowCamera3D
            {
                Target = new Vector3(0f, 0.5f, 0f),
                Pitch = 0.45f, Yaw = 0.5f, Distance = 9f, HeightOffset = 1.2f,
                AspectRatio = (float)W / H,
            };
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.7f));
                    scene.Post.Starfield = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Outline = true;
                    scene.Post.OutlineDepthThreshold = 0.06f;   // medium
                    scene.Post.OutlineNormalThreshold = 0.45f;
                    scene.CameraOverride = follow;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0, 0, 0));
                    scene.Draw(box, Matrix4x4.CreateTranslation(-1.6f, 0.6f, 0.4f),
                        new Color(0.2f, 0.55f, 0.85f, 1f));
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(1.5f, 0.7f, -0.6f),
                        new Color(0.85f, 0.35f, 0.2f, 1f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("perspective_outline", rgba, W, H);
        }
```

- [ ] **Step 2: Bake on Metal**

Run: `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_PerspectiveOutline"`
Then verify: `KE_GPU_TESTS=1 dotnet test ... --filter "FullyQualifiedName~Golden3D_PerspectiveOutline"` PASS.

- [ ] **Step 3: Confirm the full Metal golden suite is green**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GoldenSnapshotTests"`
Expected: all PASS on Metal.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs KhaozEngine.Tests/Gpu/goldens/perspective_outline.metal.txt
git commit -m "test(gpu): perspective-outline golden (Fix C stable outline + Bug B crease + Bug A upright), metal bake"
```

---

## Task 7: Cross-platform bake (D3D11 + Vulkan) via CI

The new `perspective_outline` golden and the re-baked `scene3d_normalmap` / `scene3d_skinned_normalmap` exist only on Metal. `cross-platform-gpu.yml` and `CrossBackendGoldenTests` go RED until D3D11 + Vulkan are baked. Bake them on CI BEFORE pushing the release tag.

- [ ] **Step 1: Push the feature branch and trigger the bake workflow**

```bash
git push -u origin worktree-feature+perspective-outline
gh workflow run cross-platform-gpu.yml -f bake=true --ref worktree-feature+perspective-outline
```

(Confirm the exact input name with `gh workflow view cross-platform-gpu.yml` / inspect `.github/workflows/cross-platform-gpu.yml` `workflow_dispatch` inputs first.)

- [ ] **Step 2: Wait for the run, download the baked artifacts**

```bash
gh run watch <run-id>
gh run download <run-id> -n <goldens-artifact-name> -D /tmp/ci-goldens
```

- [ ] **Step 3: Copy the D3D11 + Vulkan grids into the goldens dir and commit**

Copy `perspective_outline.direct3d11.txt`, `perspective_outline.vulkan.txt`, and the re-baked `scene3d_normalmap.{direct3d11,vulkan}.txt`, `scene3d_skinned_normalmap.{direct3d11,vulkan}.txt` from the artifact into `KhaozEngine.Tests/Gpu/goldens/`.

```bash
git add KhaozEngine.Tests/Gpu/goldens/perspective_outline.direct3d11.txt \
        KhaozEngine.Tests/Gpu/goldens/perspective_outline.vulkan.txt \
        KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.direct3d11.txt \
        KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.vulkan.txt \
        KhaozEngine.Tests/Gpu/goldens/scene3d_skinned_normalmap.direct3d11.txt \
        KhaozEngine.Tests/Gpu/goldens/scene3d_skinned_normalmap.vulkan.txt
git commit -m "test(gpu): D3D11+Vulkan bakes for perspective-outline + corrected outline-off goldens"
```

- [ ] **Step 4: Confirm `CrossBackendGoldenTests` passes (headless, no GPU)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CrossBackendGoldenTests"`
Expected: PASS (all three backends agree within the 0.20 cross-backend tolerance for every scene).

---

## Task 8: Docs + version bump + release

**Files:** `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>7.50.0</KhaozEngine5xVersion>` to `7.51.0`.

- [ ] **Step 2: CHANGELOG.md (newest-first detailed entry)**

Add a `## 7.51.0` entry covering: Bug A (parity V-flip so outline on/off + any quantize/dither combo are upright), Bug B (Metal normal/depth sampler reorder; interior-crease normal term now works), Fix C (perspective-correct linearized relative depth threshold; ortho unchanged), Optional D (`OutlineDistanceFade` knob, default off), new `perspective_outline` golden + corrected outline-off goldens. No em-dashes.

- [ ] **Step 3: CHANGENOTES.md (one-line digest, newest-first)**

One or two sentences: perspective-correct toon outline + two outline bug fixes (vertical-flip parity, dead normal-edge term on Metal) + optional distance fade.

- [ ] **Step 4: Update the 3 guard declarations**

`docs/CONSUMERS.md` "Engine current version" -> 7.51.0; `docs/ROADMAP.md` "Current released version" -> 7.51.0; `README.md` `<PackageReference>` example version -> 7.51.0.

- [ ] **Step 5: `docs/USING-KHAOZENGINE.md`** — document `OutlineDistanceFade` / `OutlineFadeStart` / `OutlineFadeEnd` in the post-process settings section, and note the outline is now perspective-correct (stable on zoom under a perspective camera).

- [ ] **Step 6: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: PASS (all three declarations match 7.51.0).

- [ ] **Step 7: Grep for stale references**

Run: `grep -rn "OutlineDistanceFade\|perspective_outline" --include='*.md' . | grep -v plans` and confirm every doc that should mention the new knob does.

- [ ] **Step 8: Full headless suite + Metal goldens green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (headless) and `KE_GPU_TESTS=1 dotnet test ... --filter GoldenSnapshotTests` (Metal). Both PASS.

- [ ] **Step 9: Pack**

Run: `dotnet pack -c Release -o ./local-feed`
Expected: all packages pack at 7.51.0.

- [ ] **Step 10: Commit**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md docs/USING-KHAOZENGINE.md
git commit -m "docs(7.51.0): perspective-correct outline + 2 outline bug fixes + OutlineDistanceFade"
```

---

## Task 9: Merge + release (autonomous, per CLAUDE.md)

- [ ] **Step 1: Merge to main**

```bash
git checkout main
git merge --no-ff worktree-feature+perspective-outline
```

- [ ] **Step 2: Repack from the main root** (the worktree's `local-feed` is removed on cleanup)

```bash
mkdir -p local-feed && dotnet pack -c Release -o ./local-feed
```

- [ ] **Step 3: Full test on the merged result**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` and `KE_GPU_TESTS=1 dotnet test ... --filter GoldenSnapshotTests`
Expected: PASS.

- [ ] **Step 4: Tag + push**

```bash
git tag v7.51.0
git push origin main
git push origin v7.51.0
```

- [ ] **Step 5: Delete the feature branch (local + the remote bake branch) and clean up the worktree**

```bash
git push origin --delete worktree-feature+perspective-outline
git fetch --prune
# Then ExitWorktree (remove) for the worktree, and delete the local branch.
```

- [ ] **Step 6: Confirm CI** (`cross-platform-gpu.yml` verify run + publish on the `v7.51.0` tag) goes green.

---

## Self-Review

**Spec coverage:** Bug A -> Task 5; Bug B -> Task 4 (reorder); Fix C -> Tasks 1, 3, 4; Optional D -> Tasks 2, 4. Ortho-byte-identical -> Tasks 3/4 verification gates (outline-on goldens unchanged; persp + fade gated). New perspective golden + cross-platform bake -> Tasks 6, 7. Headless edge-math TDD -> Task 1. Docs + version -> Task 8. Release -> Task 9. The one deviation from a literal reading of "no ortho golden moves": the 2 outline-OFF goldens are necessarily corrected by Bug A (documented in Global Constraints + Task 5); the 6 outline-ON goldens stay put.

**Type consistency:** `CameraDepth` (Task 1) consumed by `PrepareUniforms(..., in CameraDepth)` (Task 3) and `ExtractCameraDepth` call in `Scene3D` (Task 3). `EdgeUbo` 4-vec4 layout (Task 3) matches `EdgeFrag`'s `Edge` block (Task 4). `FinalUbo.Params.z` = flipV set in Task 5 matches `BlitFrag` (Task 5).
