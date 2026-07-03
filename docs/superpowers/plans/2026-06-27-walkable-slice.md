# Walkable overworld slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the shipped analytic terrain walkable in a window: a third-person follow camera orbiting behind a greybox capsule that the player drives over `TerrainPresets.Clearing()` with WASD + mouse + scroll.

**Architecture:** Three reusable engine pieces plus one throwaway sample. `FollowCamera3D` (perspective orbit-behind camera) and `FollowCameraController` (reads the `InputState` snapshot) are siblings of `IsoCamera3D`/`IsoCameraController` in `KhaozEngine.Render3D`. `Scene3D` gets an additive `CameraOverride` so a sibling camera can drive the render path. `CharacterController3D` (terrain-agnostic locomotion via a ground-height delegate) lives in `KhaozEngine.Game.Render3D`. `TerrainWalkSample` is a new windowed `Exe` (`IsPackable=false`) subclassing `GameApp3D`, wiring terrain + character + camera together.

**Tech Stack:** C# / net10.0, `System.Numerics`, xUnit (headless), `KhaozEngine.Render3D` / `Game.Render3D` / `Terrain` / `Terrain.Render3D` / `Windowing` / `Game`.

## Global Constraints

- One shared engine version line `<KhaozEngineVersion>` in `Directory.Build.props`; this is a **minor** bump `7.43.0` -> `7.44.0` (additive public API in two EXISTING packages: `KhaozEngine.Render3D` + `KhaozEngine.Game.Render3D`; NO new package, so no package-catalog churn).
- No em-dashes / en-dashes in any output (code comments, docs, commits).
- Input rule: only `AppWindow` touches windowing input statics. The controllers read the immutable `InputState` snapshot passed in; they touch no statics.
- New behaviour ships with a headless test in `KhaozEngine.Tests` (construct `InputState`/state frame-by-frame; `dt` is a plain `float` in seconds). NO GPU device in tests. The windowed sample is not unit-tested.
- Tuning values (camera distance/pitch limits/sensitivity, walk/run speeds, capsule half-height) are public fields with sane defaults, not hardcoded deep.
- STAY IN SCOPE: no animation/walk-cycle, no netcode movement, no chunk streaming, no prop/obstacle collision, no jump/gravity/physics beyond ground-clamp.
- Release ritual at the end: bump `Directory.Build.props`, `CHANGELOG.md` + `CHANGENOTES.md`, the 3 guard declarations (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example), `docs/USING-KHAOZENGINE.md` usage section, run `scripts/check-doc-versions.sh`, `dotnet test`, `dotnet pack -c Release -o ./local-feed`.

## File Structure

