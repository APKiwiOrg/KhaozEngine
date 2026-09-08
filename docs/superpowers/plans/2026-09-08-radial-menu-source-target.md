# Radial Menu and Source-Target Use Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Add a reusable interaction-anchored radial menu with an entry-local bottom choice strip, plus an opaque source-target use context.

**Architecture:** Keep both primitives in KhaozEngine.Gui. RadialMenu owns fixed-budget geometry, localized presentation, input routing, and caller-defined tags. GuiUseContext carries opaque source and target values across gestures. Neither type knows about items, recipes, crafting, stations, persistence, networking, or economy.

**Tech Stack:** .NET 10, KhaozEngine Gui, Windowing, Render2D, xUnit

**Spec:** docs/design/RADIAL-MENU-SOURCE-TARGET-DESIGN-2026-09-08.md

## Global Constraints

- Implement in a fresh worktree at /Users/antonio/KhaozEngine/.claude/worktrees/radial-menu-source-target from the latest origin/main.
- Issue https://github.com/APKiwiOrg/KhaozEngine/issues/854 owns this program.
- Keep crafting, recipes, items, stations, quantities, persistence, and economy out of KhaozEngine.
- RadialMenu accepts one through eight entries. The caller owns categorization beyond eight.
- The optional footer accepts zero through eight choices.
- Use LocalizedText for every displayed title, label, detail, and choice.
- Use only InputManager, Pointer, and their bounds helpers.
- Draw through existing Render2D primitives. Add no shader, framebuffer sampling, blur, refraction, or backend code.
- The opening gesture cannot select or dismiss the menu.
- The whole wheel and choice strip clamp and block input as one composition.
- Steady-state update and draw allocate zero bytes after opening and warming.
- The public API is additive. Take the next free engine minor version at execution time.
- Grimhollow is pinned and waiting. Pack and tag the released engine version after merge.

---

### Task 1: Define the radial values and pure geometry

**Files:**

- Create: KhaozEngine.Gui/RadialMenu.Types.cs
- Create: KhaozEngine.Gui/RadialMenu.Layout.cs
- Create: KhaozEngine.Gui.Tests/Gui/RadialMenuLayoutTests.cs

**Interfaces:**

- Consumes: LocalizedText, Rect, Vector2
- Produces: RadialMenuEntry, RadialMenuChoice, RadialMenuSelection, RadialMenuChoiceChange, RadialMenuMetrics, and pure layout helpers

- [ ] **Step 1: Write failing tests for entry limits, ordering, polar hit testing, footer geometry, and clamping.**

~~~csharp
public sealed class RadialMenuLayoutTests
{
    static RadialMenuMetrics Metrics => RadialMenuMetrics.Default;
    static readonly Rect Safe = new(0f, 0f, 960f, 540f);

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Entry_count_outside_one_through_eight_is_rejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RadialMenu.ValidateEntryCount(count));
    }

    [Fact]
    public void Footer_rejects_more_than_eight_choices()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RadialMenu.ValidateChoiceCount(9));
    }

    [Fact]
    public void Entry_zero_begins_at_twelve_oclock_and_order_is_clockwise()
    {
        Vector2 centre = new(480f, 270f);
        Assert.Equal(0, RadialMenu.EntryAt(new Vector2(480f, 130f), centre, 4, Metrics));
        Assert.Equal(1, RadialMenu.EntryAt(new Vector2(620f, 270f), centre, 4, Metrics));
    }

    [Fact]
    public void Clamp_moves_wheel_and_footer_as_one_composition()
    {
        Vector2 centre = RadialMenu.ComputeCenter(
            new Vector2(955f, 535f), Safe, 8, 4, Metrics);
        Rect bounds = RadialMenu.ComputeBounds(centre, 8, 4, Metrics);
        Assert.True(bounds.X >= Safe.X + Metrics.Margin);
        Assert.True(bounds.Y >= Safe.Y + Metrics.Margin);
        Assert.True(bounds.Right <= Safe.Right - Metrics.Margin);
        Assert.True(bounds.Bottom <= Safe.Bottom - Metrics.Margin);
    }
}
~~~

- [ ] **Step 2: Run the tests and verify the missing types fail the build.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuLayoutTests
~~~

