# KhaozEngine.Graphics Camera2D Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote SpaceGame's generic matrix camera into a new `KhaozEngine.Graphics` package as a headless, device-free `Camera2D` (position/zoom/rotation -> view matrix, screen<->world, optional world-bounds clamp).

**Architecture:** One public `sealed class Camera2D` holding `Position`/`Zoom`/`Rotation`/`Viewport`. Core methods take an explicit `Viewport` (so the math needs no `GraphicsDevice` and is fully headless-testable); no-arg overloads delegate to the core using the stored `Viewport` property. `ClampPosition` is a separate pure helper. Game follow-cams compose this base; no follow logic is promoted.

**Tech Stack:** net10.0, MonoGame.Framework.DesktopGL 3.8.*, xUnit. `ImplicitUsings` disabled (explicit `using` required). New package follows the `KhaozEngine.Time` csproj template.

**Reference spec:** `docs/superpowers/specs/2026-06-11-graphics-camera2d-design.md`

**Working directory:** worktree `.claude/worktrees/graphics-camera2d`, branch `worktree-graphics-camera2d`. All paths below are relative to the worktree root. Baseline is green (268 tests).

**Release discipline (do NOT do):** no `<Version>` bump in `Directory.Build.props`, no `CHANGELOG.md` entry, no `dotnet pack` into the shared `local-feed`. The coordinator owns the batched 3.3.0 release.

---

## File Structure

Created:
- `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj` — package definition (mirrors `KhaozEngine.Time`).
- `KhaozEngine.Graphics/Camera2D.cs` — the camera type (the entire public surface of the package).
- `KhaozEngine.Graphics/README.md` — packed package readme.
- `KhaozEngine.Tests/CameraTests.cs` — headless xUnit tests.

Modified (one line each):
- `KhaozEngine.slnx` — register the new project so the solution builds it.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add `ProjectReference` to the new package so tests can see `Camera2D`.

---

## Task 1: Scaffold the KhaozEngine.Graphics package and wire it in

Create the project, its readme, register it in the solution, and reference it from the test project. End state: solution builds with an empty-but-present package and the test project can reference the namespace. We add a placeholder `Camera2D` with just the state so the project compiles; behaviour comes in later TDD tasks.

**Files:**
- Create: `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj`
- Create: `KhaozEngine.Graphics/README.md`
- Create: `KhaozEngine.Graphics/Camera2D.cs` (state only, this task)
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

- [ ] **Step 1: Create the csproj** (mirrors `KhaozEngine.Time/KhaozEngine.Time.csproj`)

Create `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Graphics</PackageId>
    <Description>Game-agnostic 2D matrix camera (Camera2D): position/zoom/rotation to view matrix, screen&lt;-&gt;world transforms, optional world-bounds clamp. Headless (no GraphicsDevice required for the math).</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.*" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the README**

Create `KhaozEngine.Graphics/README.md`:

```markdown
# KhaozEngine.Graphics

Game-agnostic 2D rendering helpers for MonoGame games.

## Camera2D

A matrix camera: `Position` (world point at screen center), `Zoom`, `Rotation` (radians) ->
a view `Matrix`, plus `WorldToScreen` / `ScreenToWorld` and an optional `ClampPosition`
world-bounds helper.

The math is headless: the core methods take a `Viewport` argument, so no `GraphicsDevice` is
required to compute a matrix (handy for tests and tools). Convenience no-arg overloads use a
settable `Viewport` property.

```csharp
var camera = new Camera2D { Position = shipPosition, Zoom = 1.5f };

// Per-call viewport (split-screen, minimap, tests):
spriteBatch.Begin(transformMatrix: camera.GetViewMatrix(GraphicsDevice.Viewport));

// Or set the viewport once (refresh on resize) and use the no-arg overloads:
camera.Viewport = GraphicsDevice.Viewport;          // also on ClientSizeChanged
spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());
Vector2 world = camera.ScreenToWorld(input.PointerPosition);
```