- Create: `KhaozEngine.Render3D/Camera/FollowCamera3D.cs` - perspective orbit-behind camera, implements `IIsoCamera3D`.
- Create: `KhaozEngine.Render3D/Camera/FollowCameraController.cs` - drag-orbit + scroll-zoom controller reading `InputState`.
- Modify: `KhaozEngine.Render3D/Scene3D.cs` - add `CameraOverride` property + route render-time camera reads through it.
- Create: `KhaozEngine.Game.Render3D/CharacterController3D.cs` - terrain-agnostic WASD locomotion + ground-clamp.
- Create: `TerrainWalkSample/TerrainWalkSample.csproj` + `TerrainWalkSample/Program.cs` - the windowed slice.
- Create test: `KhaozEngine.Tests/Render3D/FollowCamera3DTests.cs`
- Create test: `KhaozEngine.Tests/Render3D/FollowCameraControllerTests.cs`
- Create test: `KhaozEngine.Tests/Render3D/CharacterController3DTests.cs`
- Modify (release): `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

The `KhaozEngine.Tests.csproj` already references `Render3D` + `Game.Render3D`, so no test-project wiring is needed.

---

### Task 1: FollowCamera3D (perspective orbit-behind camera)

**Files:**
- Create: `KhaozEngine.Render3D/Camera/FollowCamera3D.cs`
- Test: `KhaozEngine.Tests/Render3D/FollowCamera3DTests.cs`

**Interfaces:**
- Consumes: `IIsoCamera3D` (existing read-only camera surface: `View`/`Projection`/`ViewProjection`/`Eye`/`Forward`).
- Produces: `FollowCamera3D` with public `Vector3 Target`; `float Yaw`; `float Pitch` (clamped to `[MinPitch,MaxPitch]` in setter); `float Distance` (clamped to `[MinDistance,MaxDistance]` in setter); fields `MinPitch`/`MaxPitch`/`MinDistance`/`MaxDistance`/`HeightOffset`/`FieldOfView`/`AspectRatio`/`NearPlane`/`FarPlane`; `Eye`/`Forward`/`View`/`Projection`/`ViewProjection`; `Ray ScreenToRay(Vector2, int, int)`; `Vector3 ScreenToGround(Vector2, int, int, float)`. Convention mirrors `IsoCamera3D`: `DirToEye = normalize(cosPitch*sinYaw, sinPitch, cosPitch*cosYaw)`, `Eye = Target + DirToEye*Distance + (0,HeightOffset,0)`, `View = LookAt(Eye, Target, +Y)`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Render3D/FollowCamera3DTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class FollowCamera3DTests
    {
        [Fact]
        public void Eye_is_behind_target_along_yaw_pitch_distance()
        {
            // Yaw 0, Pitch 0, no height offset: eye sits +Z of the target by Distance, looking -Z.
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
            Assert.True(Vector3.Distance(cam.Forward, new Vector3(0, 0, -1)) < 1e-4f, cam.Forward.ToString());
        }

        [Fact]
        public void Height_offset_raises_the_eye()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 2f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 2, 10)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void Camera_always_looks_at_the_target()
        {
            foreach (var (yaw, pitch, dist) in new[] { (0f, 0.3f, 6f), (1.2f, 0.8f, 12f), (-2f, 0.1f, 4f) })
            {
                var cam = new FollowCamera3D { Target = new Vector3(3, 1, -2), Yaw = yaw, HeightOffset = 1f };
                cam.Pitch = pitch; cam.Distance = dist;
                Vector3 inView = Vector3.Transform(cam.Target, cam.View);   // target in view space
                Assert.True(MathF.Abs(inView.X) < 1e-3f && MathF.Abs(inView.Y) < 1e-3f, inView.ToString());
                Assert.True(inView.Z < 0f, $"target should be in front (-Z): {inView.Z}");
            }
        }

        [Fact]
        public void Pitch_clamps_to_its_range()
        {
            var cam = new FollowCamera3D();
            cam.Pitch = 100f;                       // absurdly high
            Assert.Equal(cam.MaxPitch, cam.Pitch, 5);
            cam.Pitch = -100f;                      // absurdly low
            Assert.Equal(cam.MinPitch, cam.Pitch, 5);
        }

        [Fact]
        public void Distance_clamps_to_min_max()
        {
            var cam = new FollowCamera3D();
            cam.Distance = 1e6f;
            Assert.Equal(cam.MaxDistance, cam.Distance, 5);
            cam.Distance = -50f;
            Assert.Equal(cam.MinDistance, cam.Distance, 5);
        }

        [Fact]
        public void Target_projects_to_screen_center()
        {
            var cam = new FollowCamera3D { Target = new Vector3(2, 0.5f, 1), AspectRatio = 1.6f };
            cam.Pitch = 0.4f; cam.Distance = 8f;
            Vector4 clip = Vector4.Transform(new Vector4(cam.Target, 1f), cam.ViewProjection);
            Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
            Assert.True(MathF.Abs(ndc.X) < 1e-3f && MathF.Abs(ndc.Y) < 1e-3f, ndc.ToString());
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FollowCamera3DTests`
Expected: FAIL/build error - `FollowCamera3D` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Render3D/Camera/FollowCamera3D.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Third-person follow camera: a perspective camera that orbits behind a moving <see cref="Target"/> at a
    /// clamped <see cref="Pitch"/> and <see cref="Distance"/>, always looking at the target. Sibling of
    /// <see cref="IsoCamera3D"/> (same Y-up right-handed convention, same Eye/Forward/ScreenToGround helpers) but
    /// perspective so scroll-zoom-via-distance reads naturally. Pure System.Numerics, no GPU and no input types;
    /// drive it with a <see cref="FollowCameraController"/> or set the fields directly.
    ///
    /// Convention (matches IsoCamera3D): dirToEye = normalize(cosP*sinYaw, sinP, cosP*cosYaw),
    /// Eye = Target + dirToEye*Distance + (0, HeightOffset, 0), looking at Target.
    /// </summary>
    public sealed class FollowCamera3D : IIsoCamera3D
    {
        /// <summary>World-space point the camera follows (the character position).</summary>
        public Vector3 Target = Vector3.Zero;
        /// <summary>Orbit angle about the Y (up) axis, radians. Yaw 0 puts the eye on +Z looking toward -Z.</summary>
        public float Yaw = 0f;

        /// <summary>Lower clamp for <see cref="Pitch"/>, radians (kept &gt; 0 so the view never goes flat). Default ~6 deg.</summary>
        public float MinPitch = MathF.PI / 30f;
        /// <summary>Upper clamp for <see cref="Pitch"/>, radians (kept &lt; 90 deg so LookAt never degenerates). Default ~80 deg.</summary>
        public float MaxPitch = MathF.PI * 0.45f;
        /// <summary>Nearest the eye may sit to the target. Default 2.</summary>
        public float MinDistance = 2f;
        /// <summary>Farthest the eye may sit from the target. Default 30.</summary>
        public float MaxDistance = 30f;
        /// <summary>Eye height added above the target so the camera looks slightly down at the character. Default 1.</summary>
        public float HeightOffset = 1f;

        /// <summary>Vertical field of view, radians. Default 60 deg.</summary>
        public float FieldOfView = MathF.PI / 3f;
        /// <summary>Viewport aspect (width/height). Set this from the framebuffer each frame.</summary>
        public float AspectRatio = 16f / 9f;
        public float NearPlane = 0.1f;
        public float FarPlane = 500f;

        float _pitch = MathF.PI / 6f;   // 30 deg, a comfortable default tilt
        float _distance = 8f;

        /// <summary>Tilt above the horizontal, radians, clamped to [<see cref="MinPitch"/>, <see cref="MaxPitch"/>].</summary>
        public float Pitch
        {
            get => _pitch;
            set => _pitch = Math.Clamp(value, MinPitch, MaxPitch);
        }

        /// <summary>Eye distance from the target, clamped to [<see cref="MinDistance"/>, <see cref="MaxDistance"/>].</summary>
        public float Distance
        {
            get => _distance;
            set => _distance = Math.Clamp(value, MinDistance, MaxDistance);
        }

        Vector3 DirToEye
        {
            get
            {
                float cP = MathF.Cos(_pitch), sP = MathF.Sin(_pitch);
                float cY = MathF.Cos(Yaw), sY = MathF.Sin(Yaw);
                return Vector3.Normalize(new Vector3(cP * sY, sP, cP * cY));
            }
        }

        public Vector3 Eye => Target + DirToEye * _distance + new Vector3(0f, HeightOffset, 0f);
        public Vector3 Forward => Vector3.Normalize(Target - Eye);

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
        public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);
        public Matrix4x4 ViewProjection => View * Projection;

        /// <summary>Unproject a screen pixel (top-left origin, y-down) into a world ray (mirrors IsoCamera3D).</summary>
        public Ray ScreenToRay(Vector2 screenPixel, int viewportWidth, int viewportHeight)
        {
            float ndcX = screenPixel.X / viewportWidth * 2f - 1f;
            float ndcY = 1f - screenPixel.Y / viewportHeight * 2f;
            Matrix4x4.Invert(ViewProjection, out var inv);
            Vector3 near = Unproject(new Vector3(ndcX, ndcY, 0f), inv);
            Vector3 far = Unproject(new Vector3(ndcX, ndcY, 1f), inv);
            return new Ray(near, far - near);
        }

        /// <summary>Pick the world point under a screen pixel on the horizontal plane y = <paramref name="groundY"/>.</summary>
        public Vector3 ScreenToGround(Vector2 screenPixel, int viewportWidth, int viewportHeight, float groundY = 0f)
        {
            Ray r = ScreenToRay(screenPixel, viewportWidth, viewportHeight);
            float t = MathF.Abs(r.Direction.Y) < 1e-6f ? 0f : (groundY - r.Origin.Y) / r.Direction.Y;
            return r.Origin + r.Direction * t;
        }

        static Vector3 Unproject(Vector3 ndc, Matrix4x4 invViewProj)
        {
            var p = Vector4.Transform(new Vector4(ndc, 1f), invViewProj);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FollowCamera3DTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Camera/FollowCamera3D.cs KhaozEngine.Tests/Render3D/FollowCamera3DTests.cs
git commit -m "render3d: FollowCamera3D perspective orbit-behind camera"
```

---

### Task 2: FollowCameraController (drag-orbit + scroll-zoom)

**Files:**
- Create: `KhaozEngine.Render3D/Camera/FollowCameraController.cs`
- Test: `KhaozEngine.Tests/Render3D/FollowCameraControllerTests.cs`

**Interfaces:**
- Consumes: `FollowCamera3D` (Task 1); `KhaozEngine.Windowing.InputState` (`MouseDelta`, `ScrollDelta`, `IsDown(MouseButton)`), `MouseButton`.
- Produces: `FollowCameraController(FollowCamera3D camera)` with `FollowCamera3D Camera { get; }`; fields `MouseButton OrbitButton` (default `Left`), `float OrbitYawSpeed`/`OrbitPitchSpeed` (radians per pixel), `float ZoomStep` (multiplicative per scroll unit); `void Update(in InputState input, float dt)`. Orbit only while `OrbitButton` is held; positive `ScrollDelta` zooms in (reduces distance).

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Render3D/FollowCameraControllerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class FollowCameraControllerTests
    {
        static InputState Frame(
            Vector2 mouseDelta = default, float scroll = 0f,
            MouseButton? down = null)
        {
            var md = new HashSet<MouseButton>();
            if (down is MouseButton b) md.Add(b);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                md, new HashSet<MouseButton>(),
                mousePosition: Vector2.Zero, mouseDelta: mouseDelta, scrollDelta: scroll,
                width: 800, height: 600);
        }

        [Fact]
        public void Drag_with_button_held_changes_yaw_and_pitch()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(mouseDelta: new Vector2(10, 4), down: MouseButton.Left), 1f / 60f);
            Assert.Equal(10f * ctl.OrbitYawSpeed, cam.Yaw, 5);
            Assert.Equal(0.5f - 4f * ctl.OrbitPitchSpeed, cam.Pitch, 5);
        }

        [Fact]
        public void Drag_without_button_does_nothing()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(mouseDelta: new Vector2(10, 4)), 1f / 60f);   // no button
            Assert.Equal(0f, cam.Yaw, 5);
            Assert.Equal(0.5f, cam.Pitch, 5);
        }

        [Fact]
        public void Scroll_up_zooms_in_scroll_down_zooms_out()
        {
            var cam = new FollowCamera3D();
            cam.Distance = 10f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(scroll: 1f), 1f / 60f);
            Assert.True(cam.Distance < 10f, $"scroll up should reduce distance: {cam.Distance}");
            float after = cam.Distance;
            ctl.Update(Frame(scroll: -1f), 1f / 60f);
            Assert.True(cam.Distance > after, $"scroll down should increase distance: {cam.Distance}");
        }

        [Fact]
        public void Pitch_and_distance_stay_clamped()
        {
            var cam = new FollowCamera3D();
            var ctl = new FollowCameraController(cam);
            // Drag far past the pitch limit.
            ctl.Update(Frame(mouseDelta: new Vector2(0, -100000), down: MouseButton.Left), 1f / 60f);
            Assert.True(cam.Pitch <= cam.MaxPitch + 1e-4f && cam.Pitch >= cam.MinPitch - 1e-4f);
            // Scroll in hard.
            for (int i = 0; i < 200; i++) ctl.Update(Frame(scroll: 1f), 1f / 60f);
            Assert.Equal(cam.MinDistance, cam.Distance, 4);
        }

        [Fact]
        public void No_input_leaves_camera_unchanged()
        {
            var cam = new FollowCamera3D { Yaw = 1.2f };
            cam.Pitch = 0.4f; cam.Distance = 7f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(), 1f / 60f);
            Assert.Equal(1.2f, cam.Yaw, 5);
            Assert.Equal(0.4f, cam.Pitch, 5);
            Assert.Equal(7f, cam.Distance, 5);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FollowCameraControllerTests`
Expected: FAIL/build error - `FollowCameraController` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Render3D/Camera/FollowCameraController.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Drives a <see cref="FollowCamera3D"/> from the per-frame <see cref="InputState"/> snapshot: drag the
    /// <see cref="OrbitButton"/> to orbit (yaw/pitch), scroll the wheel to zoom (distance). Touches no input
    /// statics (the snapshot is handed in), so it stays headless-testable. Mirrors
    /// <see cref="IsoCameraController"/>'s role for the iso camera. The camera clamps pitch/distance itself, so
    /// this controller only adds deltas; the tuning fields below are feel-tuned, not hardcoded deep.
    /// </summary>
    public sealed class FollowCameraController
    {
        /// <summary>The camera this controller drives.</summary>
        public FollowCamera3D Camera { get; }

        /// <summary>Mouse button that, while held, orbits the camera. Default <see cref="MouseButton.Left"/>.</summary>
        public MouseButton OrbitButton = MouseButton.Left;
        /// <summary>Radians of yaw applied per pixel of horizontal drag. Default 0.01.</summary>
        public float OrbitYawSpeed = 0.01f;
        /// <summary>Radians of pitch applied per pixel of vertical drag (drag up raises pitch). Default 0.01.</summary>
        public float OrbitPitchSpeed = 0.01f;
        /// <summary>Multiplicative distance factor per unit of scroll. Default 1.1 (scroll up zooms in).</summary>
        public float ZoomStep = 1.1f;

        public FollowCameraController(FollowCamera3D camera)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// Apply this frame's drag-orbit and scroll-zoom. While <see cref="OrbitButton"/> is held, the mouse delta
        /// swings <see cref="FollowCamera3D.Yaw"/> (horizontal) and tilts <see cref="FollowCamera3D.Pitch"/>
        /// (vertical, drag up = look down from higher); the wheel scales <see cref="FollowCamera3D.Distance"/>.
        /// Both are clamped by the camera. <paramref name="dt"/> is unused (gestures are delta-based) and kept for
        /// a uniform controller signature.
        /// </summary>
        public void Update(in InputState input, float dt)
        {
            if (input.IsDown(OrbitButton))
            {
                Vector2 d = input.MouseDelta;
                Camera.Yaw += d.X * OrbitYawSpeed;
                Camera.Pitch -= d.Y * OrbitPitchSpeed;   // setter clamps
            }

            float scroll = input.ScrollDelta;
            if (scroll != 0f)
                Camera.Distance *= MathF.Pow(ZoomStep, -scroll);   // setter clamps; +scroll -> closer
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FollowCameraControllerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Camera/FollowCameraController.cs KhaozEngine.Tests/Render3D/FollowCameraControllerTests.cs
git commit -m "render3d: FollowCameraController drag-orbit + scroll-zoom"
```

---

### Task 3: Scene3D.CameraOverride render hook

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs`

**Interfaces:**
- Consumes: `IIsoCamera3D` (implemented by both `IsoCamera3D` and `FollowCamera3D`).
- Produces: `Scene3D.CameraOverride { get; set; }` of type `IIsoCamera3D?` (null = use the built-in `Camera`). When set, the render path reads view/projection/eye/forward from it. The caller owns the override's `AspectRatio` (set it from the framebuffer each frame); `Scene3D` only maintains the built-in `Camera`'s aspect.

No headless test: `Scene3D` construction needs a GPU device (excluded by the no-GPU-in-tests rule). This plumbing is exercised by `TerrainWalkSample` (manual windowed validation). The camera MATH and controllers it routes are fully tested in Tasks 1-2.

- [ ] **Step 1: Add the property + active-camera accessor**

In `KhaozEngine.Render3D/Scene3D.cs`, just after the `Camera`/`Post` properties (the `public IsoCamera3D Camera { get; } = new();` line), add:

```csharp
        /// <summary>
        /// Optional camera that overrides the built-in <see cref="Camera"/> for rendering this scene. Set it to a
        /// sibling camera (e.g. <see cref="FollowCamera3D"/>) to drive the view/projection from something other than
        /// the iso camera; null (the default) uses <see cref="Camera"/>. The override supplies only the read-only
        /// camera surface (<see cref="IIsoCamera3D"/>), so the caller owns its aspect ratio: set it from the
        /// framebuffer each frame. <see cref="Camera"/>'s aspect is still maintained by the scene.
        /// </summary>
        public IIsoCamera3D? CameraOverride { get; set; }

        /// <summary>The camera the render path reads this frame: <see cref="CameraOverride"/> if set, else <see cref="Camera"/>.</summary>
        IIsoCamera3D ActiveCamera => CameraOverride ?? Camera;
```

- [ ] **Step 2: Route render-time camera reads through ActiveCamera**

Replace every render-time READ of `Camera.ViewProjection`, `Camera.Eye`, and `Camera.Forward` with `ActiveCamera.<member>`. Leave the WRITE in `EnsureSize` (`Camera.AspectRatio = ...`) untouched (it maintains the built-in camera). The reads to convert (verify by grep, below):
- `DrawBillboard` immediate path: `BillboardGeometry.CameraBasis(Camera.Forward, ...)` -> `ActiveCamera.Forward`
- `RenderInternal`: `Matrix4x4 vp = Camera.ViewProjection;` -> `ActiveCamera.ViewProjection`; `Vector3 eye = Camera.Eye;` -> `ActiveCamera.Eye`
- decal draw: `Camera.ViewProjection` -> `ActiveCamera.ViewProjection`
- fills draw: `Camera.ViewProjection` -> `ActiveCamera.ViewProjection`
- lines draw: `Camera.ViewProjection` -> `ActiveCamera.ViewProjection`
- billboards additive + alpha draws: `Camera.ViewProjection` -> `ActiveCamera.ViewProjection`
- textured billboards: `BillboardGeometry.CameraBasis(Camera.Forward, ...)` -> `ActiveCamera.Forward`; `_texBillboards.SetViewProj(cl, Camera.ViewProjection)` -> `ActiveCamera.ViewProjection`
- beams: `Vector3 viewDir = Camera.Forward;` -> `ActiveCamera.Forward`; `_beams.SetFrameUniforms(cl, Camera.ViewProjection, ...)` -> `ActiveCamera.ViewProjection`

Verification grep (after editing there must be NO render-time `Camera.ViewProjection`/`Camera.Eye`/`Camera.Forward` reads left; only `Camera.AspectRatio` in EnsureSize and the property declarations remain):

Run: `grep -n "Camera\.\(ViewProjection\|Eye\|Forward\)" KhaozEngine.Render3D/Scene3D.cs`
Expected: no matches (all replaced by `ActiveCamera.`).

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 4: Run the existing Scene3D tests to confirm no regression**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter Scene3D`
Expected: PASS (existing Scene3D queue/binder tests unaffected; `ActiveCamera` defaults to `Camera`).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Scene3D.cs
git commit -m "render3d: Scene3D.CameraOverride so a sibling camera can drive rendering"
```

---

### Task 4: CharacterController3D (terrain-agnostic locomotion)

**Files:**
- Create: `KhaozEngine.Game.Render3D/CharacterController3D.cs`
- Test: `KhaozEngine.Tests/Render3D/CharacterController3DTests.cs`

**Interfaces:**
- Consumes: `KhaozEngine.Windowing.InputState` (`IsDown(Key)`), `Key`. MUST NOT reference `KhaozEngine.Terrain`.
- Produces: `CharacterController3D` with `Vector3 Position { get; }`; fields `float WalkSpeed` (default 3), `float RunSpeed` (default 6), `float CapsuleHalfHeight` (default 0.9), `float MaxSlopeRadians` (default ~50 deg); `void Update(in InputState input, float dt, float cameraYaw, Func<float,float,float> groundHeight, Func<float,float,Vector3>? groundNormal = null)`. WASD moves camera-relative on XZ (forward = camera look projected onto XZ from `cameraYaw`); diagonals normalized; shift = run; `Position.Y = groundHeight(x,z) + CapsuleHalfHeight` each frame; if `groundNormal` is supplied and the destination slope exceeds `MaxSlopeRadians`, the horizontal move is rejected (stay put, still ground-clamp). Forward convention matches the camera: `forwardXZ = (-sin(yaw), 0, -cos(yaw))`, `rightXZ = (cos(yaw), 0, -sin(yaw))`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Render3D/CharacterController3DTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class CharacterController3DTests
    {
        static InputState Keys(params Key[] down)
        {
            var d = new HashSet<Key>(down);
            return new InputState(
                d, new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 800, 600);
        }

        static readonly Func<float, float, float> FlatGround = (x, z) => 0f;

        [Fact]
        public void W_at_yaw_zero_moves_toward_negative_z()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(c.Position.Z < 0f, c.Position.ToString());
            Assert.True(MathF.Abs(c.Position.X) < 1e-4f, c.Position.ToString());
            Assert.Equal(c.WalkSpeed, MathF.Abs(c.Position.Z), 4);   // 1 second at walk speed
        }

        [Fact]
        public void D_at_yaw_zero_moves_toward_positive_x()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.D), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(c.Position.X > 0f, c.Position.ToString());
            Assert.True(MathF.Abs(c.Position.Z) < 1e-4f, c.Position.ToString());
        }

        [Fact]
        public void Diagonal_is_normalized()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W, Key.D), dt: 1f, cameraYaw: 0f, FlatGround);
            float horiz = new Vector2(c.Position.X, c.Position.Z).Length();
            Assert.Equal(c.WalkSpeed, horiz, 3);   // not WalkSpeed*sqrt(2)
        }

        [Fact]
        public void Idle_does_not_move()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(MathF.Abs(c.Position.X) < 1e-6f && MathF.Abs(c.Position.Z) < 1e-6f, c.Position.ToString());
        }

        [Fact]
        public void Displacement_scales_with_dt()
        {
            var a = new CharacterController3D { CapsuleHalfHeight = 0f };
            a.Update(Keys(Key.W), dt: 0.1f, cameraYaw: 0f, FlatGround);
            var b = new CharacterController3D { CapsuleHalfHeight = 0f };
            b.Update(Keys(Key.W), dt: 0.2f, cameraYaw: 0f, FlatGround);
            Assert.Equal(2f * MathF.Abs(a.Position.Z), MathF.Abs(b.Position.Z), 4);
        }

        [Fact]
        public void Run_is_faster_than_walk()
        {
            var walk = new CharacterController3D { CapsuleHalfHeight = 0f };
            walk.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, FlatGround);
            var run = new CharacterController3D { CapsuleHalfHeight = 0f };
            run.Update(Keys(Key.W, Key.LeftShift), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(MathF.Abs(run.Position.Z) > MathF.Abs(walk.Position.Z), $"run {run.Position.Z} walk {walk.Position.Z}");
            Assert.Equal(run.RunSpeed, MathF.Abs(run.Position.Z), 3);
        }

        [Fact]
        public void Y_clamps_to_ground_plus_half_height_each_frame()
        {
            Func<float, float, float> bumpy = (x, z) => 5f;
            var c = new CharacterController3D { CapsuleHalfHeight = 0.9f };
            c.Update(Keys(Key.W), dt: 0.5f, cameraYaw: 0f, bumpy);
            Assert.Equal(5f + 0.9f, c.Position.Y, 4);
        }

        [Fact]
        public void Camera_relative_yaw_rotates_movement()
        {
            // Yaw = +90 deg: forward (W) should now head toward -X (camera turned a quarter turn).
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: MathF.PI / 2f, FlatGround);
            Assert.True(c.Position.X < 0f, c.Position.ToString());
            Assert.True(MathF.Abs(c.Position.Z) < 1e-3f, c.Position.ToString());
        }

        [Fact]
        public void Step_onto_too_steep_ground_is_rejected()
        {
            // Normal nearly horizontal => slope ~90 deg, exceeds MaxSlope => horizontal move rejected.
            Func<float, float, Vector3> steep = (x, z) => Vector3.Normalize(new Vector3(1f, 0.05f, 0f));
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, FlatGround, steep);
            Assert.True(MathF.Abs(c.Position.X) < 1e-6f && MathF.Abs(c.Position.Z) < 1e-6f, c.Position.ToString());
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter CharacterController3DTests`
Expected: FAIL/build error - `CharacterController3D` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Game.Render3D/CharacterController3D.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Terrain-agnostic third-person locomotion for the walkable slice. WASD moves the character on the XZ plane
    /// relative to a camera yaw (forward = the camera's look direction projected onto the ground); diagonals are
    /// normalized; left/right shift runs. Each frame the Y is clamped onto a caller-supplied ground-height
    /// delegate (plus a capsule half-height so the feet sit on the ground), and an optional ground-normal delegate
    /// rejects a step onto terrain steeper than <see cref="MaxSlopeRadians"/>. Pure System.Numerics + the input
    /// snapshot: no reference to KhaozEngine.Terrain, no physics beyond ground-clamp (no jump/gravity). The speeds
    /// and half-height are public fields, feel-tuned later.
    /// </summary>
    public sealed class CharacterController3D
    {
        Vector3 _position;

        /// <summary>Current world position (the capsule centre: ground height + <see cref="CapsuleHalfHeight"/>).</summary>
        public Vector3 Position => _position;

        /// <summary>Metres per second while walking. Default 3.</summary>
        public float WalkSpeed = 3f;
        /// <summary>Metres per second while running (shift held). Default 6.</summary>
        public float RunSpeed = 6f;
        /// <summary>Half the capsule height, added to the ground so the feet sit on the ground. Default 0.9 (a 1.8 m capsule).</summary>
        public float CapsuleHalfHeight = 0.9f;
        /// <summary>Reject a step onto ground steeper than this (angle between surface normal and +Y), when a
        /// ground-normal delegate is supplied. Default ~50 deg.</summary>
        public float MaxSlopeRadians = MathF.PI * 50f / 180f;

        /// <summary>
        /// Advance the character for one frame. <paramref name="cameraYaw"/> is the follow camera's yaw (radians);
        /// <paramref name="groundHeight"/> returns terrain height at (x, z); <paramref name="groundNormal"/> is
        /// optional and, when given, gates moves by slope. Touches no input statics.
        /// </summary>
        public void Update(in InputState input, float dt, float cameraYaw,
                           Func<float, float, float> groundHeight,
                           Func<float, float, Vector3>? groundNormal = null)
        {
            if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

            // Camera-relative ground basis (matches FollowCamera3D's yaw convention).
            float sY = MathF.Sin(cameraYaw), cY = MathF.Cos(cameraYaw);
            Vector3 forward = new(-sY, 0f, -cY);
            Vector3 right = new(cY, 0f, -sY);

            Vector3 move = Vector3.Zero;
            if (input.IsDown(Key.W)) move += forward;
            if (input.IsDown(Key.S)) move -= forward;
            if (input.IsDown(Key.D)) move += right;
            if (input.IsDown(Key.A)) move -= right;
            if (move.LengthSquared() > 1e-6f)
            {
                move = Vector3.Normalize(move);   // normalized diagonals
                float speed = (input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift)) ? RunSpeed : WalkSpeed;
                float nx = _position.X + move.X * speed * dt;
                float nz = _position.Z + move.Z * speed * dt;

                bool blocked = false;
                if (groundNormal is not null)
                {
                    float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
                    if (MathF.Acos(ny) > MaxSlopeRadians) blocked = true;
                }
                if (!blocked) { _position.X = nx; _position.Z = nz; }
            }

            _position.Y = groundHeight(_position.X, _position.Z) + CapsuleHalfHeight;
        }

        /// <summary>Teleport the character; Y is recomputed from the ground delegate on the next <see cref="Update"/>.</summary>
        public void SetXZ(float x, float z) { _position.X = x; _position.Z = z; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter CharacterController3DTests`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Game.Render3D/CharacterController3D.cs KhaozEngine.Tests/Render3D/CharacterController3DTests.cs