Expected: build failure naming RadialMenu and RadialMenuMetrics.

- [ ] **Step 3: Implement the public values exactly.**

~~~csharp
public readonly record struct RadialMenuEntry(
    LocalizedText Content,
    long Tag,
    string? IconId = null,
    bool Enabled = true,
    LocalizedText Detail = default,
    long InitialChoiceTag = 0);

public readonly record struct RadialMenuChoice(
    LocalizedText Content,
    long Tag,
    bool Enabled = true);

public readonly record struct RadialMenuSelection(long EntryTag, long ChoiceTag);
public readonly record struct RadialMenuChoiceChange(long EntryTag, long ChoiceTag);
~~~

RadialMenuMetrics owns inner radius, outer radius, wedge gap, icon size, label scale, detail gap, footer gap, footer button size, margin, border thickness, shadow offset, and sheen speed. Defaults are 54, 148, 0.035 radians, 30, 0.82, 10, 10, 56 by 30, 8, 1.5, (0, 5), and 0.12.

- [ ] **Step 4: Implement pure helpers.**

Add ValidateEntryCount, ValidateChoiceCount, WedgeAngles, ComputeCenter, ComputeBounds, EntryAt, ChoiceBounds, and LabelPoint. Entry zero is centred at twelve o'clock. Order proceeds clockwise. Inner disc, angular gaps, and points beyond the outer radius return no entry.

- [ ] **Step 5: Run the focused tests green.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuLayoutTests
~~~

- [ ] **Step 6: Commit.**

~~~bash
git add KhaozEngine.Gui/RadialMenu.Types.cs KhaozEngine.Gui/RadialMenu.Layout.cs KhaozEngine.Gui.Tests/Gui/RadialMenuLayoutTests.cs
git commit -m "gui(radial): add fixed-budget radial layout"
~~~

---

### Task 2: Implement radial pointer and navigation state

**Files:**

- Create: KhaozEngine.Gui/RadialMenu.cs
- Create: KhaozEngine.Gui.Tests/Gui/RadialMenuInputTests.cs
- Modify: KhaozEngine.Gui/RadialMenu.Types.cs

**Interfaces:**

- Consumes: Task 1 geometry, Pointer, InputManager, PlayerIndex
- Produces: the retained RadialMenu state machine and one-frame results

- [ ] **Step 1: Add failing pointer tests using the MouseFrames pattern from ContextMenuTests.**

Cover all of these exact cases:

- the opening gesture cannot select or dismiss
- a fresh tap selects an enabled wedge and returns its entry and choice tags
- a disabled wedge becomes active for inspection but does not select
- a footer tap changes only the active entry's choice and leaves the menu open
- switching entries restores each entry's own choice
- an outside release dismisses and consumes
- the complete composition calls Pointer.BlockRegion
- SetEntryChoice changes state without firing WasChoiceChanged
- frame flags clear on the next update

~~~csharp
[Fact]
public void Choice_change_belongs_to_the_active_entry_and_selection_returns_both_tags()
{
    RadialMenu menu = OpenRecipes();
    Hover(menu, EntryPoint(1));
    TapChoice(menu, choiceTag: 10);
    Assert.Equal(new RadialMenuChoiceChange(102, 10), menu.ChoiceChange);

    Tap(menu, EntryPoint(1));
    Assert.Equal(new RadialMenuSelection(102, 10), menu.Selection);
}
~~~

- [ ] **Step 2: Add failing keyboard and gamepad tests.**

Left and Right cycle enabled wedges. Down enters the footer. Up returns to wedges. Left and Right cycle enabled footer choices while it owns focus. Menu-select selects the focused control. Menu-cancel dismisses. Navigation wraps and skips disabled choices.

- [ ] **Step 3: Run the focused tests and verify failure on the missing state machine.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuInputTests
~~~

- [ ] **Step 4: Implement the public state surface.**

~~~csharp
public sealed partial class RadialMenu
{
    public RadialMenuMetrics Metrics { get; set; } = RadialMenuMetrics.Default;
    public Rect SafeBounds { get; set; }
    public bool IsOpen { get; private set; }
    public int HoverIndex { get; private set; } = -1;
    public int ActiveIndex { get; private set; } = -1;
    public bool WasSelected { get; private set; }
    public RadialMenuSelection Selection { get; private set; }
    public bool WasChoiceChanged { get; private set; }
    public RadialMenuChoiceChange ChoiceChange { get; private set; }
    public bool WasDismissed { get; private set; }
    public Rect Bounds { get; private set; }

