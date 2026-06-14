# PixelLab -> Direction8 sheet assembler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an offline console tool that turns a PixelLab character export (zip/dir) + an animation name into one uniform grid sheet (8 Direction8 rows x N frame columns) ready for `PixelLabSpriteLoader.FromGridSheet`, plus the `frameCount`/`fps` to use.

**Architecture:** A `net10.0` console project `tools/PixelLabSheetAssembler/` (IsPackable=false, never NuGet-packed) with a testable pure core (`SheetAssembler`, `GapFiller`, `Bbox`, `DirectionRows`) split from the IO boundary (`PixelLabExport`) and CLI (`Program`). A sibling xUnit project `tools/PixelLabSheetAssembler.Tests/` tests the core with synthetic in-memory PNGs and pins the row table against the live `KhaozEngine.Sprites.Direction8` enum. Both are added to `KhaozEngine.slnx` so CI builds/tests them.

**Tech Stack:** C# / net10.0, SixLabors.ImageSharp 2.1.13 (Apache-2.0) for PNG IO + pixel access, System.Text.Json, System.IO.Compression, xUnit.

**Spec:** `docs/superpowers/specs/2026-06-14-pixellab-sheet-assembler-design.md`

**Conventions inherited from `Directory.Build.props`:** `net10.0`, `Nullable=enable`, `ImplicitUsings=disable` (so every file needs explicit `using` directives). SourceLink/LICENSE packing applies only when `IsPackable != false`, so the new projects (IsPackable=false) stay clean.

**Run all commands from the worktree root** `/Users/antonio/KhaozEngine/.claude/worktrees/pixellab-sheet-assembler`.

---

## Task 1: Scaffold the two projects and wire them into the solution

**Files:**
- Create: `tools/PixelLabSheetAssembler/PixelLabSheetAssembler.csproj`
- Create: `tools/PixelLabSheetAssembler/Program.cs` (temporary stub, replaced in Task 7)
- Create: `tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj`
- Create: `tools/PixelLabSheetAssembler.Tests/SmokeTest.cs`
- Modify: `KhaozEngine.slnx`

- [ ] **Step 1: Create the tool project file**

`tools/PixelLabSheetAssembler/PixelLabSheetAssembler.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <AssemblyName>PixelLabSheetAssembler</AssemblyName>
    <RootNamespace>PixelLabSheetAssembler</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SixLabors.ImageSharp" Version="2.1.13" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a temporary Program.cs stub**

`tools/PixelLabSheetAssembler/Program.cs`:

```csharp
using System;

namespace PixelLabSheetAssembler;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("PixelLabSheetAssembler (stub)");
        return 0;
    }
}
```

- [ ] **Step 3: Create the test project file**

`tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj` (versions mirror `KhaozEngine.Tests`; references the tool plus `KhaozEngine.Sprites` for enum pinning):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../PixelLabSheetAssembler/PixelLabSheetAssembler.csproj" />
    <ProjectReference Include="../../KhaozEngine.Sprites/KhaozEngine.Sprites.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create a smoke test**

`tools/PixelLabSheetAssembler.Tests/SmokeTest.cs`:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class SmokeTest
{
    [Fact]
    public void ImageSharp_creates_transparent_image()
    {
        using var img = new Image<Rgba32>(4, 4);
        Assert.Equal(0, img[0, 0].A);
    }
}
```

- [ ] **Step 5: Add both projects to `KhaozEngine.slnx`**

Insert these two lines into `KhaozEngine.slnx` immediately before the closing `</Solution>` tag (keep them alphabetical-ish at the end is fine):

```xml
  <Project Path="tools/PixelLabSheetAssembler/PixelLabSheetAssembler.csproj" />
  <Project Path="tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj" />
```