git commit -m "game.render3d: CharacterController3D terrain-agnostic WASD locomotion"
```

---

### Task 5: TerrainWalkSample (windowed slice)

**Files:**
- Create: `TerrainWalkSample/TerrainWalkSample.csproj`
- Create: `TerrainWalkSample/Program.cs`

**Interfaces:**
- Consumes: `GameApp3D`/`GameAppOptions` (`KhaozEngine.Game`), `Scene3D`/`MeshPrimitives`/`MeshHandle`/`FollowCamera3D`/`FollowCameraController` (`KhaozEngine.Render3D`), `CharacterController3D` (`KhaozEngine.Game`), `TerrainField`/`TerrainConfig`/`TerrainCollision`/`TerrainPresets` (`KhaozEngine.Terrain`), `TerrainChunkBuilder`/`TerrainChunkRegion`/`TerrainScene3D` extensions/`TerrainLod` (`KhaozEngine.Terrain`), `Key`/`MouseButton` (`KhaozEngine.Windowing`), `Color` (`KhaozEngine.Primitives`).
- Produces: an `Exe` (not unit-tested; manual windowed validation). Honors `--smoke` / `KE_MAX_FRAMES` via the `AppWindow` loop so a headless render-N-frames-then-exit run is possible.

- [ ] **Step 1: Create the project file**

Create `TerrainWalkSample/TerrainWalkSample.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Game/KhaozEngine.Game.csproj" />
    <ProjectReference Include="../KhaozEngine.Game.Render3D/KhaozEngine.Game.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Render3D/KhaozEngine.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Windowing/KhaozEngine.Windowing.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the program**