    public void Open(
        LocalizedText title,
        IReadOnlyList<RadialMenuEntry> entries,
        Vector2 anchor,
        IReadOnlyList<RadialMenuChoice>? choices = null);

    public bool SetEntryChoice(long entryTag, long choiceTag);
    public bool Update(Pointer pointer, float dt);
    public bool Update(InputManager input, float dt, bool focused, PlayerIndex? player = null);
    public void Close();
}
~~~

Open copies and resolves all content, validates unique entry and choice tags, validates each initial choice, computes the centre, seeds focus, and arms the opening latch. Update order is: clear flags, advance sheen, block bounds, compute hover, process footer, process wedge, then outside dismissal. Gate the last three behind the opening latch.

- [ ] **Step 5: Add a warm allocation test under the existing AllocSensitive collection.**

~~~csharp
[Fact]
[Collection("AllocSensitive")]
public void Warm_update_allocates_nothing()
{
    RadialMenu menu = OpenRecipes();
    Pointer pointer = IdlePointer();
    for (int i = 0; i < 20; i++) menu.Update(pointer, 1f / 60f);
    AllocAssert.Zero(() => menu.Update(pointer, 1f / 60f));
}
~~~

- [ ] **Step 6: Run all radial input and layout tests green.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenu
~~~

- [ ] **Step 7: Commit.**

~~~bash
git add KhaozEngine.Gui/RadialMenu.cs KhaozEngine.Gui/RadialMenu.Types.cs KhaozEngine.Gui.Tests/Gui/RadialMenuInputTests.cs
git commit -m "gui(radial): add radial selection and choices"
~~~

---

### Task 3: Draw the themed faux-glass wheel

**Files:**

- Create: KhaozEngine.Gui/RadialMenuTheme.cs
- Create: KhaozEngine.Gui/RadialMenu.Draw.cs
- Create: KhaozEngine.Gui.Tests/Gui/RadialMenuLocalizationTests.cs
- Create: KhaozEngine.Render.Tests/Gpu/RadialMenuGpuTests.cs

**Interfaces:**

- Consumes: Tasks 1 and 2, PrimitiveRenderer arc bands and arcs, SpriteBatch, IconAtlas
- Produces: themeable rendering and localized resolved display reads

- [ ] **Step 1: Write a failing localization test.**

Install a counting catalog through the existing AmbientLocalization collection. Open a menu carrying localized title, entry, detail, and choices. Read ResolvedTitle, ResolvedEntryLabel, ResolvedEntryDetail, and ResolvedChoiceLabel repeatedly. Assert the catalog count does not move after Open.

- [ ] **Step 2: Write a focused render test through the existing Render2D snapshot harness.**

Draw four wedges, one disabled entry, one active entry, four footer choices, one selected footer choice, and nonzero sheen time. Assert nontransparent coverage plus distinct samples for enabled, disabled, active, and selected states. Use the existing GpuFact gate. Do not add a new capture helper.

- [ ] **Step 3: Run both tests and verify the missing draw surface fails.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuLocalizationTests
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuGpuTests
~~~

- [ ] **Step 4: Implement RadialMenuTheme.Default from GuiTheme semantics.**

The theme fields are Shadow, Surface, SurfaceHighlight, Border, BorderActive, Accent, Text, TextMuted, Disabled, and Sheen. The default surface alpha is 0.78, highlight alpha 0.34, border alpha 0.72, active accent alpha 0.32, shadow alpha 0.38, and sheen alpha 0.08.

Add the public Theme property to the RadialMenu partial in this task, after RadialMenuTheme exists. Its default value is RadialMenuTheme.Default.

- [ ] **Step 5: Implement Draw with existing primitives only.**

~~~csharp
public void Draw(
    SpriteBatch batch,
    Texture2D white,
    SpriteFont font,
    IconAtlas? icons = null);
~~~