- [ ] **Step 6: Restore + build + run the smoke test**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release`
Expected: PASS (1 test). This proves the ImageSharp restore and the cross-project references resolve.

- [ ] **Step 7: Commit**

```bash
git add tools/PixelLabSheetAssembler tools/PixelLabSheetAssembler.Tests KhaozEngine.slnx
git commit -m "build(tools): scaffold PixelLabSheetAssembler tool + test projects"
```

---

## Task 2: Direction name -> row table, pinned to the live Direction8 enum

**Files:**
- Create: `tools/PixelLabSheetAssembler/DirectionRows.cs`
- Test: `tools/PixelLabSheetAssembler.Tests/DirectionRowsTests.cs`

- [ ] **Step 1: Write the failing test**

`tools/PixelLabSheetAssembler.Tests/DirectionRowsTests.cs`:

```csharp
using System;
using KhaozEngine.Sprites;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class DirectionRowsTests
{
    [Fact]
    public void NameToRow_matches_live_Direction8_order()
    {
        // PixelLab export dir-name -> the Direction8 it must land on.
        var expected = new (string Name, Direction8 Dir)[]
        {
            ("south", Direction8.S),
            ("south-east", Direction8.SE),
            ("east", Direction8.E),
            ("north-east", Direction8.NE),
            ("north", Direction8.N),
            ("north-west", Direction8.NW),
            ("west", Direction8.W),
            ("south-west", Direction8.SW),
        };

        Assert.Equal(8, DirectionRows.NameToRow.Count);
        foreach (var (name, dir) in expected)
        {
            Assert.True(DirectionRows.NameToRow.ContainsKey(name), $"missing dir name '{name}'");
            // Row must equal the enum's integer value AND the loader's RowFor (the source of truth).
            Assert.Equal((int)dir, DirectionRows.NameToRow[name]);
            Assert.Equal(PixelLabSpriteLoader.RowFor(dir), DirectionRows.NameToRow[name]);
        }

        // Every Direction8 member is covered by some name (no row left unmapped).
        foreach (Direction8 d in Enum.GetValues<Direction8>())
            Assert.Contains((int)d, DirectionRows.NameToRow.Values);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter DirectionRowsTests`
Expected: FAIL to compile ("DirectionRows does not exist").

- [ ] **Step 3: Write the implementation**

`tools/PixelLabSheetAssembler/DirectionRows.cs`:

```csharp
using System.Collections.Generic;

namespace PixelLabSheetAssembler;

/// <summary>
/// Maps a PixelLab export direction name to its grid-sheet row index. Row order is the
/// KhaozEngine.Sprites.Direction8 integer order (S, SE, E, NE, N, NW, W, SW), which is exactly
/// what PixelLabSpriteLoader.FromGridSheet expects. Pinned against the live enum by
/// PixelLabSheetAssembler.Tests.DirectionRowsTests so an enum reorder fails loudly.
/// </summary>
public static class DirectionRows
{
    public const int RowCount = 8;

    public static readonly IReadOnlyDictionary<string, int> NameToRow = new Dictionary<string, int>
    {
        ["south"] = 0,
        ["south-east"] = 1,
        ["east"] = 2,
        ["north-east"] = 3,
        ["north"] = 4,
        ["north-west"] = 5,
        ["west"] = 6,
        ["south-west"] = 7,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter DirectionRowsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/PixelLabSheetAssembler/DirectionRows.cs tools/PixelLabSheetAssembler.Tests/DirectionRowsTests.cs
git commit -m "feat(tools): direction name->row table pinned to Direction8"
```

---

## Task 3: Gap-fill resolution (hold previous, else next; strict; warnings)

**Files:**
- Create: `tools/PixelLabSheetAssembler/AssemblyException.cs`
- Create: `tools/PixelLabSheetAssembler/GapFiller.cs`
- Test: `tools/PixelLabSheetAssembler.Tests/GapFillerTests.cs`

- [ ] **Step 1: Write the failing test**

`tools/PixelLabSheetAssembler.Tests/GapFillerTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class GapFillerTests
{
    [Fact]
    public void Mid_gap_holds_previous_frame_with_warning()
    {
        var warnings = new List<string>();
        int[] sources = GapFiller.Resolve(
            "north-west", "walking", new HashSet<int> { 0, 1, 3, 4, 5 }, frameCount: 6,
            strict: false, warnings);

        Assert.Equal(new[] { 0, 1, 1, 3, 4, 5 }, sources); // index 2 held from 1
        Assert.Single(warnings);
        Assert.Contains("frame_002", warnings[0]);
        Assert.Contains("held frame_001", warnings[0]);
    }

    [Fact]
    public void Leading_gap_holds_next_frame_with_warning()
    {
        var warnings = new List<string>();
        int[] sources = GapFiller.Resolve(
            "west", "walking", new HashSet<int> { 1, 2, 3, 4, 5 }, frameCount: 6,
            strict: false, warnings);

        Assert.Equal(new[] { 1, 1, 2, 3, 4, 5 }, sources); // index 0 held from 1 (next)
        Assert.Single(warnings);
        Assert.Contains("frame_000", warnings[0]);
        Assert.Contains("held frame_001", warnings[0]);
    }

    [Fact]
    public void No_gaps_produces_identity_and_no_warnings()
    {
        var warnings = new List<string>();
        int[] sources = GapFiller.Resolve(
            "south", "walking", new HashSet<int> { 0, 1, 2 }, frameCount: 3,
            strict: false, warnings);

        Assert.Equal(new[] { 0, 1, 2 }, sources);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Strict_throws_on_first_gap()
    {
        var warnings = new List<string>();
        var ex = Assert.Throws<AssemblyException>(() => GapFiller.Resolve(
            "north-west", "walking", new HashSet<int> { 0, 1, 3 }, frameCount: 4,
            strict: true, warnings));
        Assert.Contains("frame_002", ex.Message);
    }

    [Fact]
    public void Empty_direction_throws()
    {
        var warnings = new List<string>();
        Assert.Throws<AssemblyException>(() => GapFiller.Resolve(
            "east", "walking", new HashSet<int>(), frameCount: 3, strict: false, warnings));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter GapFillerTests`
Expected: FAIL to compile ("GapFiller / AssemblyException do not exist").

- [ ] **Step 3: Write the exception type**

`tools/PixelLabSheetAssembler/AssemblyException.cs`:

```csharp
using System;

namespace PixelLabSheetAssembler;

/// <summary>Raised for any expected, user-facing assembly failure (bad input, missing dir, strict gap).</summary>
public sealed class AssemblyException : Exception
{
    public AssemblyException(string message) : base(message) { }
}
```

- [ ] **Step 4: Write the GapFiller implementation**

`tools/PixelLabSheetAssembler/GapFiller.cs`:

```csharp
using System.Collections.Generic;

namespace PixelLabSheetAssembler;

/// <summary>
/// Resolves, for one direction, which source frame index to draw into each of the
/// 0..frameCount-1 column slots. A missing index is filled by holding the nearest previous
/// present frame; if none precede (leading gap), the nearest following frame is held. Frames are
/// never shifted, so the row stays in sync. Each fill adds a warning. With <c>strict</c>
/// the first gap throws instead. A direction with no frames always throws.
/// </summary>
public static class GapFiller
{
    public static int[] Resolve(
        string dirName, string anim, IReadOnlySet<int> present, int frameCount,
        bool strict, List<string> warnings)
    {
        if (present.Count == 0)
            throw new AssemblyException($"direction '{dirName}' has no frames for animation '{anim}'.");

        var sources = new int[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            if (present.Contains(i))
            {
                sources[i] = i;
                continue;
            }

            if (strict)
                throw new AssemblyException($"{dirName}/{anim} frame_{i:000} missing (--strict).");

            int src = -1;
            for (int j = i - 1; j >= 0; j--)
                if (present.Contains(j)) { src = j; break; }
            if (src < 0)
                for (int j = i + 1; j < frameCount; j++)
                    if (present.Contains(j)) { src = j; break; }

            sources[i] = src;
            warnings.Add($"WARNING: {dirName}/{anim} frame_{i:000} missing - held frame_{src:000}");
        }

        return sources;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter GapFillerTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add tools/PixelLabSheetAssembler/AssemblyException.cs tools/PixelLabSheetAssembler/GapFiller.cs tools/PixelLabSheetAssembler.Tests/GapFillerTests.cs
git commit -m "feat(tools): gap-fill resolution (hold previous/next, strict, warnings)"
```

---

## Task 4: Opaque bounding-box scan

**Files:**
- Create: `tools/PixelLabSheetAssembler/Bbox.cs`
- Test: `tools/PixelLabSheetAssembler.Tests/BboxTests.cs`

- [ ] **Step 1: Write the failing test**

`tools/PixelLabSheetAssembler.Tests/BboxTests.cs`:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class BboxTests
{
    [Fact]
    public void Finds_inclusive_bounds_of_opaque_pixels()
    {
        using var img = new Image<Rgba32>(10, 10); // all transparent
        img[2, 3] = new Rgba32(255, 0, 0, 255);
        img[6, 8] = new Rgba32(0, 255, 0, 255);

        var b = Bbox.OpaqueBounds(img, alphaThreshold: 0);

        Assert.NotNull(b);
        Assert.Equal((2, 3, 6, 8), (b!.Value.MinX, b.Value.MinY, b.Value.MaxX, b.Value.MaxY));
    }

    [Fact]
    public void Returns_null_for_fully_transparent_image()
    {
        using var img = new Image<Rgba32>(5, 5);
        Assert.Null(Bbox.OpaqueBounds(img, alphaThreshold: 0));
    }

    [Fact]
    public void Threshold_excludes_low_alpha_pixels()
    {
        using var img = new Image<Rgba32>(5, 5);
        img[1, 1] = new Rgba32(0, 0, 0, 10);  // below threshold
        img[3, 3] = new Rgba32(0, 0, 0, 200); // above threshold

        var b = Bbox.OpaqueBounds(img, alphaThreshold: 50);

        Assert.NotNull(b);
        Assert.Equal((3, 3, 3, 3), (b!.Value.MinX, b.Value.MinY, b.Value.MaxX, b.Value.MaxY));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter BboxTests`
Expected: FAIL to compile ("Bbox does not exist").

- [ ] **Step 3: Write the implementation**

`tools/PixelLabSheetAssembler/Bbox.cs`:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>Computes the inclusive bounding box of a frame's opaque pixels (alpha &gt; threshold).</summary>
public static class Bbox
{
    public static (int MinX, int MinY, int MaxX, int MaxY)? OpaqueBounds(Image<Rgba32> img, int alphaThreshold)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                if (img[x, y].A > alphaThreshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0) return null;
        return (minX, minY, maxX, maxY);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter BboxTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/PixelLabSheetAssembler/Bbox.cs tools/PixelLabSheetAssembler.Tests/BboxTests.cs
git commit -m "feat(tools): opaque bounding-box scan"
```

---

## Task 5: Core types + SheetAssembler (cell sizing, anchoring, full sheet)

**Files:**
- Create: `tools/PixelLabSheetAssembler/Model.cs`
- Create: `tools/PixelLabSheetAssembler/SheetAssembler.cs`
- Test: `tools/PixelLabSheetAssembler.Tests/SheetAssemblerTests.cs`

- [ ] **Step 1: Write the model types**

`tools/PixelLabSheetAssembler/Model.cs`:

```csharp
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>One loaded frame: its parsed frame_NNN index and pixels.</summary>
public sealed record FrameEntry(int Index, Image<Rgba32> Image);

/// <summary>A character's single animation: per PixelLab direction name, the present frames (any order).</summary>
public sealed record CharacterAnimation(
    string CharacterName,
    string AnimName,
    IReadOnlyDictionary<string, IReadOnlyList<FrameEntry>> FramesByDir);

/// <summary>Assembly tuning. Defaults match the CLI defaults.</summary>
public sealed record AssemblyOptions(
    int BottomPad = 0,
    int AlphaThreshold = 0,
    bool Strict = false,
    float SuggestedFps = 10f);

/// <summary>Result of assembly: the composited sheet plus the values the consumer needs.</summary>
public sealed record AssemblyResult(
    Image<Rgba32> Sheet,
    int FrameCount,
    float SuggestedFps,
    IReadOnlyList<string> Warnings,
    int CellWidth,
    int CellHeight);
```

- [ ] **Step 2: Write the failing tests**

`tools/PixelLabSheetAssembler.Tests/SheetAssemblerTests.cs`:

```csharp
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class SheetAssemblerTests
{
    // All 8 PixelLab direction names, in any order.
    private static readonly string[] Dirs =
    {
        "south", "south-east", "east", "north-east", "north", "north-west", "west", "south-west",
    };

    // A frame with a single opaque pixel at (px, py) on a transparent canvas of (w, h).
    private static Image<Rgba32> Dot(int w, int h, int px, int py)
    {
        var img = new Image<Rgba32>(w, h);
        img[px, py] = new Rgba32(255, 255, 255, 255);
        return img;
    }

    private static CharacterAnimation FullAnim(int w, int h, int frameCount, int px, int py)
    {
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
        {
            var frames = new List<FrameEntry>();
            for (int i = 0; i < frameCount; i++)
                frames.Add(new FrameEntry(i, Dot(w, h, px, py)));
            byDir[d] = frames;
        }
        return new CharacterAnimation("Test", "walking", byDir);
    }

    [Fact]
    public void Sheet_dimensions_are_8_rows_by_frameCount_columns()
    {
        var anim = FullAnim(w: 16, h: 16, frameCount: 4, px: 8, py: 15);
        using var r = SheetAssembler.Assemble(anim, new AssemblyOptions()).Sheet;

        Assert.Equal(16 * 4, r.Width);
        Assert.Equal(16 * 8, r.Height);
    }

    [Fact]
    public void Cell_size_is_max_of_frame_dimensions()
    {
        // Make one direction's frames larger; the cell must grow to fit the largest.
        var anim = FullAnim(w: 16, h: 16, frameCount: 2, px: 8, py: 15);
        var big = new List<FrameEntry> { new(0, Dot(20, 24, 10, 23)), new(1, Dot(20, 24, 10, 23)) };
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>(anim.FramesByDir) { ["south"] = big };
        var anim2 = anim with { FramesByDir = byDir };

        var result = SheetAssembler.Assemble(anim2, new AssemblyOptions());
        using var sheet = result.Sheet;

        Assert.Equal(20, result.CellWidth);
        Assert.Equal(24, result.CellHeight);
    }

    [Fact]
    public void Feet_land_on_baseline_regardless_of_source_vertical_position()
    {
        // bottomPad 0 => baseline row = cellH-1. The opaque pixel (the "foot") must land there
        // in every cell, even though the source pixel sits at different y in different frames.
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
        {
            // frame 0 foot high in canvas (y=5), frame 1 foot low (y=15): both must end at baseline.
            byDir[d] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 5)), new(1, Dot(16, 16, 8, 15)) };
        }
        var anim = new CharacterAnimation("Test", "walking", byDir);

        var result = SheetAssembler.Assemble(anim, new AssemblyOptions(BottomPad: 0));
        using var sheet = result.Sheet;

        int cell = 16;
        // south is row 0. Both columns: opaque pixel must be at cell-local y = 15 (baseline).
        Assert.Equal(255, sheet[8, 15].A);        // row 0, col 0, baseline
        Assert.Equal(255, sheet[16 + 8, 15].A);   // row 0, col 1, baseline
        // And NOT floating above (the source y differences are normalized away).
        Assert.Equal(0, sheet[8, 5].A);
    }

    [Fact]
    public void BottomPad_lifts_feet_off_the_cell_bottom()
    {
        var anim = FullAnim(w: 16, h: 16, frameCount: 1, px: 8, py: 15);
        var result = SheetAssembler.Assemble(anim, new AssemblyOptions(BottomPad: 2));
        using var sheet = result.Sheet;

        // baseline row = cellH - bottomPad - 1 = 13.
        Assert.Equal(255, sheet[8, 13].A);
        Assert.Equal(0, sheet[8, 15].A);
    }

    [Fact]
    public void Row_for_each_direction_matches_DirectionRows()
    {
        // Put a unique marker color per direction at the foot, then read it back at each row's baseline.
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
        {
            var img = new Image<Rgba32>(16, 16);
            img[8, 15] = new Rgba32((byte)(DirectionRows.NameToRow[d] * 10 + 5), 0, 0, 255);
            byDir[d] = new List<FrameEntry> { new(0, img) };
        }
        var anim = new CharacterAnimation("Test", "walking", byDir);

        using var sheet = SheetAssembler.Assemble(anim, new AssemblyOptions()).Sheet;

        foreach (var d in Dirs)
        {
            int row = DirectionRows.NameToRow[d];
            Assert.Equal((byte)(row * 10 + 5), sheet[8, row * 16 + 15].R);
        }
    }

    [Fact]
    public void Mid_gap_is_held_and_reported()
    {
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
            byDir[d] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 15)), new(1, Dot(16, 16, 8, 15)), new(2, Dot(16, 16, 8, 15)) };
        // north-west drops index 1.
        byDir["north-west"] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 15)), new(2, Dot(16, 16, 8, 15)) };
        var anim = new CharacterAnimation("Test", "walking", byDir);

        var result = SheetAssembler.Assemble(anim, new AssemblyOptions());
        using var sheet = result.Sheet;

        Assert.Equal(3, result.FrameCount);
        Assert.Single(result.Warnings);
        Assert.Contains("north-west", result.Warnings[0]);
        Assert.Contains("frame_001", result.Warnings[0]);
    }

    [Fact]
    public void Missing_whole_direction_throws()
    {
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
            byDir[d] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 15)) };
        byDir.Remove("east");
        var anim = new CharacterAnimation("Test", "walking", byDir);

        var ex = Assert.Throws<AssemblyException>(() => SheetAssembler.Assemble(anim, new AssemblyOptions()));
        Assert.Contains("east", ex.Message);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter SheetAssemblerTests`
Expected: FAIL to compile ("SheetAssembler does not exist").

- [ ] **Step 4: Write the SheetAssembler**

`tools/PixelLabSheetAssembler/SheetAssembler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>
/// Pure (no file IO) compositor: turns a CharacterAnimation into one grid sheet
/// (8 Direction8 rows x frameCount columns, uniform cells). Each frame is placed feet-on-baseline
/// via its opaque bbox bottom; missing frames are gap-filled by <see cref="GapFiller"/>.
/// </summary>
public static class SheetAssembler
{
    public static AssemblyResult Assemble(CharacterAnimation anim, AssemblyOptions opt)
    {
        // 1. Every direction must be present and non-empty.
        foreach (var name in DirectionRows.NameToRow.Keys)
        {
            if (!anim.FramesByDir.TryGetValue(name, out var list) || list.Count == 0)
                throw new AssemblyException($"animation '{anim.AnimName}' is missing direction '{name}'.");
        }

        // 2. frameCount = highest index + 1 across all directions.
        int frameCount = 0;
        foreach (var list in anim.FramesByDir.Values)
            foreach (var f in list)
                frameCount = Math.Max(frameCount, f.Index + 1);
        if (frameCount == 0)
            throw new AssemblyException("no frames found.");

        // 3. Uniform cell = max frame dimensions across all present frames.
        int cellW = 0, cellH = 0;
        foreach (var list in anim.FramesByDir.Values)
            foreach (var f in list)
            {
                cellW = Math.Max(cellW, f.Image.Width);
                cellH = Math.Max(cellH, f.Image.Height);
            }

        var warnings = new List<string>();
        var sheet = new Image<Rgba32>(cellW * frameCount, cellH * DirectionRows.RowCount); // transparent

        // 4. Composite each direction row.
        foreach (var (name, row) in DirectionRows.NameToRow)
        {
            var byIndex = anim.FramesByDir[name].ToDictionary(f => f.Index, f => f.Image);
            var present = new HashSet<int>(byIndex.Keys);
            int[] sources = GapFiller.Resolve(name, anim.AnimName, present, frameCount, opt.Strict, warnings);

            for (int col = 0; col < frameCount; col++)
            {
                Image<Rgba32> frame = byIndex[sources[col]];
                Blit(sheet, frame, col * cellW, row * cellH, cellW, cellH, opt.BottomPad, opt.AlphaThreshold);
            }
        }

        return new AssemblyResult(sheet, frameCount, opt.SuggestedFps, warnings, cellW, cellH);
    }