Create `TerrainWalkSample/Program.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

// Walkable overworld slice: drive a greybox capsule over the shipped analytic terrain
// (TerrainPresets.Clearing) with a third-person follow camera. WASD move, mouse-drag orbit,
// scroll zoom, shift run, Esc quit. The terrain field is wrapped in TerrainCollision for the
// ground-clamp; nothing here is streamed (fixed chunk grid) and the capsule is static (no
// walk-cycle yet). Honors KE_MAX_FRAMES so a headless smoke run renders N frames then exits 0.
Console.WriteLine("TerrainWalkSample - WASD move | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new TerrainWalkApp())
    app.Run();
return 0;

sealed class TerrainWalkApp : GameApp3D
{
    // Tuning surface (feel-tuned later).
    const int GridRadius = 3;                 // 7x7 chunks (2*radius+1)
    const float CapsuleRadius = 0.3f;
    const float CapsuleHalfHeight = 0.9f;     // 1.8 m total (height 1.2 + 2*radius 0.6)

    TerrainField _field = null!;
    TerrainCollision _terrain = null!;
    readonly List<MeshHandle> _chunks = new();
    MeshHandle _capsule;

    CharacterController3D _character = null!;
    FollowCamera3D _camera = null!;
    FollowCameraController _camController = null!;

    public TerrainWalkApp()
        : base(new GameAppOptions
        {
            Title = "KhaozEngine - Terrain walk",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.45f, 0.62f, 0.85f, 1f),   // sky
        })
    { }

    protected override void OnLoad()
    {
        var sc = Scene;

        // Analytic field + collision wrapper for the ground-clamp.
        _field = new TerrainField(TerrainPresets.Clearing());
        _terrain = new TerrainCollision(_field);

        // Fixed NxN grid of chunks around the origin, meshed at the densest LOD (no streaming here).
        float size = TerrainChunkRegion.DefaultSize;
        for (int gz = -GridRadius; gz <= GridRadius; gz++)
            for (int gx = -GridRadius; gx <= GridRadius; gx++)
            {
                var region = new TerrainChunkRegion { OriginX = gx * size, OriginZ = gz * size, Size = size };
                var chunk = TerrainChunkBuilder.Build(_field, region, lod: 0);
                _chunks.Add(sc.LoadTerrainChunk(chunk));
            }

        // 1.8 m greybox capsule (height 1.2 + 2*radius 0.6); mesh bottom sits at y=0 in local space.
        _capsule = sc.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

        // Character spawns on the ground at the origin.
        _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight };
        _character.SetXZ(0f, 0f);
        _character.Update(InputState.Empty, 0f, 0f, _terrain.GroundHeight);   // settle Y onto the ground

        // Follow camera drives rendering via the scene override.
        _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f };
        _camera.Distance = 9f;
        _camController = new FollowCameraController(_camera);
        sc.CameraOverride = _camera;
    }

    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) { Quit(); return; }

        _character.Update(Input, dt, _camera.Yaw, _terrain.GroundHeight);

        _camera.Target = _character.Position;
        _camera.AspectRatio = FrameHeight > 0 ? (float)FrameWidth / FrameHeight : _camera.AspectRatio;
        _camController.Update(Input, dt);
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        foreach (var chunk in _chunks)
            scene.DrawTerrainChunk(chunk);

        // Draw the capsule so its base sits on the ground (Position is the capsule centre).
        Vector3 p = _character.Position;
        scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
    }
}
```