Draw order is shadow arc bands, surface arc bands, active accents, upper highlights, borders, icons, labels, centre title and active detail, footer shadow, footer buttons, then sheen. A missing icon leaves the label centred. Disabled alpha applies to every visual channel. Cache all per-open text and per-layout points.

- [ ] **Step 6: Add a warm draw allocation assertion and run focused tests green.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenu
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuGpuTests
~~~

- [ ] **Step 7: Commit.**

~~~bash
git add KhaozEngine.Gui/RadialMenuTheme.cs KhaozEngine.Gui/RadialMenu.Draw.cs KhaozEngine.Gui.Tests/Gui/RadialMenuLocalizationTests.cs KhaozEngine.Render.Tests/Gpu/RadialMenuGpuTests.cs
git commit -m "gui(radial): draw themed glass-like wheel"
~~~

---

### Task 4: Add GuiUseContext

**Files:**

- Create: KhaozEngine.Gui/GuiUseContext.cs
- Create: KhaozEngine.Gui.Tests/Gui/GuiUseContextTests.cs

**Interfaces:**

- Consumes: Pointer and Rect
- Produces: UsePayload, UseTarget, UseResult, and GuiUseContext

- [ ] **Step 1: Add failing tests for source begin, source replacement, accepted completion, refusal, rectangle completion, cancellation, gesture consumption, and frame flags.**

~~~csharp
[Fact]
public void Accepted_target_returns_both_opaque_halves_and_clears_source()
{
    var context = new GuiUseContext();
    Pointer pointer = FreshTap(new Vector2(20f, 20f));
    var source = new UsePayload("knife", "bag", 4);
    Assert.True(context.Begin(pointer, source));

    context.BeginFrame();
    pointer = FreshTap(new Vector2(40f, 40f));
    Assert.True(context.Complete(pointer, "bag", 7));
    Assert.Equal(new UseResult(source, new UseTarget("bag", 7)), context.LastUse);
    Assert.True(context.WasCompleted);
    Assert.False(context.IsActive);
    Assert.True(pointer.IsConsumed);
}

[Fact]
public void Refusal_keeps_source_and_does_not_consume()
{
    GuiUseContext context = ActiveContext();
    Pointer pointer = FreshTap(new Vector2(40f, 40f));
    Assert.False(context.Complete(pointer, "world", 19, accepted: false));
    Assert.True(context.IsActive);
    Assert.False(pointer.IsConsumed);
}
~~~

- [ ] **Step 2: Run the focused tests and verify missing-type failures.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~GuiUseContextTests
~~~

- [ ] **Step 3: Implement the public values and context.**

~~~csharp
public readonly record struct UsePayload(object? Token, object? SourceId = null, int SourceIndex = -1);
public readonly record struct UseTarget(object? TargetId, int TargetIndex = -1);
public readonly record struct UseResult(UsePayload Source, UseTarget Target);

public sealed class GuiUseContext
{
    public bool IsActive { get; private set; }
    public UsePayload Payload { get; private set; }
    public bool WasCompleted { get; private set; }
    public UseResult LastUse { get; private set; }
    public bool WasCancelled { get; private set; }
    public UsePayload CancelledPayload { get; private set; }

    public void BeginFrame();
    public bool Begin(Pointer pointer, in UsePayload payload);
    public bool Complete(Pointer pointer, object? targetId, int targetIndex = -1, bool accepted = true);
    public bool CompleteIn(Pointer pointer, Rect bounds, object? targetId, int targetIndex = -1, bool accepted = true);
    public void Cancel();
}
~~~

Begin replaces the active source and consumes its gesture. Refusal is a no-op. Accepted completion consumes the target gesture, records the result, and clears the source. CompleteIn uses Pointer.IsTapIn. Cancel is idempotent while idle.

- [ ] **Step 4: Run the focused tests and full Gui suite.**

~~~bash
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter FullyQualifiedName~GuiUseContextTests
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release
~~~

- [ ] **Step 5: Commit.**

~~~bash
git add KhaozEngine.Gui/GuiUseContext.cs KhaozEngine.Gui.Tests/Gui/GuiUseContextTests.cs
git commit -m "gui(use): add opaque source-target context"
~~~

---

### Task 5: Document, version, integrate, pack, and release

**Files:**

