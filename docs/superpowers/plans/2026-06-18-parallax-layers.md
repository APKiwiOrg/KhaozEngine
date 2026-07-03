# Parallax Background Layers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add parallax scroll math to 5.x `KhaozEngine.Render2D`: `ParallaxLayer` (per-axis scroll factor → layer view position) and `Parallax.Wrap` (positive modulo for seamless background tiling).

**Architecture:** Two small pure units. The engine provides the scroll math; the game owns layer textures and the draw loop (deriving a layer `Camera2D` from `ParallaxLayer.ViewPosition`). Translation-only parallax; zoom/rotation shared with the main camera.

**Tech Stack:** C# / net10.0, `System.Numerics` (`Vector2`), xUnit. Headless.

**Spec:** `docs/superpowers/specs/2026-06-18-parallax-layers-design.md`

---

## File Structure

- `KhaozEngine.Render2D/ParallaxLayer.cs` (new) — the layer value (Factor + ViewPosition).
- `KhaozEngine.Render2D/Parallax.cs` (new) — the `Wrap` tiling helper.
- `KhaozEngine.Tests/Render2DParallaxTests.cs` (new) — headless coverage.
- Release files (Task 2): `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

Reference facts:
- `KhaozEngine.Render2D` uses `System.Numerics`. Run all `dotnet` commands from the worktree root.
- Current 5.x version: 5.56.0 → next 5.57.0.

---

## Task 1: `ParallaxLayer` + `Parallax.Wrap`

**Files:**
- Create: `KhaozEngine.Render2D/ParallaxLayer.cs`, `KhaozEngine.Render2D/Parallax.cs`
- Test: `KhaozEngine.Tests/Render2DParallaxTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render2DParallaxTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for parallax scroll math (ParallaxLayer + Parallax.Wrap).</summary>
public class Render2DParallaxTests
{
    private const float Tol = 1e-4f;

    private static void AssertClose(Vector2 expected, Vector2 actual) =>
        Assert.True(Vector2.Distance(expected, actual) <= Tol, $"expected {expected}, got {actual}");

    // ---- ParallaxLayer ----

    [Fact]
    public void Layer_ViewPositionScalesByFactorPerAxis()
    {
        var layer = new ParallaxLayer(new Vector2(0.5f, 1f));
        AssertClose(new Vector2(100f, 50f), layer.ViewPosition(new Vector2(200f, 50f)));
    }

    [Fact]
    public void Layer_ZeroFactorIsStaticBackdrop()
    {
        var layer = new ParallaxLayer(Vector2.Zero);
        AssertClose(Vector2.Zero, layer.ViewPosition(new Vector2(200f, 50f)));
    }

    [Fact]
    public void Layer_UnitFactorLocksToCamera()
    {
        var layer = new ParallaxLayer(new Vector2(1f, 1f));
        var cam = new Vector2(123f, -45f);
        AssertClose(cam, layer.ViewPosition(cam));
    }

    [Fact]
    public void Layer_UniformCtorSetsBothAxes()
    {
        var layer = new ParallaxLayer(0.25f);
        Assert.Equal(0.25f, layer.Factor.X, Tol);
        Assert.Equal(0.25f, layer.Factor.Y, Tol);
        AssertClose(new Vector2(25f, 50f), layer.ViewPosition(new Vector2(100f, 200f)));
    }

    // ---- Parallax.Wrap ----

    [Fact]
    public void Wrap_ReturnsRemainderInRange()
    {
        Assert.Equal(50f, Parallax.Wrap(250f, 100f), Tol);
        Assert.Equal(0f, Parallax.Wrap(100f, 100f), Tol);
        Assert.Equal(0f, Parallax.Wrap(0f, 100f), Tol);
    }

    [Fact]
    public void Wrap_NegativeValueGivesPositiveRemainder()
    {
        Assert.Equal(70f, Parallax.Wrap(-30f, 100f), Tol);
    }