- [ ] **Step 3: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: Build succeeded (resolves `CameraOverride`, `FollowCamera3D`, `CharacterController3D`, terrain extensions).

- [ ] **Step 4: Headless smoke (render a few frames then exit)**

Run: `KE_MAX_FRAMES=3 dotnet run --project TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: process prints the controls line, renders 3 frames, exits 0 (no exception). (If the host has no GPU this may no-op; the build succeeding is the gating check.)

- [ ] **Step 5: Commit**

```bash
git add TerrainWalkSample/TerrainWalkSample.csproj TerrainWalkSample/Program.cs
git commit -m "sample: TerrainWalkSample windowed walkable terrain slice"
```

---

### Task 6: Release ritual (7.43.0 -> 7.44.0)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

**Interfaces:** none (docs + version). This is the single minor bump for the whole batch (Tasks 1-5).

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<KhaozEngineVersion>7.43.0</KhaozEngineVersion>` to `<KhaozEngineVersion>7.44.0</KhaozEngineVersion>`.

- [ ] **Step 2: Add the CHANGELOG entry (newest first)**

Add at the top of the entries in `CHANGELOG.md`:

```markdown
## 7.44.0

### Added
- **`FollowCamera3D` + `FollowCameraController` (KhaozEngine.Render3D)** - a third-person follow camera:
  a perspective orbit-behind camera (`Target`/`Yaw`/`Pitch`/`Distance`, pitch + distance clamped) that always
  looks at its target, a sibling of `IsoCamera3D` sharing the Y-up convention and `Eye`/`Forward`/`ScreenToGround`.
  `FollowCameraController` drives it from the `InputState` snapshot (drag the orbit button to yaw/pitch, scroll to
  zoom); tuning is exposed as fields.
- **`Scene3D.CameraOverride` (KhaozEngine.Render3D)** - optional `IIsoCamera3D` that overrides the built-in
  iso `Camera` for rendering, so a sibling camera (e.g. `FollowCamera3D`) can drive the view/projection. Null by
  default (unchanged behaviour). The caller owns the override's aspect ratio.
- **`CharacterController3D` (KhaozEngine.Game.Render3D)** - terrain-agnostic third-person locomotion: WASD moves
  camera-relative on the XZ plane (normalized diagonals, shift to run) and each frame clamps Y onto a caller-supplied
  ground-height delegate (plus a capsule half-height), with an optional ground-normal slope gate. No physics beyond
  the ground-clamp; references no terrain package (ground supplied as delegates).
- **`TerrainWalkSample`** - a windowed sample (not packaged) that makes the shipped analytic terrain walkable:
  a greybox capsule driven over a fixed 7x7 grid of `TerrainPresets.Clearing()` chunks with the follow camera.

This is sub-project 2 of the overworld render-scale track (walk on the terrain shipped in 7.43.0).
```