- Modify: KhaozEngine.Gui/README.md
- Modify: README.md
- Modify: docs/USING-KHAOZENGINE.md
- Modify: docs/design/RADIAL-MENU-SOURCE-TARGET-DESIGN-2026-09-08.md
- Modify: docs/INDEX.md
- Modify: CHANGELOG.md
- Modify: Directory.Build.props
- Modify: all versioned examples required by scripts/check-doc-versions.sh

**Interfaces:**

- Consumes: Tasks 1 through 4 and repository release scripts
- Produces: the released engine package for Grimhollow adoption

- [ ] **Step 1: Add complete package and usage examples.**

The radial example shows caller-defined action tags and generic footer tags. The use-context example carries opaque source and target values between two widgets. State explicitly that categories beyond eight, persistence, and resulting actions are caller responsibilities.

- [ ] **Step 2: Fetch and merge current main into the feature branch.**

~~~bash
git fetch --prune
git merge origin/main
~~~

Resolve every conflict in the feature branch and rerun the focused Gui tests.

- [ ] **Step 3: Select the next free additive minor and update versioned documentation.**

~~~bash
git tag --list 'v*' --sort=-v:refname | head
rg '<KhaozEngineVersion>' Directory.Build.props
~~~

If 18.35.0 remains current and v18.36.0 is free, use 18.36.0. Otherwise take the next free minor. The changelog first sentence is: KhaozEngine.Gui adds an interaction-anchored radial menu and an opaque source-target use context.

- [ ] **Step 4: Mark the design and index row complete with the resolved version.**

Keep detailed reference material in KhaozEngine.Gui/README.md and docs/USING-KHAOZENGINE.md. The design document retains rationale.

- [ ] **Step 5: Run all guards and Release verification.**

~~~bash
scripts/check-doc-versions.sh
scripts/check-dashes.sh --tree
scripts/check-prose.sh --tree
scripts/check-file-size.sh --tree
dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~RadialMenuGpuTests
dotnet test KhaozEngine.slnx -c Release
~~~

Expected: every command exits zero. GPU tests may report only normal environment-gated skips.

- [ ] **Step 6: Pack and commit the version batch.**

~~~bash
scripts/check-local-feed.sh
scripts/pack-local-feed.sh
engine_version=$(sed -n 's:.*<KhaozEngineVersion>\([^<]*\)</KhaozEngineVersion>.*:\1:p' Directory.Build.props)
git add KhaozEngine.Gui KhaozEngine.Gui.Tests KhaozEngine.Render.Tests README.md docs CHANGELOG.md Directory.Build.props
git commit -m "gui(${engine_version}): add radial menu and source-target use"
~~~

- [ ] **Step 7: Reconcile once more, fast-forward main, verify, and push.**

~~~bash
git fetch --prune
git merge origin/main
dotnet test KhaozEngine.slnx -c Release
git -C /Users/antonio/KhaozEngine merge --ff-only feature/radial-menu-source-target
git -C /Users/antonio/KhaozEngine push origin main
~~~

If main advanced after the branch merge, merge it into the feature branch and repeat verification before the fast-forward.

- [ ] **Step 8: Tag immediately because Grimhollow is waiting.**

From /Users/antonio/KhaozEngine:

~~~bash
scripts/tag-release.sh gui "add radial menu and source-target use"
release_version=$(sed -n 's:.*<KhaozEngineVersion>\([^<]*\)</KhaozEngineVersion>.*:\1:p' Directory.Build.props)
git push origin "v$release_version"
~~~

Verify the tag is annotated, matches Directory.Build.props, and starts the release workflow.

- [ ] **Step 9: Close the issue and clean the implementation worktree.**

~~~bash
gh issue close 854 --comment "Shipped in the tagged engine release. Grimhollow adoption continues in https://github.com/APKiwiOrg/Grimhollow/issues/155"
git -C /Users/antonio/KhaozEngine worktree remove /Users/antonio/KhaozEngine/.claude/worktrees/radial-menu-source-target
git -C /Users/antonio/KhaozEngine branch -d feature/radial-menu-source-target
git -C /Users/antonio/KhaozEngine fetch --prune
~~~

Expected: main and origin/main match, the implementation worktree and branch are gone, and issue #854 is closed.