    // Places one frame into the cell at (cellX, cellY). Horizontally centers the frame canvas;
    // vertically maps the opaque-bbox bottom to the baseline (cellH - bottomPad - 1). A fully
    // transparent frame is bottom-aligned with no normalization. Source pixels outside the cell
    // are clipped (only ever the transparent padding).
    private static void Blit(
        Image<Rgba32> sheet, Image<Rgba32> frame,
        int cellX, int cellY, int cellW, int cellH, int bottomPad, int alphaThreshold)
    {
        int frameW = frame.Width, frameH = frame.Height;
        int xOff = (cellW - frameW) / 2;
        int yOff;

        var bounds = Bbox.OpaqueBounds(frame, alphaThreshold);
        if (bounds is null)
        {
            yOff = cellH - frameH; // bottom-align the canvas; content is empty anyway
        }
        else
        {
            int baselineRow = cellH - bottomPad - 1;
            yOff = baselineRow - bounds.Value.MaxY;
        }

        for (int y = 0; y < frameH; y++)
        {
            int dy = cellY + yOff + y;
            if (dy < cellY || dy >= cellY + cellH) continue;
            for (int x = 0; x < frameW; x++)
            {
                int dx = cellX + xOff + x;
                if (dx < cellX || dx >= cellX + cellW) continue;
                sheet[dx, dy] = frame[x, y];
            }
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter SheetAssemblerTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add tools/PixelLabSheetAssembler/Model.cs tools/PixelLabSheetAssembler/SheetAssembler.cs tools/PixelLabSheetAssembler.Tests/SheetAssemblerTests.cs
git commit -m "feat(tools): SheetAssembler cell sizing + feet-on-baseline compositing"
```

---

## Task 6: PixelLabExport (zip/dir resolution, metadata parse, frame loading)

**Files:**
- Create: `tools/PixelLabSheetAssembler/PixelLabExport.cs`
- Test: `tools/PixelLabSheetAssembler.Tests/PixelLabExportTests.cs`

- [ ] **Step 1: Write the failing test**

This test writes a tiny synthetic export to a temp dir (metadata.json + frame PNGs, with a mid gap), loads it, and asserts the parse. PNG sizes vary to confirm real dims are read.

`tools/PixelLabSheetAssembler.Tests/PixelLabExportTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class PixelLabExportTests
{
    private static readonly string[] Dirs =
    {
        "south", "south-east", "east", "north-east", "north", "north-west", "west", "south-west",
    };

    // Build a synthetic export under a fresh temp dir; returns the root dir path.
    // "north-west" omits frame_002 (mid gap). All frames are 12x12.
    private static string WriteExport()
    {
        string root = Directory.CreateTempSubdirectory("pl_export_test_").FullName;
        string folder = "Hero";

        var anims = new StringBuilder();
        var dirJson = new List<string>();
        foreach (var d in Dirs)
        {
            var indices = d == "north-west" ? new[] { 0, 1 } : new[] { 0, 1, 2 };
            var paths = new List<string>();
            foreach (int i in indices)
            {
                string rel = $"{folder}/animations/walking/{d}/frame_{i:000}.png";
                string full = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using (var img = new Image<Rgba32>(12, 12)) { img[6, 11] = new Rgba32(255, 255, 255, 255); img.SaveAsPng(full); }
                paths.Add($"\"{rel}\"");
            }
            dirJson.Add($"\"{d}\": [{string.Join(",", paths)}]");
        }

        string meta = $@"{{
  ""states"": [
    {{
      ""character"": {{ ""name"": ""Hero"", ""size"": {{ ""width"": 12, ""height"": 12 }} }},
      ""folder"": ""{folder}"",
      ""frames"": {{ ""animations"": {{ ""walking"": {{ {string.Join(",", dirJson)} }} }} }}
    }}
  ]
}}";
        File.WriteAllText(Path.Combine(root, "metadata.json"), meta);
        return root;
    }

    [Fact]
    public void Loads_directory_export_and_reports_character_name()
    {
        string root = WriteExport();
        var (anim, temp) = PixelLabExport.Load(root, "walking");

        Assert.Null(temp); // a dir input is not extracted, so nothing to clean up
        Assert.Equal("Hero", anim.CharacterName);
        Assert.Equal("walking", anim.AnimName);
        Assert.Equal(8, anim.FramesByDir.Count);
    }

    [Fact]
    public void Parses_frame_indices_and_drops_the_gap()
    {
        string root = WriteExport();
        var (anim, _) = PixelLabExport.Load(root, "walking");

        var nw = anim.FramesByDir["north-west"].Select(f => f.Index).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 1 }, nw); // frame_002 absent