`Zoom` must be `> 0`. Follow-cam logic (smoothing, bounds tracking) lives game-side and
composes a `Camera2D`.
```

- [ ] **Step 3: Create Camera2D.cs with state only (so the project compiles)**

Create `KhaozEngine.Graphics/Camera2D.cs`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Graphics;

/// <summary>
/// Game-agnostic 2D matrix camera. <see cref="Position"/> is the world point shown at the
/// center of the viewport; <see cref="Zoom"/> and <see cref="Rotation"/> scale and roll the
/// view about that point. The core transform methods take an explicit <see cref="Viewport"/>
/// so the math requires no <c>GraphicsDevice</c> and is fully headless. Convenience no-arg
/// overloads use the settable <see cref="Viewport"/> property.
/// </summary>
public sealed class Camera2D
{
    /// <summary>World point shown at the center of the viewport. Publicly settable so a
    /// follow-cam can drive it each frame.</summary>
    public Vector2 Position { get; set; } = Vector2.Zero;

    /// <summary>Uniform scale; greater than 1 zooms in. Must be &gt; 0: a value &lt;= 0 makes
    /// the view matrix singular, so <see cref="ScreenToWorld(Vector2, Viewport)"/> (which
    /// inverts it) returns NaN.</summary>
    public float Zoom { get; set; } = 1f;

    /// <summary>Camera roll in radians, counter-clockwise.</summary>
    public float Rotation { get; set; }

    /// <summary>Viewport used by the no-arg overloads. Set once and refresh on resize
    /// (e.g. <c>Window.ClientSizeChanged</c>). The per-call overloads ignore this.</summary>
    public Viewport Viewport { get; set; }
}
```

- [ ] **Step 4: Register the project in the solution**

Modify `KhaozEngine.slnx` — add this line in the `<Solution>` list, keeping the existing alphabetical-ish order (after the `Ecs` entry, before `Input`):

```xml
  <Project Path="KhaozEngine.Graphics/KhaozEngine.Graphics.csproj" />
```

- [ ] **Step 5: Reference the package from the test project**

Modify `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add to the `<ItemGroup>` of `ProjectReference`s (after the `Ecs` reference, before `Input`):

```xml
    <ProjectReference Include="../KhaozEngine.Graphics/KhaozEngine.Graphics.csproj" />
```

- [ ] **Step 6: Build to verify everything compiles and wires up**

Run: `dotnet build KhaozEngine.Graphics/KhaozEngine.Graphics.csproj`
Expected: Build succeeded.

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: Build succeeded (test project now references the new package).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Graphics KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "Scaffold KhaozEngine.Graphics package with Camera2D state"
```

---

## Task 2: View matrix + Position-at-center invariant (TDD)

Implement `GetViewMatrix(Viewport)` and the defining invariant that `Position` maps to the viewport center. We need `WorldToScreen(Vector2, Viewport)` to assert it cleanly, so this task adds both methods (matrix + world->screen) together; `ScreenToWorld` and round-trip come in Task 3.

**Files:**
- Modify: `KhaozEngine.Graphics/Camera2D.cs`
- Test: `KhaozEngine.Tests/CameraTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/CameraTests.cs`:

```csharp
using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

public class CameraTests
{
    private const int W = 800;
    private const int H = 600;
    private static Viewport Vp => new Viewport(0, 0, W, H);
    private const float Tol = 1e-3f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol)
    {
        Assert.True(Vector2.Distance(expected, actual) <= tol,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void WorldOrigin_AtDefaults_MapsToViewportCenter()
    {
        var camera = new Camera2D();
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(Vector2.Zero, Vp));
    }

    [Theory]
    [InlineData(123f, -45f, 1f, 0f)]
    [InlineData(-10f, 200f, 2.5f, 0.7f)]
    [InlineData(500f, 500f, 0.4f, -1.2f)]
    public void Position_AlwaysMapsToViewportCenter(float px, float py, float zoom, float rot)
    {
        var camera = new Camera2D
        {
            Position = new Vector2(px, py),
            Zoom = zoom,
            Rotation = rot,
        };
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(camera.Position, Vp));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: FAIL — `Camera2D` does not contain a definition for `WorldToScreen`.

- [ ] **Step 3: Implement GetViewMatrix and WorldToScreen**

Add to `Camera2D.cs` inside the class (after the properties):

```csharp
    /// <summary>
    /// Builds the view (world-to-screen) transform for the given viewport:
    /// translate so <see cref="Position"/> is at the origin, apply <see cref="Rotation"/>,
    /// scale by <see cref="Zoom"/>, then translate to the viewport center. The world thus
    /// rotates and scales about <see cref="Position"/>, which lands at screen center.
    /// </summary>
    public Matrix GetViewMatrix(Viewport viewport)
    {
        return Matrix.CreateTranslation(-Position.X, -Position.Y, 0f)
            * Matrix.CreateRotationZ(Rotation)
            * Matrix.CreateScale(Zoom, Zoom, 1f)
            * Matrix.CreateTranslation(viewport.Width * 0.5f, viewport.Height * 0.5f, 0f);
    }

    /// <summary>Transforms a world position to screen space using the given viewport.</summary>
    public Vector2 WorldToScreen(Vector2 world, Viewport viewport)
    {
        return Vector2.Transform(world, GetViewMatrix(viewport));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: PASS (4 tests: 1 fact + 3 theory cases).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Graphics/Camera2D.cs KhaozEngine.Tests/CameraTests.cs
git commit -m "Camera2D: view matrix + Position-at-center invariant"
```

---

## Task 3: ScreenToWorld + round-trip + zoom + rotation (TDD)

Add the inverse transform and pin zoom scaling and rotation direction.

**Files:**
- Modify: `KhaozEngine.Graphics/Camera2D.cs`
- Test: `KhaozEngine.Tests/CameraTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these methods to the `CameraTests` class in `KhaozEngine.Tests/CameraTests.cs`:

```csharp
    [Theory]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(123f, -45f, 2.5f, 0.7f)]
    [InlineData(-10f, 200f, 0.4f, -1.2f)]
    public void ScreenToWorld_IsInverseOfWorldToScreen(float px, float py, float zoom, float rot)
    {
        var camera = new Camera2D
        {
            Position = new Vector2(px, py),
            Zoom = zoom,
            Rotation = rot,
        };
        foreach (var p in new[]
        {
            new Vector2(0f, 0f), new Vector2(50f, 120f),
            new Vector2(-300f, 80f), new Vector2(640f, 400f),
        })
        {
            var screen = camera.WorldToScreen(p, Vp);
            AssertClose(p, camera.ScreenToWorld(screen, Vp));
        }
    }

    [Fact]
    public void Zoom_ScalesWorldOffsetFromCenter()
    {
        var camera = new Camera2D { Position = Vector2.Zero, Zoom = 2f };
        // World (10,0) is 10 units right of Position; at zoom 2 that is 20 px right of center.
        AssertClose(new Vector2(W / 2f + 20f, H / 2f), camera.WorldToScreen(new Vector2(10f, 0f), Vp));
    }

    [Fact]
    public void Rotation_QuarterTurn_MapsWorldXOffsetToScreenYOffset()
    {
        var camera = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = MathHelper.PiOver2 };
        // A +X world offset, rotated +90deg CCW under MonoGame's screen-space transform,
        // lands on the +Y screen axis (below center). Pins rotation direction + matrix fold.
        var screen = camera.WorldToScreen(new Vector2(10f, 0f), Vp);
        AssertClose(new Vector2(W / 2f, H / 2f + 10f), screen);
    }
```

Note for the implementer: if the `Rotation` test's expected sign is off, do NOT flip the
matrix — verify against MonoGame's `Matrix.CreateRotationZ` convention and correct the
*expected* value in the test. The matrix order is fixed by the spec; the test documents the
observed direction. (`CreateRotationZ(+theta)` rotates the world CCW; composed with the
screen translate, a +X world offset maps toward +Y screen. If empirically it is `-10f`,
update the expected to `H/2 - 10f` and leave a comment.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: FAIL — `Camera2D` does not contain a definition for `ScreenToWorld`.

- [ ] **Step 3: Implement ScreenToWorld**

Add to `Camera2D.cs` after `WorldToScreen`:

```csharp
    /// <summary>Transforms a screen position back to world space using the given viewport.
    /// Requires <see cref="Zoom"/> &gt; 0 (otherwise the matrix is singular and the result is
    /// NaN).</summary>
    public Vector2 ScreenToWorld(Vector2 screen, Viewport viewport)
    {
        Matrix inverseView = Matrix.Invert(GetViewMatrix(viewport));
        return Vector2.Transform(screen, inverseView);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: PASS. If only the rotation-direction assertion fails, correct the expected value per the Step 1 note and re-run.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Graphics/Camera2D.cs KhaozEngine.Tests/CameraTests.cs
git commit -m "Camera2D: ScreenToWorld + round-trip, zoom, rotation tests"
```

---

## Task 4: No-arg overloads using the Viewport property (TDD)

Add the turn-key overloads that delegate to the core using the stored `Viewport`.

**Files:**
- Modify: `KhaozEngine.Graphics/Camera2D.cs`
- Test: `KhaozEngine.Tests/CameraTests.cs`

- [ ] **Step 1: Write the failing test**

Append to the `CameraTests` class:

```csharp
    [Fact]
    public void NoArgOverloads_UseViewportProperty_AndMatchPerCall()
    {
        var camera = new Camera2D
        {
            Position = new Vector2(40f, -15f),
            Zoom = 1.3f,
            Rotation = 0.5f,
            Viewport = Vp,
        };

        Assert.Equal(camera.GetViewMatrix(Vp), camera.GetViewMatrix());

        var world = new Vector2(77f, 12f);
        AssertClose(camera.WorldToScreen(world, Vp), camera.WorldToScreen(world));

        var screen = new Vector2(300f, 220f);
        AssertClose(camera.ScreenToWorld(screen, Vp), camera.ScreenToWorld(screen));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: FAIL — no parameterless `GetViewMatrix` overload.

- [ ] **Step 3: Implement the no-arg overloads**

Add to `Camera2D.cs` after `ScreenToWorld`:

```csharp
    /// <summary>View matrix using the stored <see cref="Viewport"/> property.</summary>
    public Matrix GetViewMatrix() => GetViewMatrix(Viewport);

    /// <summary>World-to-screen using the stored <see cref="Viewport"/> property.</summary>
    public Vector2 WorldToScreen(Vector2 world) => WorldToScreen(world, Viewport);

    /// <summary>Screen-to-world using the stored <see cref="Viewport"/> property.</summary>
    public Vector2 ScreenToWorld(Vector2 screen) => ScreenToWorld(screen, Viewport);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Graphics/Camera2D.cs KhaozEngine.Tests/CameraTests.cs
git commit -m "Camera2D: no-arg overloads using Viewport property"
```

---

## Task 5: ClampPosition world-bounds helper (TDD)

Add the pure, zoom/viewport-aware clamp. Clamps the axis-aligned visible rect inside
`worldBounds`; centers on an axis when the world is smaller than the view there; ignores
rotation (exact at `Rotation == 0`).

**Files:**
- Modify: `KhaozEngine.Graphics/Camera2D.cs`
- Test: `KhaozEngine.Tests/CameraTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to the `CameraTests` class. Bounds `(0,0,1000,1000)`; at `Zoom=1`, `Vp` is 800x600 so
`halfW=400`, `halfH=300`; valid X range `[400, 600]`, valid Y range `[300, 700]`.

```csharp
    [Fact]
    public void ClampPosition_WorldLargerThanView_ClampsToEdges()
    {
        var camera = new Camera2D { Zoom = 1f };
        var bounds = new Rectangle(0, 0, 1000, 1000); // halfW=400 -> X in [400,600], halfH=300 -> Y in [300,700]

        // Far past top-left -> clamps to (Left+halfW, Top+halfH).
        AssertClose(new Vector2(400f, 300f), camera.ClampPosition(new Vector2(-500f, -500f), bounds, Vp));
        // Far past bottom-right -> clamps to (Right-halfW, Bottom-halfH).
        AssertClose(new Vector2(600f, 700f), camera.ClampPosition(new Vector2(5000f, 5000f), bounds, Vp));
        // Already inside -> unchanged.
        AssertClose(new Vector2(500f, 500f), camera.ClampPosition(new Vector2(500f, 500f), bounds, Vp));
    }

    [Fact]
    public void ClampPosition_WorldSmallerThanViewOnAxis_CentersThatAxis()
    {
        var camera = new Camera2D { Zoom = 1f };
        // World 200 wide (< 800 view) but 2000 tall (> 600 view).
        var bounds = new Rectangle(0, 0, 200, 2000); // X centers at 100; Y halfH=300 -> [300,1700]
        var result = camera.ClampPosition(new Vector2(9999f, 9999f), bounds, Vp);
        AssertClose(new Vector2(100f, 1700f), result);
    }

    [Fact]
    public void ClampPosition_IsZoomAware()
    {
        var bounds = new Rectangle(0, 0, 1000, 1000);
        var desired = new Vector2(-500f, 500f); // past the left edge

        // Zoom 1: halfW=400 -> X clamps to 400.
        var z1 = new Camera2D { Zoom = 1f };
        Assert.Equal(400f, z1.ClampPosition(desired, bounds, Vp).X, 3);

        // Zoom 2: halfW=200 -> X clamps to 200 (less margin needed when zoomed in).
        var z2 = new Camera2D { Zoom = 2f };
        Assert.Equal(200f, z2.ClampPosition(desired, bounds, Vp).X, 3);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: FAIL — no `ClampPosition` method.

- [ ] **Step 3: Implement ClampPosition**

Add to `Camera2D.cs` after the no-arg overloads:

```csharp
    /// <summary>
    /// Returns <paramref name="desired"/> clamped so the visible world rectangle
    /// (viewport size divided by <see cref="Zoom"/>) stays inside <paramref name="worldBounds"/>.
    /// On an axis where the world is smaller than the view, the result is centered on that
    /// axis. Does not mutate <see cref="Position"/> — the caller assigns the result if wanted.
    /// </summary>
    /// <remarks>
    /// Uses the axis-aligned visible rect and ignores <see cref="Rotation"/>: exact when
    /// <see cref="Rotation"/> is 0 (the typical platformer/scroller case); approximate with a
    /// rotated camera, where the true visible area is a rotated quad.
    /// </remarks>
    public Vector2 ClampPosition(Vector2 desired, Rectangle worldBounds, Viewport viewport)
    {
        float halfW = viewport.Width / (2f * Zoom);
        float halfH = viewport.Height / (2f * Zoom);

        float x = worldBounds.Width >= 2f * halfW
            ? MathHelper.Clamp(desired.X, worldBounds.Left + halfW, worldBounds.Right - halfW)
            : worldBounds.Left + worldBounds.Width / 2f;

        float y = worldBounds.Height >= 2f * halfH
            ? MathHelper.Clamp(desired.Y, worldBounds.Top + halfH, worldBounds.Bottom - halfH)
            : worldBounds.Top + worldBounds.Height / 2f;

        return new Vector2(x, y);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Graphics/Camera2D.cs KhaozEngine.Tests/CameraTests.cs
git commit -m "Camera2D: ClampPosition world-bounds helper"
```

---

## Task 6: Full suite + final verification

Confirm the whole engine test suite is green (no regressions) and the new package builds clean.

**Files:** none modified.

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: **281 total, 0 failed** = 268 baseline + 13 new. New cases: WorldOrigin (1) + Position-at-center theory (3) + ScreenToWorld round-trip theory (3) + Zoom (1) + Rotation (1) + NoArg parity (1) + ClampPosition (3) = 13.

- [ ] **Step 2: Build the package in Release to confirm pack-readiness (no pack)**

Run: `dotnet build KhaozEngine.Graphics/KhaozEngine.Graphics.csproj -c Release`
Expected: Build succeeded, 0 errors. (Do NOT run `dotnet pack` into the shared feed — coordinator owns release.)

- [ ] **Step 3: Confirm release-discipline files are untouched**

Run: `git status --porcelain Directory.Build.props CHANGELOG.md`
Expected: empty output (neither file modified).

- [ ] **Step 4: Final review commit (if any stray changes); otherwise done**

```bash
git status
```
Expected: clean tree (all work already committed in Tasks 1-5). No further commit needed.

---

## Self-Review Notes

- **Spec coverage:** package scaffold (T1), view matrix + Position-at-center invariant test 1/1b (T2), screen<->world round-trip + zoom + rotation tests 2/3/4 (T3), no-arg parity test 5 (T4), ClampPosition tests 6/7/8 (T5), full-suite regression (T6). Zoom>0 documented on the property (T1) and `ScreenToWorld` remark (T3). All spec sections mapped.
- **Test count:** 13 new test cases (theories counted per InlineData). Final expected total 281.
- **Type consistency:** signatures `GetViewMatrix(Viewport)`, `WorldToScreen(Vector2, Viewport)`, `ScreenToWorld(Vector2, Viewport)`, `ClampPosition(Vector2, Rectangle, Viewport)` and the three no-arg overloads are identical across spec, implementation, and tests.
- **Rotation-direction caveat:** Task 3 instructs the implementer to correct the *expected* test value (not the matrix) if MonoGame's empirical rotation sign differs, keeping the spec-fixed matrix order intact.
