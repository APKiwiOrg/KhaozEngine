# KhaozEngine.Primitives leaf + cleanup batch (6.0.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a zero-dependency `KhaozEngine.Primitives` leaf that owns the shared `Color`, deterministic + fast RNGs, and math helpers; migrate the engine onto it (breaking the `Vector4`-as-color foot-gun); dedup image decode; fix two correctness bugs; wire Pooling into ECS; move Audio onto the shared RNG. Ship as `6.0.0`.

**Architecture:** A new packable leaf project with no engine dependencies (System.Numerics only) becomes the lowest node in the graph. Consumer projects add a `ProjectReference` to it and migrate their *public API* color surface to `Color`; internal GPU memory-layout structs (vertex formats, std140 UBOs) keep `Vector4`/floats and convert at the boundary. The remaining items (image decode, two fixes, Pooling, Audio RNG) are independent and ordered after the leaf exists.

**Tech Stack:** C# / net10.0, System.Numerics, xUnit (`KhaozEngine.Tests`), Veldrid behind `KhaozEngine.Gpu`, StbImageSharp (image decode), `dotnet pack` to `local-feed`.

---

## Critical conventions (read before any task)

- **Test command:** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. Run a single test with `--filter "FullyQualifiedName~TestClass.TestMethod"`.
- **Golden render snapshots** are gated behind an env var and are the hard gate for the Color migration: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. Rendering MUST stay byte-identical after the Color migration. If a golden diff appears, the migration changed a value somewhere; fix the code, do NOT rebless the golden.
- **The Color/Vector4 rule (applies to every migration task):**
  - PUBLIC API params, public fields, and serialized component fields that mean "an RGBA color" become `KhaozEngine.Primitives.Color`.
  - INTERNAL GPU memory-layout structs stay `Vector4`/`float` (they are buffer layout, not API): `SpriteBatch.V.Color`, `LineRenderer.LineVertex`, `FillRenderer.FillVertex`, `BillboardRenderer.BillboardVertex`, `ModelRenderer`'s UBO structs (`Dir/Color/Ambient/Params/Tint/Emissive`), `Models/MeshCorner.Color`, `Models/GltfMesh.Color`. Convert `Color -> Vector4` (via the implicit operator, which is retained) at the point data is written into these structs.
- **`Color` keeps its implicit `operator Vector4`** so the boundary conversions stay invisible and the GPU-layout structs need no change.
- Commit after each task. Conventional-commit subjects; use the area as scope until the release task, where the scope becomes the version (`release(6.0.0): ...`).
- Do NOT bump `Directory.Build.props` until the final release task. All intermediate work happens at 5.71.0 in source; the build stays green (or red only mid-migration-task, never across a commit) until release.

## File structure

Created:
- `KhaozEngine.Primitives/KhaozEngine.Primitives.csproj` — the new leaf project.
- `KhaozEngine.Primitives/Color.cs` — moved from Render2D, gains `FromHex`/`ToHex`.
- `KhaozEngine.Primitives/DeterministicRng.cs` — moved from Ecs, `StableHash` public.
- `KhaozEngine.Primitives/XorRng.cs` — struct, promoted from Particles' `Xorshift32`.
- `KhaozEngine.Primitives/MathUtil.cs` — `Clamp01`, `Lerp`, `InverseLerp`.
- `KhaozEngine.Primitives/ViewportMath.cs` — `Fit`, `Cover`.
- `KhaozEngine.Primitives/Easing.cs` — moved from Render2D.
- `KhaozEngine.Tests/Primitives/*` — tests for each leaf type.

Deleted:
- `KhaozEngine.Render2D/Color.cs`, `KhaozEngine.Render2D/Easing.cs`
- `KhaozEngine.Ecs/DeterministicRng.cs`
- `KhaozEngine.Particles/Xorshift32.cs`
- `KhaozEngine.Content/ColorHex.cs`

Modified (project refs + migration): `KhaozEngine.Gpu`, `KhaozEngine.Render2D`, `KhaozEngine.Render3D`, `KhaozEngine.Particles`, `KhaozEngine.Content`, `KhaozEngine.Ecs`, `KhaozEngine.Audio`, `KhaozEngine.Effects` (only if it references `Color`), `KhaozEngine.Foundation` (umbrella), plus `Persistence/FileSettingsStorage.cs`, `Ecs/WorldSerializer.cs`, `Ecs/EntityCommandBuffer.cs`, the 5 viewport-fit sites, and the release docs.

---

## Phase A: The Primitives leaf

### Task A1: Create the `KhaozEngine.Primitives` project

**Files:**
- Create: `KhaozEngine.Primitives/KhaozEngine.Primitives.csproj`
- Modify: `KhaozEngine.slnx`

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <Version>$(KhaozEngineVersion)</Version>
    <PackageId>KhaozEngine.Primitives</PackageId>
    <Description>Zero-dependency shared primitives for KhaozEngine: Color, deterministic and fast RNG, and pure math helpers (clamp/lerp, viewport fit/cover, easing). System.Numerics only.</Description>
  </PropertyGroup>

</Project>
```

(No `<ProjectReference>` and no `<PackageReference>`: this is the zero-dependency leaf. `TargetFramework`, `Nullable`, packaging, SourceLink all come from `Directory.Build.props`.)

- [ ] **Step 2: Add to the solution**

Run: `dotnet sln KhaozEngine.slnx add KhaozEngine.Primitives/KhaozEngine.Primitives.csproj`
Expected: "Project ... added to the solution."

- [ ] **Step 3: Reference it from the test project**

Modify `KhaozEngine.Tests/KhaozEngine.Tests.csproj`: add alongside the other engine `ProjectReference`s:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

- [ ] **Step 4: Build the empty leaf + test project**

Run: `dotnet build KhaozEngine.Primitives/KhaozEngine.Primitives.csproj`
Expected: Build succeeded (empty project, no code yet).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/KhaozEngine.Primitives.csproj KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "primitives: scaffold zero-dependency leaf package"
```

---

### Task A2: Move `Color` into Primitives and add hex interop

The migration of Render2D's own call sites off the old `Color` location happens in Phase B. This task moves the type and adds `FromHex`/`ToHex`; Render2D temporarily keeps compiling because Phase B updates its references. To avoid a red build between A2 and B, this task leaves a type-forwarding shim is NOT used (it would re-introduce the Render2D namespace). Instead, Render2D is migrated in the very next phase; if executing inline, do A2 then B1 back-to-back. (Subagent-driven: A2's build step only builds the Primitives project, not the solution.)

**Files:**
- Create: `KhaozEngine.Primitives/Color.cs`
- Create: `KhaozEngine.Tests/Primitives/ColorTests.cs`
- Delete (in Phase B1): `KhaozEngine.Render2D/Color.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Primitives/ColorTests.cs`:

```csharp
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class ColorTests
{
    [Fact]
    public void FromBytes_NormalizesChannels()
    {
        var c = Color.FromBytes(255, 128, 0, 255);
        Assert.Equal(1f, c.R, 3);
        Assert.Equal(128f / 255f, c.G, 5);
        Assert.Equal(0f, c.B, 3);
        Assert.Equal(1f, c.A, 3);
    }

    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("FF8000", 255, 128, 0, 255)]
    [InlineData("#FF800080", 255, 128, 0, 128)]
    public void FromHex_ParsesRgbAndRgba(string hex, int r, int g, int b, int a)
    {
        var c = Color.FromHex(hex);
        Assert.Equal(Color.FromBytes((byte)r, (byte)g, (byte)b, (byte)a), c);
    }

    [Fact]
    public void ToHex_RoundTrips()
    {
        var c = Color.FromBytes(18, 52, 86, 171);
        Assert.Equal(c, Color.FromHex(Color.ToHex(c)));
    }

    [Fact]
    public void ToHex_FormatsRrggbbaaUpper()
    {
        Assert.Equal("#FF800080", Color.ToHex(Color.FromBytes(255, 128, 0, 128)));
    }

    [Fact]
    public void WithAlpha_ReplacesOnlyAlpha()
    {
        var c = new Color(0.2f, 0.4f, 0.6f, 1f).WithAlpha(0.5f);
        Assert.Equal(new Color(0.2f, 0.4f, 0.6f, 0.5f), c);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.ColorTests"`
Expected: compile failure (`KhaozEngine.Primitives.Color` does not exist).

- [ ] **Step 3: Create `Color.cs` in Primitives**

`KhaozEngine.Primitives/Color.cs` (the existing Render2D struct verbatim, namespace changed to `KhaozEngine.Primitives`, plus `FromHex`/`ToHex` absorbed from `Content/ColorHex`):

```csharp
using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Primitives
{
    /// <summary>
    /// An RGBA color with float channels in 0..1. A typed wrapper over <see cref="Vector4"/> so call sites
    /// stop passing a bare <c>Vector4</c> for both a destination rect and a color (a swappable foot-gun).
    /// Converts implicitly to <see cref="Vector4"/> so it drops straight into GPU layout structs; the reverse
    /// is explicit because not every <c>Vector4</c> is a color.
    /// </summary>
    public readonly struct Color : IEquatable<Color>
    {
        public readonly float R, G, B, A;

        public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

        /// <summary>From 0..255 byte channels (alpha defaults to opaque).</summary>
        public static Color FromBytes(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

        public Vector4 ToVector4() => new(R, G, B, A);
        public static Color FromVector4(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

        public static implicit operator Vector4(Color c) => c.ToVector4();
        public static explicit operator Color(Vector4 v) => FromVector4(v);

        /// <summary>The same color with a replaced alpha.</summary>
        public Color WithAlpha(float a) => new(R, G, B, a);

        /// <summary>Parse <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (leading '#' optional). Missing alpha is opaque.</summary>
        public static Color FromHex(string hex)
        {
            if (hex is null) throw new ArgumentNullException(nameof(hex));
            ReadOnlySpan<char> s = hex.AsSpan().Trim();
            if (s.Length > 0 && s[0] == '#') s = s[1..];
            if (s.Length != 6 && s.Length != 8)
                throw new FormatException($"Hex colour must be RRGGBB or RRGGBBAA, got '{hex}'.");
            byte r = ByteAt(s, 0), g = ByteAt(s, 2), b = ByteAt(s, 4);
            byte a = s.Length == 8 ? ByteAt(s, 6) : (byte)255;
            return FromBytes(r, g, b, a);
        }

        /// <summary>Format as <c>#RRGGBBAA</c> (channels clamped).</summary>
        public static string ToHex(Color c)
        {
            static int Ch(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
            return $"#{Ch(c.R):X2}{Ch(c.G):X2}{Ch(c.B):X2}{Ch(c.A):X2}";
        }

        static byte ByteAt(ReadOnlySpan<char> s, int start) =>
            byte.Parse(s.Slice(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public static readonly Color White = new(1f, 1f, 1f, 1f);
        public static readonly Color Black = new(0f, 0f, 0f, 1f);
        public static readonly Color Transparent = new(0f, 0f, 0f, 0f);

        public bool Equals(Color o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object? o) => o is Color c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public static bool operator ==(Color a, Color b) => a.Equals(b);
        public static bool operator !=(Color a, Color b) => !a.Equals(b);
        public override string ToString() => $"Color({R}, {G}, {B}, {A})";
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.ColorTests"`
Expected: PASS (5 tests / theory cases green).

Note: the solution build is red now because two `Color` types would exist once Render2D still has its own. That is resolved in B1 (delete `Render2D/Color.cs`). Do not run a full solution build between here and B1.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/Color.cs KhaozEngine.Tests/Primitives/ColorTests.cs
git commit -m "primitives: add Color with FromHex/ToHex"
```

---

### Task A3: Move `DeterministicRng` into Primitives, make `StableHash` public

**Files:**
- Create: `KhaozEngine.Primitives/DeterministicRng.cs`
- Delete: `KhaozEngine.Ecs/DeterministicRng.cs`
- Create: `KhaozEngine.Tests/Primitives/DeterministicRngTests.cs`
- Modify: `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj` (add Primitives ref)

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Primitives/DeterministicRngTests.cs`:

```csharp
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class DeterministicRngTests
{
    [Fact]
    public void SameSeed_SameStream()
    {
        var a = new DeterministicRng(1234);
        var b = new DeterministicRng(1234);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void State_SaveRestore_ReproducesStream()
    {
        var rng = new DeterministicRng(99);
        rng.NextULong(); rng.NextULong();
        var saved = rng.State;
        ulong[] expected = { rng.NextULong(), rng.NextULong(), rng.NextULong() };
        rng.State = saved;
        Assert.Equal(expected, new[] { rng.NextULong(), rng.NextULong(), rng.NextULong() });
    }

    [Fact]
    public void NextFloat_InUnitInterval()
    {
        var rng = new DeterministicRng(7);
        for (int i = 0; i < 1000; i++)
        {
            float f = rng.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void StableHash_IsPublicAndDeterministic()
    {
        // DJB2-xor of "combat": stable across runs/platforms.
        Assert.Equal(DeterministicRng.StableHash("combat"), DeterministicRng.StableHash("combat"));
        Assert.NotEqual(DeterministicRng.StableHash("combat"), DeterministicRng.StableHash("oreField"));
    }

    [Fact]
    public void CreateDerived_IsIndependentOfDrawState()
    {
        var parent = new DeterministicRng(42);
        var d1 = parent.CreateDerived("combat");
        parent.NextULong(); parent.NextULong();
        var d2 = parent.CreateDerived("combat");
        Assert.Equal(d1.NextULong(), d2.NextULong());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.DeterministicRngTests"`
Expected: compile failure (`KhaozEngine.Primitives.DeterministicRng` does not exist; `StableHash` not public).

- [ ] **Step 3: Create the moved type**

Create `KhaozEngine.Primitives/DeterministicRng.cs` as the current `KhaozEngine.Ecs/DeterministicRng.cs` content with two changes: `namespace KhaozEngine.Primitives;` and `StableHash` changed from `private static` to `public static`. Then delete the Ecs file:

```bash
git rm KhaozEngine.Ecs/DeterministicRng.cs
```

(Keep the body byte-for-byte identical otherwise: same xorshift128+/splitmix, same `State`, `NextULong`, `NextUInt`, `NextDouble`, `NextFloat`, `Next(int)`, `Next(int,int)`, `CreateDerived`. The `StableHash` doc comment stays; just widen visibility.)

- [ ] **Step 4: Add the Primitives reference to Ecs**

Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, add to the existing `ItemGroup` of project references:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