- [ ] **Step 3: Add the CHANGENOTES digest line (newest first)**

Add at the top of `CHANGENOTES.md`:

```markdown
- **7.44.0** - Walkable overworld slice: `FollowCamera3D` + `FollowCameraController` (third-person orbit-behind
  camera) and `Scene3D.CameraOverride` in Render3D, `CharacterController3D` (terrain-agnostic WASD locomotion) in
  Game.Render3D, and the `TerrainWalkSample` windowed app that walks a capsule over the shipped terrain.
```

- [ ] **Step 4: Update the 3 guard declarations**

- `docs/CONSUMERS.md`: set the "Engine current version" line to `7.44.0`.
- `docs/ROADMAP.md`: set the "Current released version" line to `7.44.0`.
- `README.md`: set the `<PackageReference ... Version="..." />` example to `7.44.0`.

(Find the exact lines with: `grep -rn "7.43.0" docs/CONSUMERS.md docs/ROADMAP.md README.md`.)

- [ ] **Step 5: Add the USING-KHAOZENGINE usage section**

Append a section to `docs/USING-KHAOZENGINE.md` documenting the follow camera + character controller. Place it near the existing 3D / camera content. Content:

```markdown
## Third-person follow camera + character controller

For a walkable 3D world, pair `FollowCamera3D` (KhaozEngine.Render3D) with `CharacterController3D`
(KhaozEngine.Game.Render3D). The camera is a perspective sibling of `IsoCamera3D`: it orbits behind a `Target`
at a clamped `Pitch`/`Distance` and always looks at the target. Drive it from the input snapshot with
`FollowCameraController` (drag the orbit button to swing yaw/pitch, scroll to zoom). To render through it, set
`Scene3D.CameraOverride` and feed the override its aspect ratio each frame:

```csharp
var camera = new FollowCamera3D { Target = character.Position, Distance = 9f };
var camController = new FollowCameraController(camera);
scene.CameraOverride = camera;   // sibling camera drives the render path; null = built-in iso Camera

