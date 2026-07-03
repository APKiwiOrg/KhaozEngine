# KhaozEngine.Gui Modern Primitives + Icon System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add rounded corners, vertical gradient fills, soft drop shadows, hover glow, and a procedural tintable icon system to `KhaozEngine.Gui` (built on a new SDF path in the shared `KhaozEngine.Render2D` SpriteBatch), all opt-in so existing screens render byte-identically.

**Architecture:** One unified 64-byte SpriteBatch vertex carrying SDF params; the shared fragment shader branches on a per-vertex `Mode` flag — flag `0` is the literal current `texture * vColor` expression (byte-identical), flag `1` multiplies alpha by a rounded-box SDF coverage (corners/shadow/glow/border-ring). Gradients reuse the existing interpolated per-vertex `Color`. `GuiStyle` gains defaulted-off fields; `GuiDraw` branches to the plain or modern path centrally so all widgets inherit it. Icons are CPU-baked alpha masks in a string-keyed registry, following the existing `VfxTextures` pattern.

**Tech Stack:** C# net10.0, Veldrid-behind-KhaozEngine.Gpu GLSL-450→SPIR-V shaders, xUnit (headless + `KE_GPU_TESTS=1` gated goldens), System.Numerics.

**Working directory:** worktree `feature/gui-modern-primitives` at `/Users/antonio/KhaozEngine/.claude/worktrees/feature+gui-modern-primitives`. All paths below are repo-relative.

**Build/test commands:**
- Build: `dotnet build KhaozEngine.Gui/KhaozEngine.Gui.csproj`
- Headless tests: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Gated GPU goldens (macOS/Metal here): `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter GoldenSnapshotTests`

---

## File Structure

**Render2D (SDF path):**
- Modify `KhaozEngine.Render2D/SpriteBatch.cs` — widen `V`, new vertex-layout elements, new shaders, new emit + public `DrawRounded`/gradient `Draw`, pure static SDF/gradient helpers.

**Gui (style + draw + icons + widgets):**
- Modify `KhaozEngine.Gui/GuiStyle.cs` — `GuiFill` enum, new fields, `Modern` preset, `ScaleRgb` helper.
- Modify `KhaozEngine.Gui/GuiDraw.cs` — plain/modern branch in `Fill`/`Border`/`DrawButton`/`DrawSlider`.
- Create `KhaozEngine.Gui/Icons.cs` — core icon id constants.
- Create `KhaozEngine.Gui/IconAtlas.cs` — procedural bake + registry + upload overloads.
- Modify `KhaozEngine.Gui/GuiSurface.cs` — `IconAtlas` property, `Icon`, `IconButton`, `StatChip`, `Panel(rect, style)`.

**Tests:**
- Create `KhaozEngine.Tests/Render2D/SpriteBatchRoundedTests.cs`
- Create `KhaozEngine.Tests/Gui/GuiStyleModernTests.cs`
- Create `KhaozEngine.Tests/Gui/IconAtlasTests.cs`
- Modify `KhaozEngine.Tests/Gui/GuiSurfaceTests.cs` (icon/widget headless tests) — or create `KhaozEngine.Tests/Gui/IconWidgetTests.cs`
- Modify `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` — add `Golden2D_Modern`.