    [Fact]
    public void Wrap_NonPositiveSizeReturnsZero()
    {
        Assert.Equal(0f, Parallax.Wrap(5f, 0f), Tol);
        Assert.Equal(0f, Parallax.Wrap(5f, -2f), Tol);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DParallaxTests"`
Expected: FAIL — `ParallaxLayer` / `Parallax` do not exist (compile error).

- [ ] **Step 3: Write `ParallaxLayer`**

Create `KhaozEngine.Render2D/ParallaxLayer.cs`:

```csharp
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A background layer's parallax rate. <see cref="Factor"/> is per-axis relative to the camera:
    /// <c>0</c> = static (a fixed backdrop / skybox), <c>1</c> = locked to the world (moves with the
    /// foreground), <c>0.5</c> = half speed (appears farther away). The game derives a layer camera from
    /// <see cref="ViewPosition"/> and draws the layer's sprites with it (zoom/rotation shared with the main
    /// camera; parallax is translation-only).
    /// </summary>
    public readonly struct ParallaxLayer
    {
        /// <summary>Per-axis scroll rate relative to the camera.</summary>
        public readonly Vector2 Factor;

        public ParallaxLayer(Vector2 factor) => Factor = factor;

        /// <summary>Uniform factor on both axes.</summary>
        public ParallaxLayer(float factor) => Factor = new Vector2(factor, factor);

        /// <summary>World position a layer's camera should sit at for the given
        /// <paramref name="cameraPosition"/>: <c>cameraPosition * Factor</c>, per axis.</summary>
        public Vector2 ViewPosition(Vector2 cameraPosition) => cameraPosition * Factor;
    }
}
```

- [ ] **Step 4: Write `Parallax`**

Create `KhaozEngine.Render2D/Parallax.cs`:

```csharp
using System;

namespace KhaozEngine.Render2D
{
    /// <summary>Parallax scroll-math helpers.</summary>
    public static class Parallax
    {
        /// <summary>
        /// Non-negative remainder (<paramref name="value"/> mod <paramref name="size"/>, in
        /// <c>[0, size)</c>) for seamlessly tiling a repeating background: the game draws copies starting at
        /// <c>-Wrap(layerViewX, tileWidth)</c> across the viewport. Returns 0 when <paramref name="size"/> is
        /// non-positive (no divide-by-zero / NaN).
        /// </summary>
        public static float Wrap(float value, float size)
        {
            if (size <= 0f) return 0f;
            return value - size * MathF.Floor(value / size);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DParallaxTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Run the full suite for regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green; prior count + 7 new).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Render2D/ParallaxLayer.cs KhaozEngine.Render2D/Parallax.cs KhaozEngine.Tests/Render2DParallaxTests.cs
git commit -m "feat(render2d): parallax scroll math — ParallaxLayer + Parallax.Wrap"
```

---

## Task 2: Release ritual (5.57.0)

Additive change → minor bump. Follows `KhaozEngine/CLAUDE.md` release order.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the 5.x version**

In `Directory.Build.props`, change `<KhaozEngineVersion>5.56.0</KhaozEngineVersion>` to `5.57.0`.

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly above the `## 5.56.0 (custom 5.x line)` heading:

```markdown
## 5.57.0 (custom 5.x line)

Parallax background layers - the final camera feel-layer slice. With this, the roadmap camera backlog
(follow, look-ahead, pixel snap, multi-target framing, eased blends, room cameras, screen shake, parallax)
is complete.

- **`ParallaxLayer`** (`KhaozEngine.Render2D`) - a per-axis scroll `Factor` (0 = static backdrop, 1 = locked
  to the world, 0.5 = half speed / farther) with `ViewPosition(cameraPosition) = cameraPosition * Factor`.
  The game derives a layer `Camera2D` from it and draws the layer's sprites; parallax is translation-only
  (zoom/rotation are shared with the main camera).
- **`Parallax.Wrap(value, size)`** (`KhaozEngine.Render2D`) - a positive modulo (`[0, size)`) for seamlessly
  tiling a repeating background; the game draws copies starting at `-Wrap(layerViewX, tileWidth)`. Returns 0
  for non-positive size.
```

- [ ] **Step 3: Update the three guard-checked doc declarations**

In `docs/CONSUMERS.md`, change the `**Engine current version:** \`5.56.0\`` line to `5.57.0`.
In `docs/ROADMAP.md`, change the `Current released version: **5.56.0**` line (near the top) to `5.57.0`.
In `README.md`, change every `Version="5.56.0"` in the `<PackageReference>` example block to `5.57.0` (grep `grep -n "5.56.0" README.md` first to find all ~4 lines).

- [ ] **Step 4: Update the ROADMAP camera section**

In `docs/ROADMAP.md`, in the "Camera: first-class follow / scroller camera" section:

(a) Under the `**Shipped:**` list, append:

```markdown
- 5.57.0: `ParallaxLayer` + `Parallax.Wrap` (`KhaozEngine.Render2D`) - **parallax background layers**:
  per-axis scroll factor (0 = static .. 1 = world-locked) off the same camera, plus a positive-modulo tiling
  helper for infinitely repeating backgrounds. Completes the camera feel-layer backlog.
```

(b) From the `**Still open**` list, delete the bullet beginning `- Parallax background layers`. If that
leaves the "Still open" list empty, replace its remaining content with a single line: `- (none — the camera
feel-layer backlog is complete as of 5.57.0)`. If other bullets remain, leave them. Report what was left.

- [ ] **Step 5: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (declarations match `<KhaozEngineVersion>` = 5.57.0).

- [ ] **Step 6: Test and pack**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green).
Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds `KhaozEngine.Render2D.5.57.0.nupkg` (and the other 5.x packages) into `local-feed` (cumulative; do not delete old versions). Confirm with `ls local-feed/KhaozEngine.Render2D.5.57.0.nupkg`.

- [ ] **Step 7: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render2d(5.57.0): parallax background layers — ParallaxLayer + Parallax.Wrap"
git tag v5.57.0
```

Pushing `main` + the tag happens at branch-finish time, not here.

---

## Self-Review Notes

- **Spec coverage:** `ParallaxLayer` Factor/ViewPosition/uniform-ctor (Task 1), `Parallax.Wrap` positive-modulo + non-positive guard (Task 1), all test-matrix items mapped, release incl. ROADMAP edits (Task 2).
- **Type consistency:** `ParallaxLayer(Vector2)` + `ParallaxLayer(float)` + `Factor` + `ViewPosition(Vector2)`; `Parallax.Wrap(float, float)`. Consistent between Task 1's tests and implementation.
- **No placeholders:** every code step shows complete code; commands have expected output.