Then update Ecs files that referenced the old `DeterministicRng`. Per the audit grep, only `DeterministicRng.cs` itself referenced the type within Ecs source, so add `using KhaozEngine.Primitives;` to any Ecs file that uses it after the move (search: `grep -rln DeterministicRng KhaozEngine.Ecs`). If none remain, no using is needed.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.DeterministicRngTests"`
Expected: PASS. Also run the existing ECS determinism tests: `--filter "FullyQualifiedName~Ecs"` — all green.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Primitives/DeterministicRng.cs KhaozEngine.Ecs/ KhaozEngine.Tests/Primitives/DeterministicRngTests.cs
git commit -m "primitives: move DeterministicRng from Ecs, make StableHash public"
```

---

### Task A4: Add the `XorRng` struct

**Files:**
- Create: `KhaozEngine.Primitives/XorRng.cs`
- Create: `KhaozEngine.Tests/Primitives/XorRngTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Primitives/XorRngTests.cs`:

```csharp
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class XorRngTests
{
    [Fact]
    public void SameSeed_SameStream()
    {
        var a = new XorRng(5);
        var b = new XorRng(5);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void ZeroSeed_DoesNotCollapse()
    {
        var rng = new XorRng(0);
        Assert.NotEqual(0u, rng.NextUInt());
    }

    [Fact]
    public void Copy_IsSnapshot()
    {
        var rng = new XorRng(9);
        rng.NextUInt();
        var snapshot = rng;            // struct copy
        Assert.Equal(snapshot.NextUInt(), rng.NextUInt());
    }

    [Fact]
    public void Range_RespectsBounds()
    {
        var rng = new XorRng(3);
        for (int i = 0; i < 1000; i++) Assert.InRange(rng.Range(2f, 5f), 2f, 5f);
        Assert.Equal(4f, new XorRng(3).Range(4f, 4f));   // degenerate range returns min
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.XorRngTests"`
Expected: compile failure (`XorRng` does not exist).

- [ ] **Step 3: Create `XorRng.cs`**

`KhaozEngine.Primitives/XorRng.cs` (the current `Particles/Xorshift32` promoted to public, renamed):

```csharp
namespace KhaozEngine.Primitives;

/// <summary>
/// Tiny deterministic xorshift32 PRNG as a value type, for allocation-free hot paths (particle emission,
/// audio noise). Two instances with the same seed and call sequence produce identical streams. Copy the
/// struct to snapshot. No System.Random, no wall-clock. For resumable/derivable streams use
/// <see cref="DeterministicRng"/> instead.
/// </summary>
public struct XorRng
{
    private uint _state;

    public XorRng(uint seed)
    {
        // xorshift collapses to 0 forever if seeded with 0; map it to a non-zero constant.
        _state = seed != 0u ? seed : 0x9E3779B9u;
    }

    /// <summary>Next raw 32-bit value.</summary>
    public uint NextUInt()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Float in [0, 1). Uses the top 24 bits for a clean mantissa.</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    /// <summary>Float uniformly in [min, max). Degenerate (max &lt;= min) returns min.</summary>
    public float Range(float min, float max) => max <= min ? min : min + (max - min) * NextFloat();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.XorRngTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/XorRng.cs KhaozEngine.Tests/Primitives/XorRngTests.cs
git commit -m "primitives: add XorRng value-type PRNG"
```

---

### Task A5: Add `MathUtil`

**Files:**
- Create: `KhaozEngine.Primitives/MathUtil.cs`
- Create: `KhaozEngine.Tests/Primitives/MathUtilTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Primitives/MathUtilTests.cs`:

```csharp
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class MathUtilTests
{
    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(2f, 1f)]
    public void Clamp01(float input, float expected) => Assert.Equal(expected, MathUtil.Clamp01(input));

    [Fact]
    public void Lerp_Interpolates() => Assert.Equal(7.5f, MathUtil.Lerp(5f, 10f, 0.5f));

    [Fact]
    public void InverseLerp_Inverts() => Assert.Equal(0.5f, MathUtil.InverseLerp(5f, 10f, 7.5f));

    [Fact]
    public void InverseLerp_DegenerateReturnsZero() => Assert.Equal(0f, MathUtil.InverseLerp(5f, 5f, 7f));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.MathUtilTests"`
Expected: compile failure (`MathUtil` does not exist).

- [ ] **Step 3: Create `MathUtil.cs`**

```csharp
namespace KhaozEngine.Primitives;

/// <summary>Pure scalar helpers shared across the engine. No allocation, no state.</summary>
public static class MathUtil
{
    /// <summary>Clamp to [0, 1].</summary>
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Linear interpolation; <paramref name="t"/> is not clamped.</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Inverse of <see cref="Lerp"/>: where does <paramref name="v"/> sit in [a, b]? Degenerate (a == b) returns 0.</summary>
    public static float InverseLerp(float a, float b, float v) => a == b ? 0f : (v - a) / (b - a);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.MathUtilTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/MathUtil.cs KhaozEngine.Tests/Primitives/MathUtilTests.cs
git commit -m "primitives: add MathUtil (clamp01/lerp/inverseLerp)"
```

---

### Task A6: Add `ViewportMath`

**Files:**
- Create: `KhaozEngine.Primitives/ViewportMath.cs`
- Create: `KhaozEngine.Tests/Primitives/ViewportMathTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Primitives/ViewportMathTests.cs`:

```csharp
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class ViewportMathTests
{
    [Fact]
    public void Fit_WiderSource_LimitedByWidth()
    {
        // src 200x100 into dst 100x100: scale = min(100/200, 100/100) = 0.5
        Assert.Equal(0.5f, ViewportMath.Fit(200, 100, 100, 100));
    }

    [Fact]
    public void Fit_TallerSource_LimitedByHeight()
    {
        // src 100x200 into 100x100: scale = min(1, 0.5) = 0.5
        Assert.Equal(0.5f, ViewportMath.Fit(100, 200, 100, 100));
    }

    [Fact]
    public void Cover_UsesMaxRatio()
    {
        // src 200x100 cover 100x100: scale = max(0.5, 1) = 1
        Assert.Equal(1f, ViewportMath.Cover(200, 100, 100, 100));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.ViewportMathTests"`
Expected: compile failure.

- [ ] **Step 3: Create `ViewportMath.cs`**

```csharp
using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// Uniform-scale helpers for fitting one rectangle inside another while preserving aspect ratio. Replaces
/// the open-coded <c>MathF.Min(w/W, h/H)</c> formula that was duplicated across windowing/camera/scene code.
/// </summary>
public static class ViewportMath
{
    /// <summary>Largest uniform scale that fits src entirely inside dst (letterbox). Aspect preserved.</summary>
    public static float Fit(float srcWidth, float srcHeight, float dstWidth, float dstHeight)
        => MathF.Min(dstWidth / srcWidth, dstHeight / srcHeight);

    /// <summary>Smallest uniform scale that covers dst entirely with src (crop). Aspect preserved.</summary>
    public static float Cover(float srcWidth, float srcHeight, float dstWidth, float dstHeight)
        => MathF.Max(dstWidth / srcWidth, dstHeight / srcHeight);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.ViewportMathTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/ViewportMath.cs KhaozEngine.Tests/Primitives/ViewportMathTests.cs
git commit -m "primitives: add ViewportMath (fit/cover)"
```

---

### Task A7: Move `Easing` into Primitives

`Render2D` call sites move in Phase B7. As with A2/B1, do not run a full solution build between this task and B7; build only the Primitives project here.

**Files:**
- Create: `KhaozEngine.Primitives/Easing.cs`
- Delete (in B7): `KhaozEngine.Render2D/Easing.cs`
- Create: `KhaozEngine.Tests/Primitives/EasingTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Primitives/EasingTests.cs`:

```csharp
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class EasingTests
{
    [Fact]
    public void Endpoints_AreFixed()
    {
        Assert.Equal(0f, Easing.SmoothStep(0f));
        Assert.Equal(1f, Easing.SmoothStep(1f));
        Assert.Equal(0f, Easing.EaseInOut(0f));
        Assert.Equal(1f, Easing.EaseInOut(1f));
    }

    [Fact]
    public void Clamps_OutOfRangeInput()
    {
        Assert.Equal(0f, Easing.SmoothStep(-1f));
        Assert.Equal(1f, Easing.EaseIn(2f));
    }

    [Fact]
    public void SmoothStep_MidpointIsHalf() => Assert.Equal(0.5f, Easing.SmoothStep(0.5f), 5);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.EasingTests"`
Expected: compile failure (`KhaozEngine.Primitives.Easing` does not exist).

- [ ] **Step 3: Create `Easing.cs` in Primitives**

Move the current `Render2D/Easing.cs` content with `namespace KhaozEngine.Primitives;` and have its private `Clamp01` delegate to `MathUtil`:

```csharp
namespace KhaozEngine.Primitives
{
    /// <summary>
    /// Pure easing curves for time-based transitions. Each reshapes a progress value <c>t</c> (clamped to
    /// <c>[0,1]</c>) and returns the eased value in <c>[0,1]</c>, with <c>f(0)=0</c> and <c>f(1)=1</c>.
    /// </summary>
    public static class Easing
    {
        public static float Linear(float t) => MathUtil.Clamp01(t);
        public static float SmoothStep(float t) { t = MathUtil.Clamp01(t); return t * t * (3f - 2f * t); }
        public static float EaseIn(float t) { t = MathUtil.Clamp01(t); return t * t; }
        public static float EaseOut(float t) { t = MathUtil.Clamp01(t); return t * (2f - t); }
        public static float EaseInOut(float t)
        {
            t = MathUtil.Clamp01(t);
            return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Primitives.EasingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/Easing.cs KhaozEngine.Tests/Primitives/EasingTests.cs
git commit -m "primitives: move Easing from Render2D"
```

---

## Phase B: Migrate consumers onto Primitives (breaking)

Each task in this phase: add the `ProjectReference`, apply the Color/Vector4 rule from the conventions section, build that project, then at the end of the phase run the full golden gate. The implicit `Color -> Vector4` operator means GPU-layout structs are untouched; only public API surface changes.

### Task B1: Render2D onto Primitives.Color

**Files:**
- Delete: `KhaozEngine.Render2D/Color.cs`
- Modify: `KhaozEngine.Render2D/KhaozEngine.Render2D.csproj` (+Primitives ref)
- Modify: `KhaozEngine.Render2D/SpriteBatch.cs`, `PrimitiveRenderer.cs`, `TextHelper.cs`, `TextLayout.cs`, `Render2DSnapshot.cs`, `Render2DSurface.cs`, `Internal/Render2DCore.cs`, and any file with `using KhaozEngine.Render2D` that referenced `Color`.

- [ ] **Step 1: Add the reference and delete the old type**

In `KhaozEngine.Render2D/KhaozEngine.Render2D.csproj` add:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

Then `git rm KhaozEngine.Render2D/Color.cs`.

- [ ] **Step 2: Add the using and migrate public color params**

Add `using KhaozEngine.Primitives;` to every Render2D file that used the local `Color` or has a public color parameter. Apply the rule:
- PUBLIC API color params/overloads become `Color`. Concretely:
  - `TextHelper.cs:47-97` — change every `Vector4 color` param to `Color color`; the internal `Fade` helper stays `Vector4` (or becomes `Color` with `WithAlpha`). These already forward into `SpriteBatch.DrawString`.
  - `PrimitiveRenderer.cs:58,62,75,85,114,130` — `Vector4 color` -> `Color color`. The gradient `DrawFilledRectVerticalGradient` (`:151`, `Vector4.Lerp(top, bottom, t)`) keeps `Vector4` internally; its public `top`/`bottom` params become `Color` and convert at the lerp (`Vector4.Lerp((Vector4)top, (Vector4)bottom, t)`).
  - `SpriteBatch.cs` — the `Vector4 color` overloads (`:200,203,207,232,268,282`) become the secondary path; the existing `Color` overloads (`:216,219,222,279`) become primary. Decide one canonical signature per draw shape taking `Color`; keep a `Vector4` overload only where a non-color Vector4 is genuinely meant. The vertex struct `V.Color` (`:32`) STAYS `Vector4` (GPU layout) — `EmitQuad` converts.
  - `TextLayout.cs:68,77` — `Vector4 color` -> `Color color`.
  - Snapshot/Surface/Core `Vector4 clear` params (`Render2DSnapshot.cs:25,60`, `Render2DSurface.cs:54,63`, `Render2DCore.cs:49,91`) -> `Color clear`; the `cl.ClearColorTarget(0, clear)` calls rely on `ClearColorTarget` taking `Color` after Task B2 (or pass `(Vector4)clear` until then — see step ordering note).

Step-ordering note: B2 changes `ClearColorTarget` to take `Color`. To keep each task's project building, do B2 (Gpu) before the snapshot/core `clear` param change here, OR pass `(Vector4)clear` in B1 and drop the cast in B2. The plan orders B2 after B1; therefore in B1 the clear calls pass `clear` (a `Color`) which still compiles via the implicit `Color->Vector4` while `ClearColorTarget` is `Vector4`. No cast needed.

- [ ] **Step 3: Build Render2D**

Run: `dotnet build KhaozEngine.Render2D/KhaozEngine.Render2D.csproj`
Expected: Build succeeded. Fix any consumer-within-Render2D call sites the compiler flags until green.

- [ ] **Step 4: Run Render2D-facing tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2D"`
Expected: PASS (non-golden tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/ KhaozEngine.Tests/
git commit -m "render2d: migrate public color API to Primitives.Color (breaking)"
```

---

### Task B2: Gpu `ClearColorTarget(uint, Color)`

**Files:**
- Modify: `KhaozEngine.Gpu/KhaozEngine.Gpu.csproj` (+Primitives ref)
- Modify: `KhaozEngine.Gpu/GpuInterfaces.cs:109`, `KhaozEngine.Gpu/Internal/VeldridGpuCommandList.cs:20-21`

- [ ] **Step 1: Add the reference**

`KhaozEngine.Gpu/KhaozEngine.Gpu.csproj`:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

- [ ] **Step 2: Change the interface and impl**

`GpuInterfaces.cs:109`:

```csharp
void ClearColorTarget(uint index, Color rgba);
```

`Internal/VeldridGpuCommandList.cs:20-21` (add `using KhaozEngine.Primitives;`):

```csharp
public void ClearColorTarget(uint index, Color rgba)
    => CommandList.ClearColorTarget(index, new RgbaFloat(rgba.R, rgba.G, rgba.B, rgba.A));
```

- [ ] **Step 3: Fix the now-`Vector4` callers**

`AppWindow.cs:193` (`ClearColor` field), `Render2DCore.cs:68,108`, `Render2DSnapshot.cs:44`, `ModelRenderer.cs:124-126`, `PixelPostProcess.cs:162` pass a `Color` or convert. Render2D's `clear` params are already `Color` (B1). For `AppWindow.ClearColor`, change its type to `Color`. Render3D's clear args (`bg`, `Vector4.Zero`) are handled in B3; until then pass `(Color)bg` is wrong (bg is Vector4) — order B2 before B3 and in B2 temporarily wrap Render3D call sites with `Color.FromVector4(bg)`; B3 then makes `bg` a `Color`.

- [ ] **Step 4: Build Gpu + Windowing**

Run: `dotnet build KhaozEngine.Gpu/KhaozEngine.Gpu.csproj && dotnet build KhaozEngine.Windowing/KhaozEngine.Windowing.csproj`
Expected: Build succeeded. (Windowing needs `using KhaozEngine.Primitives;` and a Primitives ref if `ClearColor` becomes `Color` — add the ref to `KhaozEngine.Windowing.csproj`.)

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gpu/ KhaozEngine.Windowing/
git commit -m "gpu: ClearColorTarget takes Primitives.Color (breaking)"
```

---

### Task B3: Render3D public color API onto Color

**Files:**
- Modify: `KhaozEngine.Render3D/KhaozEngine.Render3D.csproj` (+Primitives ref)
- Modify: `Palette.cs`, `Material.cs`, `Scene3D.cs` (Debug*/Draw/Billboard color params + tint), `SceneInstances.cs`, `MeshBuilder.cs`, `Ecs/MeshInstance.cs`, `Ecs/Scene3DBinder.cs`, `Rendering/ModelRenderer.cs` (clear args only), `Rendering/PixelPostProcess.cs:162`.

Apply the rule. PUBLIC API color/tint/emissive surface -> `Color`. INTERNAL GPU structs stay `Vector4`:
- `Palette.Colors` (`Palette.cs:9-10`) -> `Color[]`.
- `Material.Emissive`/ctor/`Glowing` (`Material.cs:14,22,34`) -> `Color`.
- `Scene3D` `tint`/`color` public params (`:166,170,176,184,192,201,220,247,257,263,270,293`) -> `Color`. `AppendScratch`/`AppendFillScratch` (`:229,277`) are internal and feed vertex data — keep `Vector4` and convert at the call boundary.
- `SceneInstances.Add`/`Instance.Tint` (`:17,18,24,26,27`) — `Tint` is internal instance data fed to the instance buffer; keep `Vector4`, accept `Color` in the public `Add` and convert.
- `MeshBuilder.Add` (`:29`) public `color` -> `Color` (internal `AddInternal` `Vector4?` stays).
- `Ecs/MeshInstance.Tint` (`:16`) is a serialized ECS component -> `Color` (save format change, acceptable under major). `Scene3DBinder.cs:30,47` `m.Tint == Vector4.Zero ? Vector4.One` becomes `m.Tint == Color.Transparent ? Color.White` (preserve the "unset = white" intent; confirm default(Color) is Transparent which it is: all-zero).
- `ModelRenderer` UBO structs (`:19,28,29`) and `Rendering/*Vertex` structs STAY `Vector4`. The clear calls `ModelRenderer.cs:124-126` and `PixelPostProcess.cs:162` pass `Color` (drop the temporary `Color.FromVector4` wrapper from B2: `bg` should now be a `Color`, `Vector4.Zero` becomes `Color.Transparent`, `new Vector4(0,0,0,1)` becomes `Color.Black`).

- [ ] **Step 1: Add the reference**

`KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

- [ ] **Step 2: Apply the migration** per the file list and rule above; add `using KhaozEngine.Primitives;` where needed.

- [ ] **Step 3: Build Render3D**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Run Render3D tests (non-golden)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render3D"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/
git commit -m "render3d: migrate public color API to Primitives.Color (breaking)"
```

---

### Task B4: Particles onto Color + XorRng + MathUtil.Lerp

**Files:**
- Modify: `KhaozEngine.Particles/KhaozEngine.Particles.csproj` (+Primitives ref)
- Modify: `Particle.cs:27` (`Color`), `EmitterConfig.cs` (Start/EndColor), `ParticleSystem.cs:24,41,201`
- Delete: `KhaozEngine.Particles/Xorshift32.cs`

- [ ] **Step 1: Add reference, swap RNG, drop local Lerp**

`KhaozEngine.Particles/KhaozEngine.Particles.csproj`:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

In `ParticleSystem.cs`: `using KhaozEngine.Primitives;`, change `private Xorshift32 _rng;` -> `private XorRng _rng;` (`:24`), `_rng = new Xorshift32(seed);` -> `_rng = new XorRng(seed);` (`:41`), and delete the private `Lerp` (`:201`), replacing call sites with `MathUtil.Lerp`. Then `git rm KhaozEngine.Particles/Xorshift32.cs`.

- [ ] **Step 2: Migrate Particle/EmitterConfig color fields**

`Particle.cs:27` `public Vector4 Color;` -> `public Color Color;` (it is interpolated each frame via the now-`MathUtil.Lerp`; if color lerp uses `Vector4.Lerp`, keep a `Vector4` local in the update loop and assign back, OR add component-wise lerp — keep the existing `Vector4.Lerp` by converting: `(Color)Vector4.Lerp(start, end, t)`). Migrate `EmitterConfig.Start/EndColor` to `Color`.

- [ ] **Step 3: Build + test**

Run: `dotnet build KhaozEngine.Particles/KhaozEngine.Particles.csproj && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Particle"`
Expected: Build succeeded; particle determinism tests PASS (XorRng is bit-identical to the old Xorshift32, so determinism golden values are unchanged).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Particles/
git commit -m "particles: use Primitives Color/XorRng/MathUtil; drop local copies (breaking)"
```

---

### Task B5: Content onto Color, delete ColorHex

**Files:**
- Modify: `KhaozEngine.Content/KhaozEngine.Content.csproj` (+Primitives ref)
- Delete: `KhaozEngine.Content/ColorHex.cs`
- Modify: the color JSON converter that used `ColorHex` (find: `grep -rln ColorHex KhaozEngine.Content KhaozEngine.Serialization`).

- [ ] **Step 1: Add reference, delete ColorHex**

`KhaozEngine.Content/KhaozEngine.Content.csproj`:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

`git rm KhaozEngine.Content/ColorHex.cs`.

- [ ] **Step 2: Repoint the JSON converter**

Any `ColorHex.FromHex(s)` -> `Color.FromHex(s)` and `ColorHex.ToHex(v)` -> `Color.ToHex(c)`. The converter's read/write type changes from `Vector4` to `Color`. If the converter lives in `Serialization`, add the Primitives ref there too (Serialization is a leaf-level package; adding a zero-dep Primitives ref creates no cycle).

- [ ] **Step 3: Build + test**

Run: `dotnet build KhaozEngine.Content/KhaozEngine.Content.csproj && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Content"`
Expected: Build succeeded; tests PASS. If a color round-trip test asserted `Vector4`, update it to `Color`.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Content/ KhaozEngine.Serialization/ KhaozEngine.Tests/
git commit -m "content: hex color via Primitives.Color, delete ColorHex (breaking)"
```

---

### Task B6: Replace the 5 viewport-fit sites with `ViewportMath`

**Files:**
- Modify: `KhaozEngine.Windowing/AppWindow.cs:131`, `KhaozEngine.Render2D/Camera2D.cs:77`, `KhaozEngine.Render2D/CameraFraming.cs:57`, `KhaozEngine.Render3D/Scene3D.cs:335`, `KhaozEngine.Render3D/Camera/IsoCamera3D.cs:82`

Each already has a Primitives ref after B1/B2/B3 except confirm Windowing (added in B2). Replace the inline `MathF.Min(dst/src, ...)` with `ViewportMath.Fit(...)` (or `Cover` for `IsoCamera3D`'s aspect-cover variant — read that site and pick `Fit`/`Cover` to match existing behavior exactly).

- [ ] **Step 1: Read each site and confirm fit-vs-cover**

Run: `grep -n "MathF.Min\|MathF.Max" KhaozEngine.Windowing/AppWindow.cs KhaozEngine.Render2D/Camera2D.cs KhaozEngine.Render2D/CameraFraming.cs KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Render3D/Camera/IsoCamera3D.cs`
For each, confirm the existing arg order so the replacement is behavior-identical.

- [ ] **Step 2: Replace each with the matching `ViewportMath` call**, preserving exact argument order (`Fit(srcW, srcH, dstW, dstH)`).

- [ ] **Step 3: Build the three projects + run camera tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Camera"`
Expected: PASS (math identical, no behavior change).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Windowing/ KhaozEngine.Render2D/ KhaozEngine.Render3D/
git commit -m "render: route viewport fit math through ViewportMath"
```

---

### Task B7: Repoint Easing call sites + Effects check + full golden gate

**Files:**
- Delete: `KhaozEngine.Render2D/Easing.cs`
- Modify: every `Render2D` file using `Easing` (find: `grep -rln "Easing\." KhaozEngine.Render2D`) — add `using KhaozEngine.Primitives;`.
- Modify: `KhaozEngine.Effects/KhaozEngine.Effects.csproj` ONLY if Effects references `Color`/`Easing` (find: `grep -rlnE "\bColor\b|Easing" KhaozEngine.Effects`).

- [ ] **Step 1: Delete the old Easing and repoint**

`git rm KhaozEngine.Render2D/Easing.cs`. Add `using KhaozEngine.Primitives;` to the Render2D files that call `Easing.*` (e.g. `CameraBlend`).

- [ ] **Step 2: Effects dependency check**

Run: `grep -rlnE "\bColor\b|Easing\.|Xorshift|DeterministicRng" KhaozEngine.Effects --include=*.cs`
If any hit, add the Primitives `ProjectReference` to `KhaozEngine.Effects.csproj` and `using KhaozEngine.Primitives;`. If no hit, Effects needs no change (record that in the commit message).

- [ ] **Step 3: Full solution build**

Run: `dotnet build KhaozEngine.slnx`
Expected: Build succeeded across all projects. This is the first full-solution green since B1.

- [ ] **Step 4: THE GOLDEN GATE — byte-identical rendering**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: ALL green, including every golden snapshot. If any golden diffs, a value changed in migration (likely an alpha default or a `Color.Transparent` vs `Vector4.Zero` mismatch); fix the code so rendering matches the committed goldens. Do NOT rebless.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/ KhaozEngine.Effects/
git commit -m "render: move Easing to Primitives; complete Color migration; goldens green"
```

---

## Phase C: Image-decode dedup

### Task C1: Route Render2DCore and Scene3D through `ImageRgba.Decode`

**Files:**
- Modify: `KhaozEngine.Render2D/ImageRgba.cs` (ensure a single `Decode(byte[])` + texture-create helper), `KhaozEngine.Render2D/Internal/Render2DCore.cs:23-33`, `KhaozEngine.Render3D/Scene3D.cs:118,125-132`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Render2D/ImageDecodeParityTests.cs`:

```csharp
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D;

public class ImageDecodeParityTests
{
    [Fact]
    public void Decode_ProducesRgba_ForKnownPng()
    {
        // Use an existing test asset already used by the render tests; assert dimensions + that
        // decode yields width*height*4 bytes (RGBA), proving the single helper round-trips.
        string path = TestAssets.SmallPngPath;   // existing helper/const in the test project
        var img = ImageRgba.Load(path);
        Assert.Equal(img.Width * img.Height * 4, img.Pixels.Length);
    }
}
```

(If `TestAssets.SmallPngPath` does not exist, use the literal path of an existing PNG the render tests already load; grep `KhaozEngine.Tests` for `.png`.)

- [ ] **Step 2: Run it to verify it fails or passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ImageDecodeParity"`
Expected: PASS if `ImageRgba.Load` already exposes `Width/Height/Pixels`; if not, FAIL — add the minimal accessor to `ImageRgba` and re-run to green.

- [ ] **Step 3: Repoint Render2DCore and Scene3D**

In `Render2DCore.cs:33` replace the direct `ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha)` with `ImageRgba.Decode(File.ReadAllBytes(path))` (or `ImageRgba.Load(path)`), and route the texture-create block (`:23-29`) through a shared helper on `ImageRgba` (e.g. `ImageRgba.CreateTexture(IGpuDevice, ReadOnlySpan<byte>, int, int)`). Do the same in `Scene3D.cs:118,125-132`. Render3D already references Render2D, so no new dependency. Remove Render3D's now-unused direct `using StbImageSharp;`.

- [ ] **Step 4: Build + golden gate**

Run: `dotnet build KhaozEngine.slnx && KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: Build succeeded; all goldens green (decode output is identical, same Stb call centralized).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/ KhaozEngine.Render3D/ KhaozEngine.Tests/
git commit -m "render: single image-decode path via ImageRgba (Render3D stops re-importing Stb)"
```

---

## Phase D: Correctness fixes

### Task D1: FileSettingsStorage reads with TolerantRead

**Files:**
- Modify: `KhaozEngine.Persistence/FileSettingsStorage.cs:48`
- Test: `KhaozEngine.Tests/Persistence/FileSettingsStorageTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests.Persistence;

public class FileSettingsStorageTolerantReadTests
{
    private sealed class Settings { public int Volume { get; set; } }

    [Fact]
    public void Load_AcceptsTrailingCommaAndComments()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, "{\n  // user edit\n  \"volume\": 7,\n}");
        try
        {
            var storage = new FileSettingsStorage(path);
            var s = storage.Load<Settings>();
            Assert.Equal(7, s.Volume);   // case-insensitive + comments + trailing comma all tolerated
        }
        finally { File.Delete(path); }
    }
}
```

(Match the real `FileSettingsStorage` constructor/`Load<T>` signature; read the file first to confirm.)

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileSettingsStorageTolerantRead"`
Expected: FAIL (current `Deserialize<T>(json)` with no options throws on the comment/trailing comma, and is case-sensitive).

- [ ] **Step 3: Fix the read**

`FileSettingsStorage.cs:48`: change `JsonSerializer.Deserialize<T>(json)` to `JsonSerializer.Deserialize<T>(json, JsonDefaults.TolerantRead)`. Confirm `using KhaozEngine.Serialization;` is present (Persistence already references Serialization).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileSettingsStorageTolerantRead"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/FileSettingsStorage.cs KhaozEngine.Tests/Persistence/FileSettingsStorageTolerantReadTests.cs
git commit -m "persistence: FileSettingsStorage reads with JsonDefaults.TolerantRead"
```

---

### Task D2: ECS save versioning + `[ComponentId]` stable keys

This task has three sub-behaviors, each TDD'd: (1) `Load` reads `FormatVersion` and throws on unknown future versions; (2) a migration-dispatch seam; (3) an optional `[ComponentId("...")]` attribute that overrides `Type.FullName` as the persisted key.

**Files:**
- Create: `KhaozEngine.Ecs/ComponentIdAttribute.cs`
- Modify: `KhaozEngine.Ecs/WorldSerializer.cs` (read `FormatVersion`, key by `[ComponentId]`, migration hook)
- Test: `KhaozEngine.Tests/Ecs/WorldSerializerVersioningTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

public class WorldSerializerVersioningTests
{
    [ComponentId("pos")]
    private struct Position { public int X; public int Y; }

    [Fact]
    public void UnknownFutureVersion_ThrowsTyped()
    {
        // A document whose FormatVersion is far above the current writer version must throw a clear error,
        // not silently mis-deserialize.
        string json = "{\"FormatVersion\": 9999, \"Entities\": []}";
        var ex = Record.Exception(() => WorldSerializer.Load(json));
        Assert.IsType<UnsupportedSaveVersionException>(ex);
    }

    [Fact]
    public void ComponentId_OverridesTypeFullNameInOutput()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddComponent(e, new Position { X = 1, Y = 2 });
        string json = WorldSerializer.Save(world);
        Assert.Contains("\"pos\"", json);
        Assert.DoesNotContain(typeof(Position).FullName!, json);
    }

    [Fact]
    public void Save_Then_Load_RoundTripsWithComponentId()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddComponent(e, new Position { X = 3, Y = 4 });
        var reloaded = WorldSerializer.Load(WorldSerializer.Save(world));
        // assert the component survived using the world's query API (match the real API surface)
        Assert.True(reloaded.TryGetComponent<Position>(reloaded.FirstEntity(), out var p));
        Assert.Equal((3, 4), (p.X, p.Y));
    }
}
```

(Adjust `World`/`WorldSerializer`/component-registration API names to the real ones after reading `WorldSerializer.cs` and `World.cs`; the test intent is fixed, the surface must match.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldSerializerVersioning"`
Expected: compile failure (`ComponentIdAttribute`, `UnsupportedSaveVersionException` missing) then test failures.

- [ ] **Step 3: Add the attribute + exception**

`KhaozEngine.Ecs/ComponentIdAttribute.cs`:

```csharp
using System;

namespace KhaozEngine.Ecs;

/// <summary>
/// Optional stable persistence key for a component type. When present, <see cref="WorldSerializer"/> writes
/// and reads the component under this id instead of <see cref="Type.FullName"/>, so renaming or moving the
/// struct does not break existing saves. Ids must be unique within a world's component set.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class ComponentIdAttribute : Attribute
{
    public string Id { get; }
    public ComponentIdAttribute(string id) => Id = id;
}
```

Add `UnsupportedSaveVersionException` (in `WorldSerializer.cs` or its own file):

```csharp
public sealed class UnsupportedSaveVersionException : Exception
{
    public int FoundVersion { get; }
    public int MaxSupportedVersion { get; }
    public UnsupportedSaveVersionException(int found, int maxSupported)
        : base($"Save FormatVersion {found} is newer than supported version {maxSupported}.")
    { FoundVersion = found; MaxSupportedVersion = maxSupported; }
}
```

- [ ] **Step 4: Implement version read + key-by-ComponentId + migration seam**

In `WorldSerializer.cs`:
- Define `const int CurrentFormatVersion = 1;` (the value written at the old `:117`).
- In `Load`, read `FormatVersion`; if `> CurrentFormatVersion` throw `UnsupportedSaveVersionException(found, CurrentFormatVersion)`; if `< CurrentFormatVersion`, run registered migrations in order (the seam below) before deserializing.
- Migration seam: `static readonly Dictionary<int, Func<JsonObject, JsonObject>> _migrations = new();` plus `public static void RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> upgrade)`. `Load` applies migrations from the document version up to `CurrentFormatVersion`.
- Component key resolution: replace the two `Type.FullName` sites (`:70` write, `:89` read) with a helper `static string KeyFor(Type t) => t.GetCustomAttribute<ComponentIdAttribute>()?.Id ?? t.FullName!;` and a reverse lookup keyed the same way when reading.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldSerializerVersioning"`
Expected: PASS. Also run the existing serializer tests `--filter "FullyQualifiedName~WorldSerializer"` — green (existing saves without `[ComponentId]` still key by `FullName`).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Ecs/ KhaozEngine.Tests/Ecs/
git commit -m "ecs: read FormatVersion + migration seam + [ComponentId] stable keys"
```

---

## Phase E: Pooling into ECS

### Task E1: Pool the EntityCommandBuffer per-playback dictionary

**Files:**
- Modify: `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj` (+Pooling ref)
- Modify: `KhaozEngine.Ecs/EntityCommandBuffer.cs:40`
- Test: `KhaozEngine.Tests/Ecs/EntityCommandBufferPoolingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

public class EntityCommandBufferPoolingTests
{
    [Fact]
    public void RepeatedPlayback_DoesNotGrowAllocations()
    {
        var world = new World();
        var ecb = new EntityCommandBuffer();
        // Warm up one playback to prime the pool.
        ecb.CreateEntity(); ecb.Playback(world);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) { ecb.CreateEntity(); ecb.Playback(world); }
        long after = GC.GetAllocatedBytesForCurrentThread();

        // The per-playback remap dictionary must be reused, not reallocated each playback.
        // Allow headroom for unrelated allocations but assert it is far below 50 fresh dictionaries.
        Assert.True(after - before < 50 * 1024, $"allocated {after - before} bytes across 50 playbacks");
    }
}
```

(Match the real `EntityCommandBuffer`/`World` API after reading the file; the intent is reuse, not the exact method names.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~EntityCommandBufferPooling"`
Expected: FAIL (a fresh `new Dictionary<int,Entity>()` per playback exceeds the budget).

- [ ] **Step 3: Wire ObjectPool**

`KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`:

```xml
<ProjectReference Include="../KhaozEngine.Pooling/KhaozEngine.Pooling.csproj" />
```

In `EntityCommandBuffer.cs`, replace the per-playback `new Dictionary<int,Entity>()` (`:40`) with a rented dictionary from a `static readonly ObjectPool<Dictionary<int,Entity>>` (factory `() => new()`, reset clears it). Wrap playback in `try { ... } finally { pool.Return(remap); }` so a throwing command does not leak the instance. Match `ObjectPool`'s real rent/return API (read `KhaozEngine.Pooling/ObjectPool.cs`).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~EntityCommandBufferPooling"`
Expected: PASS. Run the full ECS suite `--filter "FullyQualifiedName~Ecs"` — green (pooling is behavior-transparent).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Ecs/ KhaozEngine.Tests/Ecs/
git commit -m "ecs: pool EntityCommandBuffer playback dictionary via KhaozEngine.Pooling"
```

---

## Phase F: Audio onto the shared RNG

### Task F1: AudioSystem uses DeterministicRng; WavSynth uses XorRng

**Files:**
- Modify: `KhaozEngine.Audio/KhaozEngine.Audio.csproj` (+Primitives ref)
- Modify: `KhaozEngine.Audio/AudioSystem.cs:29,138` (RNG field + `SetRng`), `KhaozEngine.Audio/WavSynth.cs:69`
- Test: `KhaozEngine.Tests/Audio/AudioRandomTrackDeterminismTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using KhaozEngine.Primitives;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public class AudioRandomTrackDeterminismTests
{
    [Fact]
    public void SeededRng_ReproducesRandomTrackSequence()
    {
        // Two AudioSystems with the same seeded DeterministicRng pick the same track order.
        var seqA = PlayRandomSequence(new DeterministicRng(123), 10);
        var seqB = PlayRandomSequence(new DeterministicRng(123), 10);
        Assert.Equal(seqA, seqB);
    }

    private static int[] PlayRandomSequence(DeterministicRng rng, int n)
    {
        var audio = AudioSystem.CreateHeadlessForTests();   // existing test-double ctor/path
        audio.SetRng(rng);
        // register N fake tracks then call PlayRandomTrack n times, recording chosen indices.
        // (use the existing headless test seam used by the 5.71.0 rotation-pool tests)
        ...
    }
}
```

(Fill the body against the exact headless seam the existing `AudioSystem` rotation-pool tests use — grep `KhaozEngine.Tests` for `SetRotationPool`/`PlayRandomTrack` to copy the established test harness. Keep the determinism assertion as the fixed intent.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AudioRandomTrackDeterminism"`
Expected: compile failure (`SetRng` does not yet take `DeterministicRng`).

- [ ] **Step 3: Swap the RNG**

`KhaozEngine.Audio/KhaozEngine.Audio.csproj`:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

In `AudioSystem.cs`: `using KhaozEngine.Primitives;`. Replace the `System.Random` field (`:29`) with `private DeterministicRng _rng = new(0);` (or a constructor-supplied seed). Update `SetRng` (`:138`) to accept `DeterministicRng`. Replace random-index draws (`_rng.Next(count)`) — `DeterministicRng.Next(int)` already exists. In `WavSynth.cs:69`, replace the inline xorshift with a `XorRng` local seeded by the existing constant.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Audio"`
Expected: PASS, including the existing 5.71.0 rotation-pool tests (the track-*set* semantics are unchanged; only the RNG source differs, and those tests do not assert a specific random sequence — confirm; if one does, update its expected sequence to the DeterministicRng output).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Audio/ KhaozEngine.Tests/Audio/
git commit -m "audio: deterministic RNG via Primitives (replaces System.Random); WavSynth uses XorRng"
```

---

## Phase G: Packaging + release (6.0.0)

### Task G1: Foundation umbrella adds Primitives; full suite green

**Files:**
- Modify: `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`

- [ ] **Step 1: Add Primitives to the umbrella**

Add to the umbrella's `ProjectReference` list:

```xml
<ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
```

- [ ] **Step 2: Full build + full test (incl. golden)**

Run: `dotnet build KhaozEngine.slnx && KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: Build succeeded; ALL tests green (the full ~1122+ suite plus the new Primitives/versioning/pooling/audio tests).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Foundation/
git commit -m "foundation: umbrella includes KhaozEngine.Primitives"
```

---

### Task G2: Version bump + CHANGELOG + doc-version declarations + pack + tag

**Files:**
- Modify: `Directory.Build.props:18`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the version**

`Directory.Build.props:18`: `<KhaozEngineVersion>5.71.0</KhaozEngineVersion>` -> `<KhaozEngineVersion>6.0.0</KhaozEngineVersion>`.

- [ ] **Step 2: CHANGELOG entry (newest-first)**

Add at the top of `CHANGELOG.md`:

```markdown
## 6.0.0

BREAKING. First 6.x release.

- New package **KhaozEngine.Primitives** (zero-dependency leaf): `Color` (now with `FromHex`/`ToHex`),
  `DeterministicRng` (moved from Ecs, `StableHash` now public), `XorRng` (value-type PRNG, promoted from
  Particles), `MathUtil` (`Clamp01`/`Lerp`/`InverseLerp`), `ViewportMath` (`Fit`/`Cover`), `Easing` (moved
  from Render2D).
- BREAKING: the public color API across Gpu/Render2D/Render3D/Particles/Content now takes
  `KhaozEngine.Primitives.Color` instead of `Vector4`. `IGpuCommandList.ClearColorTarget` takes `Color`.
  `Content.ColorHex` removed (use `Color.FromHex`/`Color.ToHex`). Internal GPU layout structs are unchanged.
  Rendering output is byte-identical.
- BREAKING: `KhaozEngine.Ecs.DeterministicRng` moved to `KhaozEngine.Primitives` (update `using`s).
- Ecs save format: `WorldSerializer` now reads `FormatVersion` (throws `UnsupportedSaveVersionException` on
  unknown future versions), has a migration-registration seam, and supports `[ComponentId("key")]` for
  rename-stable component keys.
- Audio random-track selection now uses the deterministic `DeterministicRng` (via `SetRng`) instead of
  `System.Random`; the random sequence changes, the rotation-pool track-set semantics are unchanged.
- Fix: `FileSettingsStorage` reads with `JsonDefaults.TolerantRead` (comments / trailing commas /
  case-insensitive), matching its write and `GameStorage`.
- Internal: single image-decode path (`ImageRgba`); `EntityCommandBuffer` playback dictionary pooled
  via `KhaozEngine.Pooling`; viewport-fit math centralized in `ViewportMath`.
```

- [ ] **Step 3: Update the three doc-version declarations + CONSUMERS package list**

- `docs/CONSUMERS.md` "Engine current version" -> `6.0.0`, and add `KhaozEngine.Primitives` to the package list/matrix.
- `docs/ROADMAP.md` "Current released version" -> `6.0.0`.
- `README.md` the `<PackageReference ... Version="..." />` example -> `6.0.0`.

- [ ] **Step 4: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (the three declarations all read `6.0.0`).

- [ ] **Step 5: Pack to local-feed (cumulative)**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: all packable projects (including `KhaozEngine.Primitives` and the four umbrellas) emit `6.0.0` `.nupkg` + `.snupkg` into `local-feed` without removing prior versions.

- [ ] **Step 6: Commit + tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "release(6.0.0): Primitives leaf + Color migration + cleanup batch"
git tag v6.0.0
```

(Pushing `main` + the tag is the finishing-branch step, handled per the user's merge-back menu, not inside this task.)

---

## Self-review notes (for the executor)

- Spec coverage: every spec section maps to a task — leaf (A1-A7), breaking Color (B1-B5, B7), viewport/Easing helpers (A6/A7/B6/B7), image-decode (C1), the two fixes (D1, D2 incl. `[ComponentId]`), Pooling (E1), Audio RNG (F1), packaging+release+6.0.0 (G1, G2).
- Type-name consistency: `Color`, `DeterministicRng`, `XorRng`, `MathUtil`, `ViewportMath`, `Easing`, `ComponentIdAttribute`, `UnsupportedSaveVersionException` are used with the same names everywhere they appear.
- Known soft spots the executor must reconcile against the real source (read the file before editing): exact `FileSettingsStorage` ctor/`Load<T>` shape (D1), `World`/`WorldSerializer`/`EntityCommandBuffer` API names (D2/E1), `ObjectPool` rent/return surface (E1), and the existing `AudioSystem` headless test seam (F1). The test *intent* in each is fixed; the surrounding API calls must match reality.
- The golden gate (`KE_GPU_TESTS=1`) is run at B7, C1, and G1 — these are the hard checkpoints that the breaking migration kept rendering byte-identical.