**Docs/release:**
- Modify `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

---

## Task 1: Widen the SpriteBatch vertex + SDF shader (plain path stays identical)

**Files:**
- Modify: `KhaozEngine.Render2D/SpriteBatch.cs`
- Test: `KhaozEngine.Tests/Render2D/SpriteBatchRoundedTests.cs` (create)

This task changes the vertex format, shaders, layout, and `VertexSizeBytes`, and defaults all existing emits to `Mode = 0`. After it, the engine compiles and all existing tests/goldens still pass (plain path unchanged). Public rounded/gradient API arrives in Task 2.

- [ ] **Step 1: Write the failing test for the pure SDF helpers**

Create `KhaozEngine.Tests/Render2D/SpriteBatchRoundedTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>Headless coverage for the pure rounded/gradient vertex-build helpers (no GPU device).</summary>
    public class SpriteBatchRoundedTests
    {
        [Fact]
        public void RoundedLocals_AreCornerOffsetsFromCentre()
        {
            // For a w x h rect, the four local corners are +/- half-extents from the centre,
            // TL, TR, BR, BL in that order.
            var (tl, tr, br, bl) = SpriteBatch.RoundedLocals(200f, 100f);
            Assert.Equal(new Vector2(-100f, -50f), tl);
            Assert.Equal(new Vector2(100f, -50f), tr);
            Assert.Equal(new Vector2(100f, 50f), br);
            Assert.Equal(new Vector2(-100f, -50f), tl);
            Assert.Equal(new Vector2(-100f, 50f), bl);
        }

        [Fact]
        public void RoundedShape_PacksHalfExtentsRadiusSoftness()
        {
            Vector4 s = SpriteBatch.RoundedShape(200f, 100f, radius: 8f, softness: 3f);
            Assert.Equal(new Vector4(100f, 50f, 8f, 3f), s);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SpriteBatchRoundedTests`
Expected: FAIL — `SpriteBatch` has no `RoundedLocals` / `RoundedShape`.

- [ ] **Step 3: Replace the shaders, vertex struct, layout, and stride in `SpriteBatch.cs`**

Replace the `VertSrc`/`FragSrc` constants (lines ~17-31) with:

```csharp
        const string VertSrc = @"#version 450
layout(location=0) in vec2 ClipPos;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 Local;
layout(location=4) in vec4 Shape;
layout(location=5) in vec2 Mode;
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vColor;
layout(location=2) out vec2 vLocal;
layout(location=3) out vec4 vShape;
layout(location=4) out vec2 vMode;
void main() {
    gl_Position = vec4(ClipPos, 0.0, 1.0);
    vUv = Uv; vColor = Color; vLocal = Local; vShape = Shape; vMode = Mode;
}";

        const string FragSrc = @"#version 450
layout(set=0, binding=0) uniform texture2D Tex;
layout(set=0, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=2) in vec2 vLocal;
layout(location=3) in vec4 vShape;
layout(location=4) in vec2 vMode;
layout(location=0) out vec4 oColor;
void main() {
    vec4 base = texture(sampler2D(Tex, Samp), vUv) * vColor;
    if (vMode.y < 0.5) {
        oColor = base;                       // plain draws: byte-identical to before
    } else {
        vec2 b = vShape.xy;
        float r = vShape.z;
        float soft = vShape.w;
        float stroke = vMode.x;
        vec2 q = abs(vLocal) - b + r;
        float d = min(max(q.x, q.y), 0.0) + length(max(q, vec2(0.0))) - r;
        if (stroke > 0.0) d = abs(d) - stroke * 0.5;
        float aa = soft > 0.0 ? soft : max(fwidth(d), 1e-4);
        float cov = clamp(0.5 - d / aa, 0.0, 1.0);
        base.a *= cov;
        oColor = base;
    }
}";
```

Replace the `V` struct (line ~33) with:

```csharp
        struct V
        {
            public Vector2 Pos; public Vector2 Uv; public Vector4 Color;
            public Vector2 Local; public Vector4 Shape; public Vector2 Mode;
        }
```

Change `VertexSizeBytes` (line ~58):

```csharp
        const uint VertexSizeBytes = 64;       // V = Pos(8)+Uv(8)+Color(16)+Local(8)+Shape(16)+Mode(8)
```

Add the three vertex elements in the constructor's `vl` (after the `Color` element, line ~86):

```csharp
            var vl = new GpuVertexLayoutDescription(
                new GpuVertexElement("ClipPos", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Local", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Shape", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Mode", GpuVertexElementFormat.Float2));
```

- [ ] **Step 4: Update `EmitQuad` to default the new fields, and add the pure helpers**

Replace the existing `EmitQuad` body (the six `_runs.Add(...)` lines, ~288-289) so every plain vertex carries zeroed SDF fields (`Mode = (0,0)` → disabled branch). Add an SDF-aware overload + pure static helpers. Replace the `EmitQuad` method with:

```csharp
        void EmitQuad(Texture2D tex, Vector2 worldTL, Vector2 worldTR, Vector2 worldBR, Vector2 worldBL, Vector4 srcUV, Vector4 color)
        {
            // Plain path: zero SDF fields, single colour on all four corners, Mode.y = 0 (disabled).
            EmitQuad(tex, worldTL, worldTR, worldBR, worldBL, srcUV, color, color,
                Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector4.Zero, Vector2.Zero);
        }

        // Full emit: per-corner colour (top vs bottom for gradients) + per-corner Local + shared Shape/Mode.
        void EmitQuad(Texture2D tex, Vector2 worldTL, Vector2 worldTR, Vector2 worldBR, Vector2 worldBL,
            Vector4 srcUV, Vector4 colorTop, Vector4 colorBottom,
            Vector2 localTL, Vector2 localTR, Vector2 localBR, Vector2 localBL, Vector4 shape, Vector2 mode)
        {
            Vector2 tl = Clip(worldTL.X, worldTL.Y), tr = Clip(worldTR.X, worldTR.Y), br = Clip(worldBR.X, worldBR.Y), bl = Clip(worldBL.X, worldBL.Y);
            var uTL = new Vector2(srcUV.X, srcUV.Y); var uTR = new Vector2(srcUV.Z, srcUV.Y);
            var uBR = new Vector2(srcUV.Z, srcUV.W); var uBL = new Vector2(srcUV.X, srcUV.W);
            object key = _blend == BlendMode.Alpha ? tex.Handle : AdditiveKeyFor(tex.Handle);
            V vtl = new V { Pos = tl, Uv = uTL, Color = colorTop, Local = localTL, Shape = shape, Mode = mode };
            V vtr = new V { Pos = tr, Uv = uTR, Color = colorTop, Local = localTR, Shape = shape, Mode = mode };
            V vbr = new V { Pos = br, Uv = uBR, Color = colorBottom, Local = localBR, Shape = shape, Mode = mode };
            V vbl = new V { Pos = bl, Uv = uBL, Color = colorBottom, Local = localBL, Shape = shape, Mode = mode };
            _runs.Add(key, vtl); _runs.Add(key, vtr); _runs.Add(key, vbr);
            _runs.Add(key, vtl); _runs.Add(key, vbr); _runs.Add(key, vbl);
        }

        /// <summary>The four rect-local corner offsets (TL, TR, BR, BL) from the centre of a w x h rect. Pure / headless.</summary>
        internal static (Vector2 TL, Vector2 TR, Vector2 BR, Vector2 BL) RoundedLocals(float w, float h)
        {
            float hx = w * 0.5f, hy = h * 0.5f;
            return (new Vector2(-hx, -hy), new Vector2(hx, -hy), new Vector2(hx, hy), new Vector2(-hx, hy));
        }

        /// <summary>Packs the SDF Shape attribute = (halfX, halfY, radius, softness). Pure / headless.</summary>
        internal static Vector4 RoundedShape(float w, float h, float radius, float softness) =>
            new Vector4(w * 0.5f, h * 0.5f, radius, softness);
```

Note: the two-colour `EmitQuad` assigns `colorTop` to the TL/TR vertices and `colorBottom` to BR/BL, so a vertical gradient interpolates across the quad. Plain draws pass the same colour for both → identical to before.

- [ ] **Step 5: Build and run the full headless suite**

Run: `dotnet build KhaozEngine.Render2D/KhaozEngine.Render2D.csproj` then `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SpriteBatchRoundedTests`
Expected: build succeeds; `SpriteBatchRoundedTests` PASS.

- [ ] **Step 6: Run the existing Render2D headless tests to confirm no regression**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Render2D"`
Expected: PASS (vertex change is internal; `QuadRunBuilder`/order tests unaffected).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Render2D/SpriteBatch.cs KhaozEngine.Tests/Render2D/SpriteBatchRoundedTests.cs
git commit -m "render2d(7.4.0): widen SpriteBatch vertex to 64B + SDF fragment branch (plain path identical)"
```

---

## Task 2: Public gradient + rounded Draw overloads

**Files:**
- Modify: `KhaozEngine.Render2D/SpriteBatch.cs`
- Test: `KhaozEngine.Tests/Render2D/SpriteBatchRoundedTests.cs`

- [ ] **Step 1: Write the failing tests for the public builders**

Append to `SpriteBatchRoundedTests.cs` (inside the class):

```csharp
        [Fact]
        public void RoundedMode_FilledVsStroke()
        {
            // Filled fill: stroke 0, modeFlag 1.
            Assert.Equal(new Vector2(0f, 1f), SpriteBatch.RoundedMode(strokeWidth: 0f));
            // Border ring: stroke > 0, modeFlag 1.
            Assert.Equal(new Vector2(2.5f, 1f), SpriteBatch.RoundedMode(strokeWidth: 2.5f));
        }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SpriteBatchRoundedTests`
Expected: FAIL — no `RoundedMode`.

- [ ] **Step 3: Add the helper + public overloads in `SpriteBatch.cs`**

Add near the other rounded helpers:

```csharp
        /// <summary>Packs the Mode attribute = (strokeWidth, 1). Pure / headless. modeFlag 1 enables the SDF branch.</summary>
        internal static Vector2 RoundedMode(float strokeWidth) => new Vector2(strokeWidth, 1f);
```

Add public overloads next to the existing `Draw(Texture2D, Vector4, Vector4, Color)` (around line 236):

```csharp
        /// <summary>
        /// Vertical 2-tone fill: <paramref name="top"/> on the upper edge, <paramref name="bottom"/> on the lower
        /// edge, interpolated by the per-vertex colour. dest = (x, y, w, h); whole-texture UV. Plain (non-rounded).
        /// </summary>
        public void Draw(Texture2D tex, Vector4 destRect, Color top, Color bottom)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            EmitQuad(tex, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h),
                new Vector4(0, 0, 1, 1), (Vector4)top, (Vector4)bottom,
                Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector4.Zero, Vector2.Zero);
        }

        /// <summary>
        /// Rounded-rect draw with optional vertical gradient, soft edge, and stroke. <paramref name="cornerRadius"/>
        /// in draw units; <paramref name="softness"/> 0 = crisp fwidth AA, &gt;0 = soft falloff (shadow/glow);
        /// <paramref name="strokeWidth"/> 0 = filled, &gt;0 = ring (border). Alpha-shaped by an SDF in the shared
        /// shader; batches with everything. Use the white texture for solid fills.
        /// </summary>
        public void DrawRounded(Texture2D tex, Vector4 destRect, Vector4 srcUV, Color top, Color bottom,
            float cornerRadius, float softness = 0f, float strokeWidth = 0f)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            var (lTL, lTR, lBR, lBL) = RoundedLocals(w, h);
            Vector4 shape = RoundedShape(w, h, cornerRadius, softness);
            Vector2 mode = RoundedMode(strokeWidth);
            EmitQuad(tex, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h),
                srcUV, (Vector4)top, (Vector4)bottom, lTL, lTR, lBR, lBL, shape, mode);
        }

        /// <summary>Rounded-rect convenience: single colour, whole-texture UV.</summary>
        public void DrawRounded(Texture2D tex, Vector4 destRect, Color color,
            float cornerRadius, float softness = 0f, float strokeWidth = 0f) =>
            DrawRounded(tex, destRect, new Vector4(0, 0, 1, 1), color, color, cornerRadius, softness, strokeWidth);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SpriteBatchRoundedTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/SpriteBatch.cs KhaozEngine.Tests/Render2D/SpriteBatchRoundedTests.cs