        var south = anim.FramesByDir["south"].Select(f => f.Index).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, south);
    }

    [Fact]
    public void Unknown_animation_lists_available_names()
    {
        string root = WriteExport();
        var ex = Assert.Throws<AssemblyException>(() => PixelLabExport.Load(root, "running"));
        Assert.Contains("walking", ex.Message);
    }

    [Fact]
    public void End_to_end_assembles_a_loaded_export()
    {
        string root = WriteExport();
        var (anim, _) = PixelLabExport.Load(root, "walking");
        var result = SheetAssembler.Assemble(anim, new AssemblyOptions());
        using var sheet = result.Sheet;

        Assert.Equal(3, result.FrameCount);
        Assert.Equal(12 * 3, sheet.Width);
        Assert.Equal(12 * 8, sheet.Height);
        Assert.Single(result.Warnings); // the north-west gap
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter PixelLabExportTests`
Expected: FAIL to compile ("PixelLabExport does not exist").

- [ ] **Step 3: Write the implementation**

`tools/PixelLabSheetAssembler/PixelLabExport.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>
/// IO boundary: resolves a PixelLab character export (zip or unzipped dir), parses metadata.json,
/// and loads the chosen animation's frames. Returns the parsed animation plus, when a zip was
/// extracted, the temp directory the caller must delete.
/// </summary>
public static class PixelLabExport
{
    public static (CharacterAnimation Anim, string? TempDir) Load(string input, string animName)
    {
        string root;
        string? temp = null;

        if (Directory.Exists(input))
        {
            root = input;
        }
        else if (File.Exists(input) && input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            temp = Directory.CreateTempSubdirectory("pixellab_").FullName;
            ZipFile.ExtractToDirectory(input, temp);
            root = temp;
        }
        else
        {
            throw new AssemblyException($"input not found or not a .zip / directory: {input}");
        }

        string metaPath = Path.Combine(root, "metadata.json");
        if (!File.Exists(metaPath))
        {
            string[] found = Directory.GetFiles(root, "metadata.json", SearchOption.AllDirectories);
            if (found.Length == 0)
                throw new AssemblyException($"metadata.json not found under: {input}");
            metaPath = found[0];
            root = Path.GetDirectoryName(metaPath)!;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        JsonElement state = doc.RootElement.GetProperty("states")[0];
        string charName = state.GetProperty("character").GetProperty("name").GetString() ?? "character";

        JsonElement anims = state.GetProperty("frames").GetProperty("animations");
        if (!anims.TryGetProperty(animName, out JsonElement animEl))
        {
            IEnumerable<string> names = anims.EnumerateObject().Select(p => p.Name);
            throw new AssemblyException($"animation '{animName}' not found. Available: {string.Join(", ", names)}");
        }

        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (JsonProperty dirProp in animEl.EnumerateObject())
        {
            var list = new List<FrameEntry>();
            foreach (JsonElement pathEl in dirProp.Value.EnumerateArray())
            {
                string rel = pathEl.GetString()!;
                string full = Path.Combine(root, rel);
                if (!File.Exists(full)) continue; // metadata lists it but it's absent -> treat as a gap
                int idx = ParseIndex(rel);
                list.Add(new FrameEntry(idx, Image.Load<Rgba32>(full)));
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            byDir[dirProp.Name] = list;
        }

        return (new CharacterAnimation(charName, animName, byDir), temp);
    }

    private static int ParseIndex(string path)
    {
        Match m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        if (!m.Success)
            throw new AssemblyException($"cannot parse frame index from '{path}'.");
        return int.Parse(m.Value);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release --filter PixelLabExportTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/PixelLabSheetAssembler/PixelLabExport.cs tools/PixelLabSheetAssembler.Tests/PixelLabExportTests.cs
git commit -m "feat(tools): PixelLabExport zip/dir + metadata parse + frame loading"
```

---

## Task 7: CLI (Program.cs): arg parsing, wiring, report, exit codes

**Files:**
- Modify: `tools/PixelLabSheetAssembler/Program.cs` (replace the Task 1 stub)

- [ ] **Step 1: Replace the stub with the full CLI**

`tools/PixelLabSheetAssembler/Program.cs`:

```csharp
using System;
using System.IO;
using SixLabors.ImageSharp;

namespace PixelLabSheetAssembler;

internal static class Program
{
    private const string Usage =
        "PixelLabSheetAssembler - assemble a PixelLab character export into a Direction8 grid sheet.\n" +
        "\n" +
        "Usage:\n" +
        "  dotnet run --project tools/PixelLabSheetAssembler -- \\\n" +
        "    --input <char.zip|dir> --anim <name> [--out <path.png>] \\\n" +
        "    [--fps <n>] [--bottom-pad <px>] [--alpha-threshold <0-255>] [--strict]\n";

    private static int Main(string[] args)
    {
        string? input = null, anim = null, outPath = null;
        float fps = 10f;
        int bottomPad = 0, alphaThreshold = 0;
        bool strict = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input": input = Next(args, ref i); break;
                    case "--anim": anim = Next(args, ref i); break;
                    case "--out": outPath = Next(args, ref i); break;
                    case "--fps": fps = float.Parse(Next(args, ref i)); break;
                    case "--bottom-pad": bottomPad = int.Parse(Next(args, ref i)); break;
                    case "--alpha-threshold": alphaThreshold = int.Parse(Next(args, ref i)); break;
                    case "--strict": strict = true; break;
                    case "-h":
                    case "--help": Console.WriteLine(Usage); return 0;
                    default: throw new ArgumentException($"unknown argument '{args[i]}'.");
                }
            }

            if (string.IsNullOrEmpty(input)) throw new ArgumentException("--input is required.");
            if (string.IsNullOrEmpty(anim)) throw new ArgumentException("--anim is required.");
            if (fps <= 0f) throw new ArgumentException("--fps must be positive.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string? temp = null;
        try
        {
            var (charAnim, t) = PixelLabExport.Load(input!, anim!);
            temp = t;

            AssemblyResult result = SheetAssembler.Assemble(
                charAnim, new AssemblyOptions(bottomPad, alphaThreshold, strict, fps));

            outPath ??= DefaultOut(input!, anim!);
            using (result.Sheet)
            {
                result.Sheet.SaveAsPng(outPath);

                Console.WriteLine(
                    $"Wrote {outPath}  ({result.FrameCount}x{DirectionRows.RowCount} cells, " +
                    $"cell {result.CellWidth}x{result.CellHeight}, " +
                    $"sheet {result.Sheet.Width}x{result.Sheet.Height})");
            }
            Console.WriteLine($"frameCount = {result.FrameCount}");
            Console.WriteLine($"suggested fps = {result.SuggestedFps:0.#}");
            Console.WriteLine(
                $"FromGridSheet(sheet, frameCount: {result.FrameCount}, fps: {result.SuggestedFps:0.#}f)");
            foreach (string w in result.Warnings)
                Console.WriteLine(w);

            return 0;
        }
        catch (AssemblyException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            if (temp != null)
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value after '{args[i]}'.");
        return args[++i];
    }

    private static string DefaultOut(string input, string anim)
    {
        string dir = Directory.Exists(input) ? input : (Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".");
        string baseName = Directory.Exists(input)
            ? new DirectoryInfo(input).Name
            : Path.GetFileNameWithoutExtension(input);
        return Path.Combine(dir, $"{baseName}_{anim}.png");
    }
}
```

- [ ] **Step 2: Build the tool**

Run: `dotnet build tools/PixelLabSheetAssembler/PixelLabSheetAssembler.csproj -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Manual CLI check against a synthetic export**

Create a throwaway synthetic export and run the CLI end to end (no real fixtures needed here):

```bash
python3 - <<'PY'
import json, os, struct, zlib
root="/tmp/pl_cli_check"; os.makedirs(root, exist_ok=True)
def png(path,w=12,h=12):
    os.makedirs(os.path.dirname(path),exist_ok=True)
    def chunk(t,d): return struct.pack(">I",len(d))+t+d+struct.pack(">I",zlib.crc32(t+d)&0xffffffff)
    raw=b"".join(b"\x00"+b"\x00\x00\x00\x00"*w for _ in range(h))
    with open(path,"wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",w,h,8,6,0,0,0))+chunk(b"IDAT",zlib.compress(raw))+chunk(b"IEND",b""))
dirs=["south","south-east","east","north-east","north","north-west","west","south-west"]
anims={}
for d in dirs:
    idxs=[0,1] if d=="north-west" else [0,1,2]
    paths=[]
    for i in idxs:
        rel=f"Hero/animations/walking/{d}/frame_{i:03}.png"; png(os.path.join(root,rel)); paths.append(rel)
    anims[d]=paths
meta={"states":[{"character":{"name":"Hero","size":{"width":12,"height":12}},"folder":"Hero","frames":{"animations":{"walking":anims}}}]}
json.dump(meta,open(os.path.join(root,"metadata.json"),"w"))
print("wrote",root)
PY
dotnet run --project tools/PixelLabSheetAssembler -c Release -- --input /tmp/pl_cli_check --anim walking --out /tmp/pl_cli_check/out.png
```

Expected stdout: a `Wrote ...` line, `frameCount = 3`, `suggested fps = 10`, the `FromGridSheet(...)` line, and one `WARNING: north-west/walking frame_002 missing - held frame_001` line. Exit code 0.

- [ ] **Step 4: Manual CLI error-path check**

Run: `dotnet run --project tools/PixelLabSheetAssembler -c Release -- --input /tmp/pl_cli_check --anim running; echo "exit=$?"`
Expected: `error: animation 'running' not found. Available: walking` on stderr, `exit=1`.

- [ ] **Step 5: Commit**

```bash
git add tools/PixelLabSheetAssembler/Program.cs
git commit -m "feat(tools): PixelLabSheetAssembler CLI (args, report, exit codes)"
```

---

## Task 8: Documentation (README + CHANGELOG note)

**Files:**
- Create: `tools/PixelLabSheetAssembler/README.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Write the tool README**

`tools/PixelLabSheetAssembler/README.md`:

```markdown
# PixelLabSheetAssembler

Offline asset-prep tool. Turns a PixelLab character export (zip or unzipped dir) plus an
animation name into one grid sheet PNG (8 `Direction8` rows x N frame columns, uniform cell size),
ready for `KhaozEngine.Sprites.PixelLabSpriteLoader.FromGridSheet`. Shared by all KhaozEngine games.
Not a runtime package (IsPackable=false): it is never NuGet-packed and never published.

## Run

    dotnet run --project tools/PixelLabSheetAssembler -- \
      --input <char.zip|dir> --anim <name> [--out <path.png>] \
      [--fps <n>] [--bottom-pad <px>] [--alpha-threshold <0-255>] [--strict]

- `--input`   (required) PixelLab character `.zip` or unzipped export dir.
- `--anim`    (required) animation name under `animations/` (e.g. `walking`).
- `--out`     output PNG path. Default `<inputName>_<anim>.png` next to the input.
- `--fps`     suggested fps echoed back for `FromGridSheet` (default 10; PixelLab exports no timing).
- `--bottom-pad`      px between the feet baseline and the cell bottom (default 0).
- `--alpha-threshold` alpha above which a pixel counts as opaque for the bbox scan (default 0).
- `--strict`  fail on the first missing frame instead of holding the previous/next frame.

## What it does

- **Row order:** PixelLab dir names map to rows by name, in `Direction8` order (S, SE, E, NE, N, NW,
  W, SW). Pinned to the live enum by a test.
- **Uniform cells:** cell = max frame width/height; smaller frames are padded, none clipped.
- **Feet on the ground:** each frame's opaque bbox bottom is aligned to a baseline near the cell
  bottom, so the planted foot stays put under `SpriteAnchor.FootprintBottomCenter`.
- **Missing-frame tolerance:** a dropped frame is held from the nearest previous frame (or the next
  one for a leading gap), with a `WARNING`, never silently shifting the row. `--strict` turns the
  first gap into an error.

## Consuming the output

    var sheet = /* load the PNG as Texture2D */;
    var sprite = PixelLabSpriteLoader.FromGridSheet(sheet, frameCount, fps);

`frameCount` and the suggested `fps` are printed by the tool.
```

- [ ] **Step 2: Add a CHANGELOG note**

Open `CHANGELOG.md`, read the top entry's format, and add a `Tools` note at the top (above the
newest version entry). It is intentionally not tied to a package `<Version>` (the tool ships no
package). Use this text, matched to the file's heading style:

```markdown
## Tools

- Added `tools/PixelLabSheetAssembler` (offline, IsPackable=false): assembles a PixelLab character
  export (zip/dir) + an animation name into a `Direction8` grid sheet PNG for
  `PixelLabSpriteLoader.FromGridSheet`, with uniform cells, feet-on-baseline anchoring, and
  hold-previous missing-frame tolerance. Not packed or tagged (not a consumable package).
```

- [ ] **Step 3: Verify the doc-version guard still passes**

Run: `./scripts/check-doc-versions.sh; echo "exit=$?"`
Expected: `exit=0` (the tool adds no package, so the version declarations are untouched).

- [ ] **Step 4: Commit**

```bash
git add tools/PixelLabSheetAssembler/README.md CHANGELOG.md
git commit -m "docs(tools): README + CHANGELOG note for PixelLabSheetAssembler"
```

---

## Task 9: Full-suite green + manual verification against the real fixtures

**Files:** none (verification only).

- [ ] **Step 1: Run the whole engine test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release` then
`dotnet test tools/PixelLabSheetAssembler.Tests/PixelLabSheetAssembler.Tests.csproj -c Release`
Expected: both PASS, 0 failures. (CI runs the whole `.slnx`; this mirrors it locally.)

- [ ] **Step 2: Assemble the real drone fixture**

Run:
```bash
dotnet run --project tools/PixelLabSheetAssembler -c Release -- \
  --input ~/Hardpoint/art/iso/drone_sheet.zip --anim walking --out /tmp/drone_walking.png
```
Expected: `frameCount = 6`, `suggested fps = 10`, a `FromGridSheet(...)` line, and one warning
`WARNING: west/walking frame_000 missing - held frame_001` (leading gap). Note the printed values.

- [ ] **Step 3: Assemble the real tank fixture**

Run:
```bash
dotnet run --project tools/PixelLabSheetAssembler -c Release -- \
  --input ~/Hardpoint/art/iso/tank_sheet.zip --anim walking --out /tmp/tank_walking.png
```
Expected: `frameCount = 6`, `suggested fps = 10`, a `FromGridSheet(...)` line, and one warning
`WARNING: north-west/walking frame_002 missing - held frame_001` (mid gap). Note the printed values.

- [ ] **Step 4: Eyeball both output sheets**

Open `/tmp/drone_walking.png` and `/tmp/tank_walking.png` (Read tool renders PNGs). Confirm:
8 rows, 6 columns; rows in S, SE, E, NE, N, NW, W, SW order top-to-bottom; characters' feet sit on
a consistent bottom line across every cell (no floating); the held-gap cell duplicates its neighbour.

- [ ] **Step 5: Report the values for Hardpoint Phase 2b**

Record, for the changelog of work / handoff: drone `FromGridSheet(sheet, frameCount: 6, fps: 10f)`
and tank `FromGridSheet(sheet, frameCount: 6, fps: 10f)`, plus the exact invocation. (Both fixtures
are 6-frame walks; cell sizes 88x88 drone, 120x120 tank.)

---

## Self-review notes (author)

- **Spec coverage:** req1 row order -> Task 2; req2 uniform cell -> Task 5 (cell size + tests); req3
  feet anchoring -> Task 5 (Blit + baseline tests); req4 missing-frame tolerance -> Task 3 + Task 5
  integration + Task 9 real fixtures. Form factor / IsPackable / no-bump -> Tasks 1, 8. CLI -> Task 7.
- **Leading vs mid gap:** both covered (GapFillerTests + drone(west, leading) and tank(nw, mid) in Task 9).
- **Type consistency:** `DirectionRows.NameToRow`/`RowCount`, `GapFiller.Resolve`, `Bbox.OpaqueBounds`,
  `SheetAssembler.Assemble`, `AssemblyOptions(BottomPad, AlphaThreshold, Strict, SuggestedFps)`,
  `AssemblyResult(Sheet, FrameCount, SuggestedFps, Warnings, CellWidth, CellHeight)`,
  `CharacterAnimation(CharacterName, AnimName, FramesByDir)`, `FrameEntry(Index, Image)`,
  `PixelLabExport.Load -> (CharacterAnimation, string?)` are used consistently across tasks.
```