// each frame:
character.Update(input, dt, camera.Yaw, terrain.GroundHeight);   // WASD camera-relative, ground-clamped
camera.Target = character.Position;
camera.AspectRatio = (float)frameWidth / frameHeight;
camController.Update(input, dt);
```

`CharacterController3D` is terrain-agnostic: it takes ground height (and optionally ground normal) as delegates,
so any height source works. Pair it with `TerrainCollision.GroundHeight` for analytic terrain. WASD is
camera-relative on XZ (normalized diagonals, shift to run); `Position.Y` clamps to the ground plus the capsule
half-height each frame. Speeds, capsule half-height, camera distance/pitch limits, and orbit/zoom sensitivity are
public fields. See `TerrainWalkSample` for the full wiring. No animation/streaming/physics beyond ground-clamp.
```

- [ ] **Step 6: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: pass (all three declarations match `7.44.0`).

- [ ] **Step 7: Full test suite green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all tests, including the 3 new test classes).

- [ ] **Step 8: Pack**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: packs all packages at 7.44.0 (Render3D + Game.Render3D carry the new API).

- [ ] **Step 9: Commit**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md docs/USING-KHAOZENGINE.md
git commit -m "docs(7.44.0): walkable slice release notes + usage + version bump"
```

---

## Post-implementation (handled outside the task list, per CLAUDE.md)

- Merge `feature/walkable-slice` -> `main`, repack from the main root, `git tag v7.44.0`, push `main` + the tag, clean up the worktree + merged branch.
- End with the one-click windowed boot command for the user (from this worktree's absolute path), in a language-tagged ```bash block.

## Self-Review

- **Spec coverage:** FollowCamera3D + FollowCameraController (Task 1-2), CharacterController3D (Task 4), TerrainWalkSample (Task 5), `Scene3D` render integration via `CameraOverride` (Task 3, the "FollowCamera3D -> view/proj -> Scene3D" data-flow edge), headless tests covering the spec's Testing list (Tasks 1/2/4), minor release with all docs (Task 6). In scope only; out-of-scope items (animation/netcode/streaming/prop collision/physics) are explicitly excluded in the sample comments and not built.
- **Placeholder scan:** none - every code/test step has complete code.
- **Type consistency:** `FollowCamera3D` members (`Yaw`/`Pitch`/`Distance`/`Target`/`HeightOffset`/`AspectRatio`/`Min*`/`Max*`) are used identically across Tasks 1/2/5; `CharacterController3D.Update(in InputState, float, float, Func<float,float,float>, Func<float,float,Vector3>?)` + `Position`/`SetXZ`/`CapsuleHalfHeight` match across Tasks 4/5; `Scene3D.CameraOverride : IIsoCamera3D?` matches Tasks 3/5.