git commit -m "render2d(7.4.0): public DrawRounded + vertical-gradient Draw overloads"
```

---

## Task 3: GuiStyle modern fields + Modern preset

**Files:**
- Modify: `KhaozEngine.Gui/GuiStyle.cs`
- Test: `KhaozEngine.Tests/Gui/GuiStyleModernTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Gui/GuiStyleModernTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class GuiStyleModernTests
    {
        [Fact]
        public void Default_KeepsTodaysFlatLook()
        {
            var s = GuiStyle.Default;
            Assert.Equal(0f, s.CornerRadius);
            Assert.Equal(0f, s.ShadowSize);
            Assert.Equal(GuiFill.Solid, s.FillMode);
            Assert.Equal(1f, s.GradientTopScale);
            Assert.Equal(1f, s.GradientBottomScale);
            Assert.Equal(0f, s.GlowSize);
            Assert.True(s.IsFlat);   // the plain-path predicate
        }

        [Fact]
        public void Modern_OptsIntoRoundedShadowGradientGlow()
        {
            var s = GuiStyle.Modern;
            Assert.True(s.CornerRadius > 0f);
            Assert.True(s.ShadowSize > 0f);
            Assert.Equal(GuiFill.VerticalGradient, s.FillMode);
            Assert.True(s.GlowSize > 0f);
            Assert.False(s.IsFlat);
        }

        [Fact]
        public void ScaleRgb_MultipliesRgbKeepsAlpha()
        {
            var c = new Vector4(0.4f, 0.5f, 0.6f, 0.8f);
            var scaled = GuiStyle.ScaleRgb(c, 1.5f);
            Assert.Equal(0.6f, scaled.X, 3);
            Assert.Equal(0.75f, scaled.Y, 3);
            Assert.Equal(0.9f, scaled.Z, 3);
            Assert.Equal(0.8f, scaled.W, 3);   // alpha untouched
        }

        [Fact]
        public void ScaleRgb_ClampsToOne()
        {
            var scaled = GuiStyle.ScaleRgb(new Vector4(0.8f, 0.8f, 0.8f, 1f), 2f);
            Assert.Equal(1f, scaled.X, 3);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter GuiStyleModernTests`
Expected: FAIL — missing members.

- [ ] **Step 3: Extend `GuiStyle.cs`**

Add the enum above the struct, the fields inside it, the `IsFlat` predicate, `ScaleRgb`, and the `Modern` preset. Insert after the `GuiAlign` enum:

```csharp
    /// <summary>How a widget body is filled. <see cref="Solid"/> is the flat default.</summary>
    public enum GuiFill { Solid, VerticalGradient }
```

Add these fields to the `GuiStyle` struct after `BorderThickness`:

```csharp
        /// <summary>Corner radius in draw units. 0 (default) = hard corners (today's look).</summary>
        public float CornerRadius;
        /// <summary>Soft drop-shadow spread in draw units. 0 (default) = no shadow.</summary>
        public float ShadowSize;
        /// <summary>Drop-shadow colour (default transparent).</summary>
        public Vector4 ShadowColor;
        /// <summary>Drop-shadow offset in draw units (default (0,0)).</summary>
        public Vector2 ShadowOffset;
        /// <summary>Body fill mode (default <see cref="GuiFill.Solid"/>).</summary>
        public GuiFill FillMode;
        /// <summary>Top-edge RGB multiplier of the active state colour when <see cref="GuiFill.VerticalGradient"/> (default 1).</summary>
        public float GradientTopScale;
        /// <summary>Bottom-edge RGB multiplier of the active state colour when <see cref="GuiFill.VerticalGradient"/> (default 1).</summary>
        public float GradientBottomScale;
        /// <summary>Hover-glow colour (default transparent).</summary>
        public Vector4 GlowColor;
        /// <summary>Hover-glow spread in draw units. 0 (default) = no glow.</summary>
        public float GlowSize;

        /// <summary>
        /// True when every modern knob is at its off default, so <see cref="GuiDraw"/> takes the plain
        /// single-quad path that renders byte-identically to pre-7.4.0.
        /// </summary>
        public bool IsFlat =>
            CornerRadius == 0f && ShadowSize == 0f && FillMode == GuiFill.Solid && GlowSize == 0f;
```

Update the `Default` initializer to set the gradient scales to 1 (so `IsFlat` holds and gradient math is a no-op if ever taken):

```csharp
        public static GuiStyle Default => new()
        {
            Fill = new Vector4(0.18f, 0.30f, 0.42f, 1f),
            Hover = new Vector4(0.26f, 0.50f, 0.66f, 1f),
            Press = new Vector4(0.20f, 0.40f, 0.55f, 1f),
            Border = new Vector4(0.30f, 0.38f, 0.52f, 1f),
            Text = Vector4.One,
            DisabledFill = new Vector4(0.14f, 0.15f, 0.18f, 0.9f),
            DisabledText = new Vector4(0.5f, 0.5f, 0.55f, 1f),
            SelectedFill = new Vector4(0.28f, 0.46f, 0.66f, 1f),
            SelectedBorder = new Vector4(0.55f, 0.80f, 1f, 1f),
            BorderThickness = 1.5f,
            FillMode = GuiFill.Solid,
            GradientTopScale = 1f,
            GradientBottomScale = 1f,
        };

        /// <summary>
        /// The default palette with modern affordances switched on: rounded corners, a soft drop shadow, a subtle
        /// vertical gradient, and a hover glow. Opt in with <c>ui.Style = GuiStyle.Modern</c>; games tune the palette.
        /// </summary>
        public static GuiStyle Modern
        {
            get
            {
                var s = Default;
                s.CornerRadius = 7f;
                s.ShadowSize = 8f;
                s.ShadowColor = new Vector4(0f, 0f, 0f, 0.40f);
                s.ShadowOffset = new Vector2(0f, 3f);
                s.FillMode = GuiFill.VerticalGradient;
                s.GradientTopScale = 1.12f;
                s.GradientBottomScale = 0.85f;
                s.GlowColor = new Vector4(0.55f, 0.80f, 1f, 0.35f);
                s.GlowSize = 10f;
                return s;
            }
        }

        /// <summary>Multiply RGB by <paramref name="scale"/> (clamped to [0,1] per channel), keeping alpha. Pure.</summary>
        public static Vector4 ScaleRgb(Vector4 c, float scale) => new Vector4(
            System.Math.Clamp(c.X * scale, 0f, 1f),
            System.Math.Clamp(c.Y * scale, 0f, 1f),
            System.Math.Clamp(c.Z * scale, 0f, 1f),
            c.W);
```

Ensure `using System.Numerics;` is present (it is).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter GuiStyleModernTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/GuiStyle.cs KhaozEngine.Tests/Gui/GuiStyleModernTests.cs
git commit -m "gui(7.4.0): GuiStyle modern fields + Modern preset + ScaleRgb"
```

---

## Task 4: GuiDraw plain/modern branch

**Files:**
- Modify: `KhaozEngine.Gui/GuiDraw.cs`

The plain path must call the **exact existing** `batch.Draw(white, destRect, color)` / 4-edge border so `IsFlat` styles stay byte-identical. The modern path draws shadow → fill → border-ring → (hover) glow. Visual output is verified by the golden in Task 9; this task wires the branch and keeps the build + existing Gui tests green.

- [ ] **Step 1: Add a private modern-fill helper + a styled fill entry point in `GuiDraw.cs`**

Add these methods to `GuiDraw` (keep the existing `Fill`/`Border` exactly as they are — they remain the plain path):

```csharp
        /// <summary>
        /// Fill <paramref name="r"/> honouring <paramref name="style"/>: when <see cref="GuiStyle.IsFlat"/> this is
        /// the exact plain single-quad <see cref="Fill"/> (byte-identical to pre-7.4.0); otherwise it draws the
        /// soft shadow, the rounded (optionally gradient) body, and the rounded border. <paramref name="bodyColor"/>
        /// is the resolved state colour (hover/press/etc.). <paramref name="borderColor"/> is the outline.
        /// </summary>
        public static void FillStyled(SpriteBatch batch, Texture2D white, Rect r, in GuiStyle style,
            Vector4 bodyColor, Vector4 borderColor)
        {
            if (style.IsFlat)
            {
                Fill(batch, white, r, bodyColor);
                Border(batch, white, r, style.BorderThickness, borderColor);
                return;
            }

            var dest = new Vector4(r.X, r.Y, r.Width, r.Height);

            // Soft drop shadow under everything.
            if (style.ShadowSize > 0f && style.ShadowColor.W > 0f)
            {
                var shadow = new Vector4(r.X + style.ShadowOffset.X, r.Y + style.ShadowOffset.Y, r.Width, r.Height);
                batch.DrawRounded(white, shadow, (Color)style.ShadowColor, style.CornerRadius, softness: style.ShadowSize);
            }

            // Rounded body: vertical gradient (scale of the state colour) or flat.
            Vector4 top = bodyColor, bottom = bodyColor;
            if (style.FillMode == GuiFill.VerticalGradient)
            {
                top = GuiStyle.ScaleRgb(bodyColor, style.GradientTopScale);
                bottom = GuiStyle.ScaleRgb(bodyColor, style.GradientBottomScale);
            }
            batch.DrawRounded(white, dest, new Vector4(0, 0, 1, 1), (Color)top, (Color)bottom, style.CornerRadius);

            // Rounded border ring.
            if (style.BorderThickness > 0f)
                batch.DrawRounded(white, dest, (Color)borderColor, style.CornerRadius, softness: 0f, strokeWidth: style.BorderThickness);
        }

        /// <summary>Draw a hover glow halo behind/around <paramref name="r"/> (additive) when the style enables it.</summary>
        public static void HoverGlow(SpriteBatch batch, Texture2D white, Rect r, in GuiStyle style)
        {
            if (style.GlowSize <= 0f || style.GlowColor.W <= 0f) return;
            var prev = batch.BlendMode;
            batch.BlendMode = BlendMode.Additive;
            float g = style.GlowSize;
            var dest = new Vector4(r.X - g * 0.5f, r.Y - g * 0.5f, r.Width + g, r.Height + g);
            batch.DrawRounded(white, dest, (Color)style.GlowColor, style.CornerRadius + g * 0.5f, softness: g);
            batch.BlendMode = prev;
        }
```

- [ ] **Step 2: Route `DrawButton` and `DrawSlider` through the styled path**

In `DrawButton`, replace the two body/border draw lines:

```csharp
            Fill(batch, white, rect, fill);
            Border(batch, white, rect, style.BorderThickness, border);
```

with:

```csharp
            if (hover && enabled) HoverGlow(batch, white, rect, style);
            FillStyled(batch, white, rect, style, fill, border);
```

In `DrawSlider`, replace the track fill and handle drawing to honour the style. Change the track line:

```csharp
            Fill(batch, white, track, enabled ? style.Fill : style.DisabledFill);
```

to keep the thin track flat (a 2px track gets no rounding benefit — leave it as `Fill`). Change the handle block:

```csharp
            var handle = new Rect(centerX - half, rect.Y, half * 2f, rect.Height);
            Fill(batch, white, handle, knob);
            Border(batch, white, handle, style.BorderThickness, enabled ? style.Border : style.DisabledText);
```

to:

```csharp
            var handle = new Rect(centerX - half, rect.Y, half * 2f, rect.Height);
            FillStyled(batch, white, handle, style, knob, enabled ? style.Border : style.DisabledText);
```

(The accent fill and track stay flat; only the knob picks up the modern look, which is the visible affordance.)

- [ ] **Step 3: Build the Gui project**

Run: `dotnet build KhaozEngine.Gui/KhaozEngine.Gui.csproj`
Expected: build succeeds.

- [ ] **Step 4: Run the full headless Gui suite to confirm no behavioural regression**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Gui"`
Expected: PASS (existing widget tests use a null batch and assert interaction/return values, which are unchanged; the new draw path only runs with a non-null batch).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/GuiDraw.cs
git commit -m "gui(7.4.0): centralize modern fill (rounded+shadow+gradient+glow) in GuiDraw"
```

---

## Task 5: Icon atlas procedural bake

**Files:**
- Create: `KhaozEngine.Gui/Icons.cs`
- Create: `KhaozEngine.Gui/IconAtlas.cs`
- Test: `KhaozEngine.Tests/Gui/IconAtlasTests.cs` (create)

The bake produces one RGBA8 atlas: white RGB, per-icon alpha mask, laid out as a grid of `cell`-sized cells. A tiny alpha-raster toolkit (stroked line/circle, filled triangle/rect) draws each icon. Pure and headless.

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Gui/IconAtlasTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class IconAtlasTests
    {
        [Fact]
        public void Bake_HasExpectedDimensions()
        {
            var (px, w, h, uvs) = IconAtlas.BakeAtlasPixels(cell: 32);
            Assert.Equal(w * h * 4, px.Length);
            // 15 core icons packed into a grid of 32px cells; atlas is a whole number of cells.
            Assert.True(w % 32 == 0 && h % 32 == 0);
            Assert.Equal(Icons.All.Count, uvs.Count);
        }

        [Fact]
        public void Bake_EveryCoreIconHasNonTrivialAlphaCoverage()
        {
            var (px, w, h, uvs) = IconAtlas.BakeAtlasPixels(cell: 32);
            foreach (string id in Icons.All)
            {
                Vector4 uv = uvs[id];
                int x0 = (int)(uv.X * w), y0 = (int)(uv.Y * h);
                int x1 = (int)(uv.Z * w), y1 = (int)(uv.W * h);
                long opaque = 0, total = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        total++;
                        if (px[(y * w + x) * 4 + 3] > 40) opaque++;
                    }
                Assert.True(opaque > total / 100, $"icon '{id}' alpha coverage too low ({opaque}/{total})");
                Assert.True(opaque < total, $"icon '{id}' should not be fully opaque");
            }
        }

        [Fact]
        public void Bake_RgbIsWhiteEverywhere()
        {
            var (px, _, _, _) = IconAtlas.BakeAtlasPixels(cell: 16);
            for (int i = 0; i < px.Length; i += 4)
            {
                Assert.Equal(255, px[i]); Assert.Equal(255, px[i + 1]); Assert.Equal(255, px[i + 2]);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter IconAtlasTests`
Expected: FAIL — no `Icons`/`IconAtlas`.

- [ ] **Step 3: Create `Icons.cs`**

```csharp
using System.Collections.Generic;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// String ids for the engine's core UI icon set (registered into an <see cref="IconAtlas"/> by
    /// <see cref="IconAtlas.Bake"/>). Games register their own ids alongside these via
    /// <see cref="IconAtlas.Register"/>. Outline style, single-colour, tinted at draw time.
    /// </summary>
    public static class Icons
    {
        public const string Coin = "core.coin";
        public const string Heart = "core.heart";
        public const string Skull = "core.skull";
        public const string Crosshair = "core.crosshair";
        public const string Gear = "core.gear";
        public const string Play = "core.play";
        public const string Pause = "core.pause";
        public const string Close = "core.close";
        public const string Check = "core.check";
        public const string Plus = "core.plus";
        public const string Minus = "core.minus";
        public const string ChevronLeft = "core.chevron_left";
        public const string ChevronRight = "core.chevron_right";
        public const string ChevronUp = "core.chevron_up";
        public const string ChevronDown = "core.chevron_down";

        /// <summary>All core ids in atlas-cell order (row-major). Length drives the atlas grid.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Coin, Heart, Skull, Crosshair, Gear, Play, Pause, Close,
            Check, Plus, Minus, ChevronLeft, ChevronRight, ChevronUp, ChevronDown,
        };
    }
}
```

- [ ] **Step 4: Create `IconAtlas.cs` with the raster toolkit + per-icon bakers + atlas assembly**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A tintable UI icon set drawn through the shared batched-quad path. The core set is CPU-baked into one
    /// alpha-mask atlas (white RGB, per-icon alpha) following the <c>VfxTextures</c> pattern — no shipped asset,
    /// headless-testable. Games register their own icons (which may point at their own textures) into the same
    /// string-keyed registry, drawn the same way via <see cref="GuiSurface.Icon"/>.
    /// </summary>
    public sealed class IconAtlas
    {
        readonly Dictionary<string, (Texture2D Tex, Vector4 SrcUV)> _reg = new();

        /// <summary>Register (or replace) an icon id with a texture + source UV sub-rect (u0,v0,u1,v1 in 0..1).</summary>
        public void Register(string id, Texture2D tex, Vector4 srcUV) => _reg[id] = (tex, srcUV);

        /// <summary>Look up an icon's texture + source UV. Returns false when the id is unknown.</summary>
        public bool TryGet(string id, out Texture2D tex, out Vector4 srcUV)
        {
            if (_reg.TryGetValue(id, out var e)) { tex = e.Tex; srcUV = e.SrcUV; return true; }
            tex = null!; srcUV = default; return false;
        }

        /// <summary>True when <paramref name="id"/> is registered.</summary>
        public bool Has(string id) => _reg.ContainsKey(id);

        // ---- Core atlas bake -------------------------------------------------------------------------------

        /// <summary>
        /// Bake the core icon set into one RGBA8 atlas (white RGB, per-icon alpha). Returns the pixel buffer,
        /// its width/height, and each core id's source UV sub-rect. Pure / headless. <paramref name="cell"/> is the
        /// per-icon cell size in texels (clamped to at least 8).
        /// </summary>
        public static (byte[] Pixels, int Width, int Height, IReadOnlyDictionary<string, Vector4> Uvs)
            BakeAtlasPixels(int cell = 64)
        {
            cell = Math.Max(8, cell);
            int count = Icons.All.Count;
            int cols = 4;
            int rows = (count + cols - 1) / cols;
            int w = cols * cell, h = rows * cell;
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 0; }

            var uvs = new Dictionary<string, Vector4>(count);
            for (int idx = 0; idx < count; idx++)
            {
                int cx = (idx % cols) * cell, cy = (idx / cols) * cell;
                DrawIcon(Icons.All[idx], px, w, cx, cy, cell);
                uvs[Icons.All[idx]] = new Vector4((float)cx / w, (float)cy / h, (float)(cx + cell) / w, (float)(cy + cell) / h);
            }
            return (px, w, h, uvs);
        }

        /// <summary>Bake the core atlas and upload it to a sampleable texture on <paramref name="surface"/>'s device, returning a populated registry.</summary>
        public static IconAtlas Bake(Render2DSurface surface, int cell = 64)
        {
            ArgumentNullException.ThrowIfNull(surface);
            var (px, w, h, uvs) = BakeAtlasPixels(cell);
            Texture2D tex = surface.CreateTexture(px, w, h);
            return FromCore(tex, uvs);
        }

        /// <summary>Bake the core atlas and upload it on the snapshot <paramref name="context"/>'s device (for goldens).</summary>
        public static IconAtlas Bake(Render2DContext context, int cell = 64)
        {
            ArgumentNullException.ThrowIfNull(context);
            var (px, w, h, uvs) = BakeAtlasPixels(cell);
            Texture2D tex = context.CreateTexture(px, w, h);
            return FromCore(tex, uvs);
        }

        static IconAtlas FromCore(Texture2D tex, IReadOnlyDictionary<string, Vector4> uvs)
        {
            var a = new IconAtlas();
            foreach (var kv in uvs) a.Register(kv.Key, tex, kv.Value);
            return a;
        }

        // ---- Per-icon rasterisation into a cell's alpha ----------------------------------------------------

        static void DrawIcon(string id, byte[] px, int w, int cx, int cy, int n)
        {
            // Work in a normalised cell: centre (0.5,0.5), unit = n. Stroke ~ 8% of the cell.
            float s = MathF.Max(1.5f, n * 0.08f);
            float c = n * 0.5f;
            switch (id)
            {
                case Icons.Coin:
                    Ring(px, w, cx, cy, n, c, c, n * 0.36f, s);
                    Ring(px, w, cx, cy, n, c, c, n * 0.20f, s * 0.7f);
                    break;
                case Icons.Heart:
                    DiscMask(px, w, cx, cy, n, c - n * 0.16f, c - n * 0.12f, n * 0.18f);
                    DiscMask(px, w, cx, cy, n, c + n * 0.16f, c - n * 0.12f, n * 0.18f);
                    FillTri(px, w, cx, cy, n,
                        new Vector2(c - n * 0.32f, c - n * 0.04f),
                        new Vector2(c + n * 0.32f, c - n * 0.04f),
                        new Vector2(c, c + n * 0.34f));
                    break;
                case Icons.Skull:
                    DiscMask(px, w, cx, cy, n, c, c - n * 0.06f, n * 0.30f);
                    FillRect(px, w, cx, cy, n, c - n * 0.22f, c - n * 0.06f, c + n * 0.22f, c + n * 0.22f);
                    Punch(px, w, cx, cy, n, c - n * 0.12f, c - n * 0.06f, n * 0.09f);   // left eye
                    Punch(px, w, cx, cy, n, c + n * 0.12f, c - n * 0.06f, n * 0.09f);   // right eye
                    Punch(px, w, cx, cy, n, c, c + n * 0.08f, n * 0.05f);               // nose
                    break;
                case Icons.Crosshair:
                    Ring(px, w, cx, cy, n, c, c, n * 0.30f, s);
                    Line(px, w, cx, cy, n, c, c - n * 0.42f, c, c - n * 0.18f, s);
                    Line(px, w, cx, cy, n, c, c + n * 0.18f, c, c + n * 0.42f, s);
                    Line(px, w, cx, cy, n, c - n * 0.42f, c, c - n * 0.18f, c, s);
                    Line(px, w, cx, cy, n, c + n * 0.18f, c, c + n * 0.42f, c, s);
                    break;
                case Icons.Gear:
                    int teeth = 8;
                    for (int t = 0; t < teeth; t++)
                    {
                        float a = t * (MathF.PI * 2f / teeth);
                        float tx = c + MathF.Cos(a) * n * 0.40f, ty = c + MathF.Sin(a) * n * 0.40f;
                        DiscMask(px, w, cx, cy, n, tx, ty, n * 0.10f);
                    }
                    Ring(px, w, cx, cy, n, c, c, n * 0.28f, s * 1.3f);
                    Punch(px, w, cx, cy, n, c, c, n * 0.14f);
                    break;
                case Icons.Play:
                    FillTri(px, w, cx, cy, n,
                        new Vector2(c - n * 0.18f, c - n * 0.26f),
                        new Vector2(c - n * 0.18f, c + n * 0.26f),
                        new Vector2(c + n * 0.28f, c));
                    break;
                case Icons.Pause:
                    FillRect(px, w, cx, cy, n, c - n * 0.22f, c - n * 0.26f, c - n * 0.06f, c + n * 0.26f);
                    FillRect(px, w, cx, cy, n, c + n * 0.06f, c - n * 0.26f, c + n * 0.22f, c + n * 0.26f);
                    break;
                case Icons.Close:
                    Line(px, w, cx, cy, n, c - n * 0.24f, c - n * 0.24f, c + n * 0.24f, c + n * 0.24f, s);
                    Line(px, w, cx, cy, n, c + n * 0.24f, c - n * 0.24f, c - n * 0.24f, c + n * 0.24f, s);
                    break;
                case Icons.Check:
                    Line(px, w, cx, cy, n, c - n * 0.26f, c, c - n * 0.06f, c + n * 0.22f, s);
                    Line(px, w, cx, cy, n, c - n * 0.06f, c + n * 0.22f, c + n * 0.28f, c - n * 0.24f, s);
                    break;
                case Icons.Plus:
                    Line(px, w, cx, cy, n, c, c - n * 0.28f, c, c + n * 0.28f, s);
                    Line(px, w, cx, cy, n, c - n * 0.28f, c, c + n * 0.28f, c, s);
                    break;
                case Icons.Minus:
                    Line(px, w, cx, cy, n, c - n * 0.28f, c, c + n * 0.28f, c, s);
                    break;
                case Icons.ChevronLeft:
                    Line(px, w, cx, cy, n, c + n * 0.14f, c - n * 0.26f, c - n * 0.14f, c, s);
                    Line(px, w, cx, cy, n, c - n * 0.14f, c, c + n * 0.14f, c + n * 0.26f, s);
                    break;
                case Icons.ChevronRight:
                    Line(px, w, cx, cy, n, c - n * 0.14f, c - n * 0.26f, c + n * 0.14f, c, s);
                    Line(px, w, cx, cy, n, c + n * 0.14f, c, c - n * 0.14f, c + n * 0.26f, s);
                    break;
                case Icons.ChevronUp:
                    Line(px, w, cx, cy, n, c - n * 0.26f, c + n * 0.14f, c, c - n * 0.14f, s);
                    Line(px, w, cx, cy, n, c, c - n * 0.14f, c + n * 0.26f, c + n * 0.14f, s);
                    break;
                case Icons.ChevronDown:
                    Line(px, w, cx, cy, n, c - n * 0.26f, c - n * 0.14f, c, c + n * 0.14f, s);
                    Line(px, w, cx, cy, n, c, c + n * 0.14f, c + n * 0.26f, c - n * 0.14f, s);
                    break;
            }
        }

        // alpha = max(existing, value) so overlapping strokes union cleanly.
        static void Plot(byte[] px, int w, int cx, int cy, int n, int lx, int ly, float a)
        {
            if (lx < 0 || ly < 0 || lx >= n || ly >= n) return;
            int gx = cx + lx, gy = cy + ly;
            int i = (gy * w + gx) * 4 + 3;
            byte v = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            if (v > px[i]) px[i] = v;
        }

        // Hard-clear alpha to 0 (eye/nose holes), localised to a disc.
        static void Punch(byte[] px, int w, int cx, int cy, int n, float ox, float oy, float r)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - ox, dy = y + 0.5f - oy;
                    if (dx * dx + dy * dy <= r * r)
                    {
                        int gx = cx + x, gy = cy + y;
                        px[(gy * w + gx) * 4 + 3] = 0;
                    }
                }
        }

        static void DiscMask(byte[] px, int w, int cx, int cy, int n, float ox, float oy, float r)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - ox, dy = y + 0.5f - oy;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float a = Math.Clamp(r - d + 0.5f, 0f, 1f);   // 1px AA edge
                    if (a > 0f) Plot(px, w, cx, cy, n, x, y, a);
                }
        }

        static void Ring(byte[] px, int w, int cx, int cy, int n, float ox, float oy, float r, float thick)
        {
            float half = thick * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - ox, dy = y + 0.5f - oy;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float a = Math.Clamp(half - MathF.Abs(d - r) + 0.5f, 0f, 1f);
                    if (a > 0f) Plot(px, w, cx, cy, n, x, y, a);
                }
        }

        static void Line(byte[] px, int w, int cx, int cy, int n, float x0, float y0, float x1, float y1, float thick)
        {
            float half = thick * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float a = Math.Clamp(half - DistToSegment(x + 0.5f, y + 0.5f, x0, y0, x1, y1) + 0.5f, 0f, 1f);
                    if (a > 0f) Plot(px, w, cx, cy, n, x, y, a);
                }
        }

        static void FillRect(byte[] px, int w, int cx, int cy, int n, float x0, float y0, float x1, float y1)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    if (fx >= x0 && fx <= x1 && fy >= y0 && fy <= y1) Plot(px, w, cx, cy, n, x, y, 1f);
                }
        }

        static void FillTri(byte[] px, int w, int cx, int cy, int n, Vector2 a, Vector2 b, Vector2 cc)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (InTriangle(p, a, b, cc)) Plot(px, w, cx, cy, n, x, y, 1f);
                }
        }

        static float DistToSegment(float px_, float py, float x0, float y0, float x1, float y1)
        {
            float vx = x1 - x0, vy = y1 - y0;
            float wx = px_ - x0, wy = py - y0;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
            float dx = px_ - (x0 + t * vx), dy = py - (y0 + t * vy);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Sign(Vector2 p, Vector2 a, Vector2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter IconAtlasTests`
Expected: PASS. If any single icon trips the coverage assert, nudge its geometry constants (sizes are tuned for cell≥32); re-run.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Gui/Icons.cs KhaozEngine.Gui/IconAtlas.cs KhaozEngine.Tests/Gui/IconAtlasTests.cs
git commit -m "gui(7.4.0): procedural core icon atlas + string-keyed registry"
```

---

## Task 6: GuiSurface icon draw + Panel(rect, style)

**Files:**
- Modify: `KhaozEngine.Gui/GuiSurface.cs`
- Test: `KhaozEngine.Tests/Gui/IconWidgetTests.cs` (create)

- [ ] **Step 1: Write the failing headless tests**

Create `KhaozEngine.Tests/Gui/IconWidgetTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class IconWidgetTests
    {
        // Headless: null batch (nothing draws), no icon atlas needed for the no-op assertions.
        static GuiSurface NewSurface() => new GuiSurface(white: null!, style: null);

        [Fact]
        public void Icon_WithNoAtlas_IsNoOpAndDoesNotThrow()
        {
            var ui = NewSurface();
            ui.Begin(null, new Pointer());
            ui.Icon(new Rect(0, 0, 32, 32), Icons.Coin, Vector4.One);   // no atlas set -> silently nothing
        }

        [Fact]
        public void IconButton_ReturnsTrueOnTapInAndReservesRect()
        {
            var ui = NewSurface();
            var rect = new Rect(10, 10, 40, 40);
            // Press-origin inside, released this frame inside -> a tap.
            var p = new Pointer();
            p.SetForTest(position: new Vector2(20, 20), isDown: false, justReleased: true,
                pressOrigin: new Vector2(20, 20));
            ui.Begin(null, p);
            bool clicked = ui.IconButton(rect, Icons.Play, GuiStyle.Default);
            Assert.True(clicked);
            Assert.True(ui.PointerCaptured);   // rect reserved for click-through
        }

        [Fact]
        public void StatChip_ReservesItsRect()
        {
            var ui = NewSurface();
            var rect = new Rect(0, 0, 120, 36);
            var p = new Pointer();
            p.SetForTest(position: new Vector2(5, 5), isDown: true, justReleased: false,
                pressOrigin: new Vector2(5, 5));
            ui.Begin(null, p);
            ui.StatChip(rect, Icons.Coin, "Gold", "120", font: null!, GuiStyle.Default);
            Assert.True(ui.PointerCaptured);
        }
    }
}
```

NOTE: this test uses a `Pointer.SetForTest(...)` helper. Check `KhaozEngine.Windowing/Pointer.cs` for the existing test-construction API (the repo's Gui tests already build pointers headlessly — `GuiSurfaceTests.cs`/`GuiSurfaceSliderTests.cs` show the exact pattern). **Use whatever constructor/helper those tests use** rather than `SetForTest` if it differs; adjust the three tests above to match the existing pattern before running.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter IconWidgetTests`
Expected: FAIL — `Icon`/`IconButton`/`StatChip` missing (and/or pointer-helper mismatch to fix first).

- [ ] **Step 3: Add the icon atlas hook + `Icon` + `Panel(rect, style)` to `GuiSurface.cs`**

Add a field/property and methods. Property:

```csharp
        /// <summary>The icon set resolved by <see cref="Icon"/>/<see cref="IconButton"/>/<see cref="StatChip"/>; null = icons draw nothing.</summary>
        public IconAtlas? IconAtlas { get; set; }
```

Add methods:

```csharp
        /// <summary>
        /// Draw icon <paramref name="id"/> into <paramref name="rect"/>, tinted by <paramref name="tint"/>, via the
        /// shared batched-quad path. No-op when no <see cref="IconAtlas"/> is set or the id is unknown. Decoration:
        /// does not reserve a rect (compose inside a button/chip to reserve).
        /// </summary>
        public void Icon(Rect rect, string id, Vector4 tint)
        {
            if (_batch is null || IconAtlas is null) return;
            if (!IconAtlas.TryGet(id, out var tex, out var uv)) return;
            _batch.Draw(tex, new Vector4(rect.X, rect.Y, rect.Width, rect.Height), uv, (Color)tint);
        }

        /// <summary>Draw a panel honouring the full <paramref name="style"/> (rounded/shadow/gradient); reserves it for click-through.</summary>
        public void Panel(Rect rect, in GuiStyle style)
        {
            _blocked.Add(rect);
            if (_batch is null) return;
            GuiDraw.FillStyled(_batch, _white, rect, style, style.Fill, style.Border);
        }
```

- [ ] **Step 4: Add `IconButton` + `StatChip`**

```csharp
        /// <summary>
        /// An icon-only button (icon centred in a styled panel, tinted by the text colour, hover glow from the
        /// style). Returns true on a valid press-origin tap; always reserves its rect. Mirrors <see cref="Button(SpriteFont, Rect, string, GuiStyle, bool, bool)"/>.
        /// </summary>
        public bool IconButton(Rect rect, string iconId, GuiStyle style, bool enabled = true, bool selected = false)
        {
            _blocked.Add(rect);
            Pointer p = _pointer;
            bool clicked = enabled && p.IsTapIn(rect);
            bool hovering = enabled && p.IsHoveringIn(rect);
            if (hovering) _hoveredRect = rect;
            if (_batch is null) return clicked;

            bool pressing = p.IsPressingIn(rect);
            Vector4 fill = !enabled ? style.DisabledFill
                : selected ? style.SelectedFill
                : pressing ? style.Press
                : hovering ? style.Hover
                : style.Fill;
            Vector4 border = selected ? style.SelectedBorder : style.Border;
            Vector4 text = enabled ? style.Text : style.DisabledText;

            if (hovering) GuiDraw.HoverGlow(_batch, _white, rect, style);
            GuiDraw.FillStyled(_batch, _white, rect, style, fill, border);

            // Icon centred, inset to ~60% of the rect's smaller side.
            float side = System.MathF.Min(rect.Width, rect.Height) * 0.6f;
            var iconRect = new Rect(rect.X + (rect.Width - side) * 0.5f, rect.Y + (rect.Height - side) * 0.5f, side, side);
            Icon(iconRect, iconId, text);
            return clicked;
        }

        /// <summary>
        /// A non-interactive "stat chip": a styled rounded panel with an icon at the left and a label/value to its
        /// right. Reserves its rect for click-through (like <see cref="Panel(Rect, Vector4)"/>). A null
        /// <paramref name="font"/> draws panel + icon only (headless-safe).
        /// </summary>
        public void StatChip(Rect rect, string iconId, string label, string value, SpriteFont font, GuiStyle style)
        {
            _blocked.Add(rect);
            if (_batch is null) return;

            GuiDraw.FillStyled(_batch, _white, rect, style, style.Fill, style.Border);

            float pad = rect.Height * 0.18f;
            float iconSide = rect.Height - pad * 2f;
            var iconRect = new Rect(rect.X + pad, rect.Y + pad, iconSide, iconSide);
            Icon(iconRect, iconId, style.Text);

            if (font is null) return;
            float textX = iconRect.Right + pad;
            float ty = rect.Y + (rect.Height - font.LineHeight) * 0.5f;
            string text = string.IsNullOrEmpty(value) ? label : $"{label}  {value}";
            _batch.DrawString(font, text, new Vector2(textX, ty), (Color)style.Text);
        }
```

- [ ] **Step 5: Adjust the test's pointer construction to the real API, then run**

Open `KhaozEngine.Tests/Gui/GuiSurfaceTests.cs` and copy its exact `Pointer` construction pattern into `IconWidgetTests` (replace the `SetForTest` placeholder). Then run:

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter IconWidgetTests`
Expected: PASS.

- [ ] **Step 6: Run the whole Gui suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Gui"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Gui/GuiSurface.cs KhaozEngine.Tests/Gui/IconWidgetTests.cs
git commit -m "gui(7.4.0): GuiSurface Icon/IconButton/StatChip + Panel(rect,style)"
```

---

## Task 7: Modern golden + existing-golden regression check

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs`

- [ ] **Step 1: Add the `Golden2D_Modern` gated test**

Append this method to `GoldenSnapshotTests` (uses the same `Render2DSnapshot.Capture` ctx API as `Golden2D_FixedScene`):

```csharp
        [GpuFact]
        public void Golden2D_Modern()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.10f, 0.11f, 0.14f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var atlas = KhaozEngine.Gui.IconAtlas.Bake(ctx, cell: 64);
                ctx.Batch.Begin();

                // Rounded gradient panel with a soft drop shadow (modern path).
                var style = KhaozEngine.Gui.GuiStyle.Modern;
                ctx.Batch.DrawRounded(white, new System.Numerics.Vector4(50, 230, 60, 40),
                    (Color)style.ShadowColor, style.CornerRadius, softness: style.ShadowSize);
                ctx.Batch.DrawRounded(white, new System.Numerics.Vector4(40, 40, 200, 120),
                    (Color)new System.Numerics.Vector4(0.30f, 0.55f, 0.95f, 1f),
                    (Color)new System.Numerics.Vector4(0.10f, 0.20f, 0.45f, 1f),
                    style.CornerRadius, 0f, 0f);
                ctx.Batch.DrawRounded(white, new System.Numerics.Vector4(40, 40, 200, 120),
                    (Color)new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f), style.CornerRadius, 0f, 3f);

                // A few tinted icons.
                ctx.Batch.Draw(atlas_Tex(atlas, KhaozEngine.Gui.Icons.Coin), new System.Numerics.Vector4(280, 50, 48, 48),
                    atlas_Uv(atlas, KhaozEngine.Gui.Icons.Coin), new Color(0.95f, 0.8f, 0.2f, 1f));
                ctx.Batch.Draw(atlas_Tex(atlas, KhaozEngine.Gui.Icons.Heart), new System.Numerics.Vector4(340, 50, 48, 48),
                    atlas_Uv(atlas, KhaozEngine.Gui.Icons.Heart), new Color(0.9f, 0.25f, 0.3f, 1f));
                ctx.Batch.Draw(atlas_Tex(atlas, KhaozEngine.Gui.Icons.Gear), new System.Numerics.Vector4(400, 50, 48, 48),
                    atlas_Uv(atlas, KhaozEngine.Gui.Icons.Gear), new Color(0.8f, 0.85f, 0.9f, 1f));

                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d_modern", rgba, W, H);

            // local helpers to pull (tex, uv) out of the atlas registry
            static Texture2D atlas_Tex(KhaozEngine.Gui.IconAtlas a, string id) { a.TryGet(id, out var t, out _); return t; }
            static System.Numerics.Vector4 atlas_Uv(KhaozEngine.Gui.IconAtlas a, string id) { a.TryGet(id, out _, out var uv); return uv; }
        }
```

(If C# rejects the trailing local functions after the `GoldenCompare` call, hoist `atlas_Tex`/`atlas_Uv` to `static` methods on the class instead.)

- [ ] **Step 2: Bake the new golden on this machine (Metal)**

Run: `KE_UPDATE_GOLDENS=1 KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "GoldenSnapshotTests.Golden2D_Modern"`
Expected: writes `KhaozEngine.Tests/Gpu/goldens/scene2d_modern.metal.txt`. Open the committed grid is not human-readable; instead spot-check by eye is not required — the cross-backend test guards it.

- [ ] **Step 3: Verify existing goldens DID NOT MOVE (the byte-identity proof)**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "GoldenSnapshotTests"`
Expected: ALL PASS, including `Golden2D_FixedScene` and `Golden2D_Primitives` **without** re-baking — proving the widened vertex + SDF branch left the plain path visually identical. If either moved, STOP: the disabled branch is not bit-identical on this backend; investigate the shader before continuing (do not re-bless).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs KhaozEngine.Tests/Gpu/goldens/scene2d_modern.metal.txt
git commit -m "test(7.4.0): scene2d_modern golden + confirm plain-path goldens unmoved"
```

NOTE: D3D11 (Windows) and Vulkan (Linux/lavapipe) `scene2d_modern.*.txt` goldens must be baked on those backends in CI or on those machines (same `KE_UPDATE_GOLDENS=1` run). Until then, `CrossBackendGoldenTests` only has the Metal grid for this scene (it compares backends that exist), and the per-backend verify runs only where a grid exists. Flag in the PR that the other two grids need baking. Do not fabricate them.

---

## Task 8: Release ritual (7.3.0 → 7.4.0)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Run the full headless suite green first**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: ALL PASS (gated GPU tests skip without `KE_GPU_TESTS=1`).

- [ ] **Step 2: Bump the version**

Edit `Directory.Build.props`: change `<KhaozEngineVersion>7.3.0</KhaozEngineVersion>` to `7.4.0`.

- [ ] **Step 3: Add the `CHANGELOG.md` entry (newest-first, detailed)**

Add at the top of the entries:

```markdown
## 7.4.0

- **Gui modern primitives.** New opt-in `KhaozEngine.Gui` visuals, all defaulted off so existing
  screens render byte-identically: `GuiStyle.CornerRadius`, `ShadowSize`/`ShadowColor`/`ShadowOffset`,
  `FillMode` (`GuiFill.Solid`|`VerticalGradient`) with `GradientTopScale`/`GradientBottomScale`,
  and `GlowColor`/`GlowSize` hover glow. New `GuiStyle.Modern` preset wires rounded corners + soft
  shadow + gradient + glow onto the default palette. Centralized in `GuiDraw`, so all widgets
  (Panel/Button/Slider + retained widgets) inherit it. New `GuiSurface.Panel(rect, style)` overload.
- **SDF SpriteBatch path (Render2D).** The shared sprite vertex widened 32B→64B with rounded-rect SDF
  attributes; the fragment shader branches on a per-vertex mode flag (flag 0 = the prior
  `texture * vColor`, byte-identical for all existing draws; flag 1 = alpha shaped by an
  Inigo-Quilez rounded-box SDF with `fwidth` AA, used for corners/shadow/glow/border-ring). New public
  `SpriteBatch.DrawRounded(...)` and a vertical-gradient `Draw(tex, dest, top, bottom)` overload (the
  latter exploiting the already-interpolated per-vertex colour).
- **Icon system (Gui).** `IconAtlas` CPU-bakes a core outline icon set (coin, heart, skull, crosshair,
  gear, play, pause, close, check, plus, minus, chevron-l/r/u/d) into one tintable alpha-mask atlas
  (no shipped asset; `VfxTextures` pattern), exposed via a string-keyed registry. Games register their
  own icons (`IconAtlas.Register`). Draw with `GuiSurface.Icon(rect, id, tint)`. New composed widgets
  `GuiSurface.IconButton` and `GuiSurface.StatChip`.
```

- [ ] **Step 4: Add the `CHANGENOTES.md` digest line (newest-first, 1-2 sentences)**

```markdown
- 7.4.0: Gui modern primitives (rounded corners, gradients, soft shadow, hover glow) via a new SDF SpriteBatch path (plain draws byte-identical), plus a procedural tintable icon system (core set + game registry) and IconButton/StatChip widgets.
```

- [ ] **Step 5: Update the three guarded version declarations**

- `docs/CONSUMERS.md`: set "Engine current version" to `7.4.0`.
- `docs/ROADMAP.md`: set "Current released version" to `7.4.0`.
- `README.md`: bump the `<PackageReference ... Version="7.3.0" />` example to `7.4.0`.

- [ ] **Step 6: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (all three declarations match `7.4.0`).

- [ ] **Step 7: Pack to local-feed**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: produces `KhaozEngine.*.7.4.0.nupkg` (incl. `KhaozEngine.Gui.7.4.0.nupkg`, `KhaozEngine.Render2D.7.4.0.nupkg`, and the umbrella metapackages) in `local-feed/`.

- [ ] **Step 8: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "gui(7.4.0): modern UI primitives + icon system (SDF SpriteBatch path, byte-identical defaults)"
```

- [ ] **Step 9: Tag**

```bash
git tag v7.4.0
```

(Pushing `main` + the tag happens at branch-finish time per the finishing-a-development-branch flow, not here.)

---

## Self-Review (completed by plan author)

**Spec coverage:**
- Rounded rect + soft shadow + gradient → Tasks 1-2 (SDF path), Task 4 (GuiDraw wiring). ✓
- GuiStyle additive fields + Modern preset → Task 3. ✓
- Rounded Panel overload / honour CornerRadius centrally → Task 4 (GuiDraw branch) + Task 6 (`Panel(rect, style)`). ✓
- Byte-identical defaults → Task 1 disabled branch + `IsFlat` plain path (Task 3/4) + Task 7 regression check. ✓
- Icon atlas + registry + core set + game-registered → Tasks 5-6. ✓
- Icon draw via GuiSurface + StatChip/IconButton → Task 6. ✓
- Golden + existing-golden no-move → Task 7. ✓
- Release ritual (version, changelog, changenotes, doc declarations, pack, tag) → Task 8. ✓

**Type consistency:** `RoundedLocals`/`RoundedShape`/`RoundedMode` (Task 1/2) used consistently; `FillStyled`/`HoverGlow` (Task 4) consumed in Task 6; `IconAtlas.BakeAtlasPixels`/`Bake`/`TryGet`/`Register` consistent Tasks 5-7; `Icons.All` drives both bake and tests; `GuiStyle.IsFlat`/`ScaleRgb`/`Modern` consistent Tasks 3-4.

**Known follow-ups flagged in-plan:** the `Pointer` test-construction helper must be matched to the repo's real API (Task 6 Step 1/5); D3D11 + Vulkan `scene2d_modern` goldens need baking on their backends/CI (Task 7 NOTE).

**Out of scope (per spec):** rounded textured draws, Hardpoint adoption, blur-quality shadows.
