# Time-scale + Pause Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an engine-level `GameClock` (pause + time-scale) so games can freeze, slow, or speed up the simulation while UI, transitions, and notifications stay on real time.

**Architecture:** A new pure package `KhaozEngine.Time` holds `GameClock` (real vs scaled delta, orthogonal pause-with-memory, `Paused`/`Resumed` events). `ScreenManager` owns a `GameClock`, drives transitions on real dt, exposes ambient forwarders, and dispatches `OnPause`/`OnResume` virtuals to stacked screens. ECS is untouched - gameplay screens feed `world.Update(ScaledDeltaSeconds)` themselves. Default `TimeScale == 1` makes scaled dt identical to real dt, so the three pinned consumers are unaffected and SpaceGame's fixed-timestep lockstep stays deterministic.

**Tech Stack:** C# net10.0, MonoGame.Framework.DesktopGL 3.8 (for `GameTime`), xUnit.

---

## File Structure

- Create: `KhaozEngine.Time/KhaozEngine.Time.csproj` - new package, references MonoGame for `GameTime`.
- Create: `KhaozEngine.Time/GameClock.cs` - the `GameClock` type.
- Create: `KhaozEngine.Time/README.md` - package readme (packed, like other packages).
- Modify: `KhaozEngine.slnx` - register the new project.
- Modify: `KhaozEngine.Screens/KhaozEngine.Screens.csproj` - add ProjectReference to `.Time`.
- Modify: `KhaozEngine.Screens/GameScreen.cs:49-53` - add `OnPause`/`OnResume` virtuals + internal shims.
- Modify: `KhaozEngine.Screens/ScreenManager.cs:16-34,65-67` - clock field, two constructors, `Clock` + forwarders, advance clock in `Update`, dispatch hooks.
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` - add ProjectReference to `.Time`.
- Create: `KhaozEngine.Tests/GameClockTests.cs` - `GameClock` unit suite.
- Create: `KhaozEngine.Tests/ScreenManagerTimeTests.cs` - `ScreenManager` clock integration suite.
- Modify: `Directory.Build.props:9` - version 2.1.0 → 2.2.0.
- Modify: `CHANGELOG.md` - newest-first entry.
- Modify: `docs/CONSUMERS.md` - engine-version line + matrix Time column.

---

## Task 1: Scaffold `KhaozEngine.Time` package and wire it into the solution

Creates the package with a minimal compiling `GameClock` stub and references, so the solution and test project build before we TDD behavior in Task 2.

**Files:**
- Create: `KhaozEngine.Time/KhaozEngine.Time.csproj`
- Create: `KhaozEngine.Time/README.md`
- Create: `KhaozEngine.Time/GameClock.cs`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Screens/KhaozEngine.Screens.csproj`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

- [ ] **Step 1: Create the project file**

Create `KhaozEngine.Time/KhaozEngine.Time.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Time</PackageId>
    <Description>Game-agnostic time scaling + pause clock (GameClock): real vs scaled delta, slow-mo/fast-forward, pause/resume events.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.*" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the package README**

Create `KhaozEngine.Time/README.md`:

```markdown
# KhaozEngine.Time

A small, game-agnostic clock for pausing and time-scaling the simulation.

`GameClock` separates **real** delta time (UI, transitions, notifications) from
**scaled** delta time (gameplay, world). Set `TimeScale` for slow-mo (`< 1`),
normal (`1`), or fast-forward (`> 1`); `Pause()`/`Resume()` freeze the sim
without losing the intended speed. `Paused`/`Resumed` events fire on transitions.

```csharp
var clock = new GameClock();
clock.Update(gameTime);            // once per frame
world.Update(clock.ScaledDeltaSeconds);
clock.TimeScale = 0.5f;            // slow-mo
clock.Pause();                     // ScaledDeltaSeconds == 0; RealDeltaSeconds unchanged
```

Used standalone, or via `ScreenManager.Clock` in `KhaozEngine.Screens`.
```

- [ ] **Step 3: Create a minimal `GameClock` stub**

Create `KhaozEngine.Time/GameClock.cs`:

```csharp
using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Time;

/// <summary>
/// A game-agnostic clock that separates real delta time from a scaled simulation delta.
/// Set <see cref="TimeScale"/> for slow-mo/fast-forward and <see cref="Pause"/>/<see cref="Resume"/>
/// to freeze the sim while real time keeps running (UI, transitions, notifications).
/// </summary>
public sealed class GameClock
{
    /// <summary>Advance once per frame before consumers read the deltas.</summary>
    public void Update(GameTime gameTime) { }
}
```

- [ ] **Step 4: Register the project in the solution**

In `KhaozEngine.slnx`, add the Time project between the Tests and UI lines:

```xml
  <Project Path="KhaozEngine.Tests/KhaozEngine.Tests.csproj" />
  <Project Path="KhaozEngine.Time/KhaozEngine.Time.csproj" />
  <Project Path="KhaozEngine.UI/KhaozEngine.UI.csproj" />
```

- [ ] **Step 5: Reference `.Time` from `.Screens`**

In `KhaozEngine.Screens/KhaozEngine.Screens.csproj`, add the ProjectReference next to the existing Input reference:

```xml
    <ProjectReference Include="../KhaozEngine.Input/KhaozEngine.Input.csproj" />
    <ProjectReference Include="../KhaozEngine.Time/KhaozEngine.Time.csproj" />
```

- [ ] **Step 6: Reference `.Time` from the test project**

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add the ProjectReference after the Screens reference:

```xml
    <ProjectReference Include="../KhaozEngine.Screens/KhaozEngine.Screens.csproj" />
    <ProjectReference Include="../KhaozEngine.Time/KhaozEngine.Time.csproj" />
```

- [ ] **Step 7: Build the solution**

Run: `dotnet build KhaozEngine.slnx`
Expected: Build succeeded (new package compiles, existing projects unaffected).

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Time KhaozEngine.slnx KhaozEngine.Screens/KhaozEngine.Screens.csproj KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "Scaffold KhaozEngine.Time package (GameClock stub)"
```

---

## Task 2: Implement `GameClock` behavior (TDD)

**Files:**
- Test: `KhaozEngine.Tests/GameClockTests.cs`
- Modify: `KhaozEngine.Time/GameClock.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/GameClockTests.cs`:

```csharp
using System;
using KhaozEngine.Time;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class GameClockTests
{
    private static GameTime Frame(double dt) =>
        new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt));

    [Fact]
    public void DefaultsAreNormalSpeedNotPausedZeroDeltas()
    {
        var c = new GameClock();
        Assert.Equal(1f, c.TimeScale);
        Assert.False(c.IsPaused);
        Assert.Equal(0f, c.RealDeltaSeconds);
        Assert.Equal(0f, c.ScaledDeltaSeconds);
    }

    [Fact]
    public void UpdateAtNormalSpeedScaledEqualsReal()
    {
        var c = new GameClock();
        c.Update(Frame(0.5));
        Assert.Equal(0.5f, c.RealDeltaSeconds);
        Assert.Equal(0.5f, c.ScaledDeltaSeconds);
    }

    [Fact]
    public void FastForwardScalesSimButNotReal()
    {
        var c = new GameClock { TimeScale = 2f };
        c.Update(Frame(0.5));
        Assert.Equal(0.5f, c.RealDeltaSeconds);
        Assert.Equal(1.0f, c.ScaledDeltaSeconds);
    }

    [Fact]
    public void SlowMoScalesSimDown()
    {
        var c = new GameClock { TimeScale = 0.5f };
        c.Update(Frame(0.5));
        Assert.Equal(0.25f, c.ScaledDeltaSeconds);
        Assert.Equal(0.5f, c.RealDeltaSeconds);
    }

    [Fact]
    public void NegativeTimeScaleClampsToZero()
    {
        var c = new GameClock { TimeScale = -3f };
        Assert.Equal(0f, c.TimeScale);
    }

    [Fact]
    public void PauseZeroesScaledButNotRealDelta()
    {
        var c = new GameClock();
        c.Pause();
        c.Update(Frame(0.5));
        Assert.True(c.IsPaused);
        Assert.Equal(0f, c.ScaledDeltaSeconds);
        Assert.Equal(0.5f, c.RealDeltaSeconds);
    }

    [Fact]
    public void ResumeRestoresPriorTimeScale()
    {
        var c = new GameClock { TimeScale = 2f };
        c.Pause();
        c.Resume();
        c.Update(Frame(0.5));
        Assert.False(c.IsPaused);
        Assert.Equal(1.0f, c.ScaledDeltaSeconds);   // back to 2x, not 1x
    }

    [Fact]
    public void TimeScaleZeroReportsPaused()
    {
        var c = new GameClock { TimeScale = 0f };
        Assert.True(c.IsPaused);
    }

    [Fact]
    public void PausedEventFiresOnceOnTransitionNotPerFrame()
    {
        var c = new GameClock();
        int paused = 0, resumed = 0;
        c.Paused += () => paused++;
        c.Resumed += () => resumed++;

        c.Pause();
        c.Update(Frame(0.5));
        c.Update(Frame(0.5));
        Assert.Equal(1, paused);
        Assert.Equal(0, resumed);

        c.Resume();
        c.Update(Frame(0.5));
        Assert.Equal(1, paused);
        Assert.Equal(1, resumed);
    }

    [Fact]
    public void SettingTimeScaleToZeroFiresPausedEvent()
    {
        var c = new GameClock();
        int paused = 0;
        c.Paused += () => paused++;
        c.TimeScale = 0f;
        Assert.Equal(1, paused);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GameClockTests"`
Expected: FAIL - assertions fail (stub leaves deltas at 0, no `TimeScale`/`IsPaused`/`Pause` members compile error). If members are missing the build fails; that counts as the failing state.

- [ ] **Step 3: Implement `GameClock`**

Replace the contents of `KhaozEngine.Time/GameClock.cs`:

```csharp
using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Time;

/// <summary>
/// A game-agnostic clock that separates real delta time from a scaled simulation delta.
/// Set <see cref="TimeScale"/> for slow-mo (&lt;1), normal (1), or fast-forward (&gt;1), and
/// <see cref="Pause"/>/<see cref="Resume"/> to freeze the sim while real time keeps running
/// (UI, transitions, notifications). Pause is orthogonal to <see cref="TimeScale"/>: resuming
/// restores the intended speed. <see cref="Paused"/>/<see cref="Resumed"/> fire on transitions.
/// </summary>
public sealed class GameClock
{
    private float _timeScale = 1f;
    private bool _paused;
    private bool _wasPaused;   // last observed IsPaused, for edge-triggered events

    /// <summary>Simulation speed multiplier; clamped to &gt;= 0. 0 = paused, &lt;1 = slow-mo, &gt;1 = fast-forward.</summary>
    public float TimeScale
    {
        get => _timeScale;
        set { _timeScale = value < 0f ? 0f : value; RaiseIfChanged(); }
    }

    /// <summary>True when explicitly paused or <see cref="TimeScale"/> is 0.</summary>
    public bool IsPaused => _paused || _timeScale == 0f;

    /// <summary>Last frame's unscaled delta in seconds.</summary>
    public float RealDeltaSeconds { get; private set; }

    /// <summary>Last frame's simulation delta: <see cref="RealDeltaSeconds"/> * scale, or 0 when paused.</summary>
    public float ScaledDeltaSeconds { get; private set; }

    /// <summary>Fired when <see cref="IsPaused"/> transitions false -&gt; true.</summary>
    public event Action? Paused;

    /// <summary>Fired when <see cref="IsPaused"/> transitions true -&gt; false.</summary>
    public event Action? Resumed;

    /// <summary>Explicitly pause the simulation (independent of <see cref="TimeScale"/>).</summary>
    public void Pause() { _paused = true; RaiseIfChanged(); }

    /// <summary>Clear an explicit pause, restoring the current <see cref="TimeScale"/>.</summary>
    public void Resume() { _paused = false; RaiseIfChanged(); }

    /// <summary>Advance once per frame, before consumers read the deltas.</summary>
    public void Update(GameTime gameTime)
    {
        RealDeltaSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
        ScaledDeltaSeconds = IsPaused ? 0f : RealDeltaSeconds * _timeScale;
        RaiseIfChanged();
    }

    private void RaiseIfChanged()
    {
        bool now = IsPaused;
        if (now == _wasPaused) return;
        _wasPaused = now;
        if (now) Paused?.Invoke();
        else Resumed?.Invoke();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GameClockTests"`
Expected: PASS - all 11 tests green.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Time/GameClock.cs KhaozEngine.Tests/GameClockTests.cs
git commit -m "Implement GameClock: time-scale, pause-with-memory, transition events"
```

---

## Task 3: ScreenManager clock integration + GameScreen lifecycle hooks (TDD)

**Files:**
- Test: `KhaozEngine.Tests/ScreenManagerTimeTests.cs`
- Modify: `KhaozEngine.Screens/GameScreen.cs`
- Modify: `KhaozEngine.Screens/ScreenManager.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/ScreenManagerTimeTests.cs`:

```csharp
using System;
using KhaozEngine.Input;
using KhaozEngine.Screens;
using KhaozEngine.Time;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class LifecycleSpyScreen : GameScreen
{
    public int PauseCount;
    public int ResumeCount;

    public LifecycleSpyScreen(int order)
    {
        DrawOrder = order;
        PassUpdateThrough = true;
    }

    public override bool Update(GameTime gameTime, bool receivesInput) => false;
    public override void Draw(GameTime gameTime, SpriteBatch spriteBatch) { }
    protected override void OnPause() => PauseCount++;
    protected override void OnResume() => ResumeCount++;
}

public class ScreenManagerTimeTests
{
    private static GameTime Frame(double dt) =>
        new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt));

    [Fact]
    public void InjectedClockIsExposed()
    {
        var clock = new GameClock();
        var m = new ScreenManager(new InputManager(), clock);
        Assert.Same(clock, m.Clock);
    }

    [Fact]
    public void DefaultConstructorCreatesAClock()
    {
        var m = new ScreenManager(new InputManager());
        Assert.NotNull(m.Clock);
        Assert.False(m.IsPaused);
    }

    [Fact]
    public void ScaledDeltaReflectsTimeScaleAfterUpdate()
    {
        var m = new ScreenManager(new InputManager()) { TimeScale = 2f };
        m.Update(Frame(0.5));
        Assert.Equal(0.5f, m.RealDeltaSeconds);
        Assert.Equal(1.0f, m.ScaledDeltaSeconds);
    }

    [Fact]
    public void TransitionsAdvanceWhilePaused()
    {
        var m = new ScreenManager(new InputManager());
        var s = new LifecycleSpyScreen(0) { TransitionOnDuration = 1f };
        m.Add(s);
        Assert.Equal(ScreenState.TransitionOn, s.State);
        Assert.Equal(0f, s.TransitionAlpha);

        m.Clock.Pause();
        m.Update(Frame(0.5));   // real dt still flows to transitions
        Assert.Equal(0.5f, s.TransitionAlpha);
    }

    [Fact]
    public void PauseDispatchesOnPauseToAllScreens()
    {
        var m = new ScreenManager(new InputManager());
        var a = new LifecycleSpyScreen(0);
        var b = new LifecycleSpyScreen(10);
        m.Add(a); m.Add(b);

        m.Clock.Pause();
        Assert.Equal(1, a.PauseCount);
        Assert.Equal(1, b.PauseCount);

        m.Update(Frame(0.5));   // does not refire
        Assert.Equal(1, a.PauseCount);

        m.Clock.Resume();
        Assert.Equal(1, a.ResumeCount);
        Assert.Equal(1, b.ResumeCount);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ScreenManagerTimeTests"`
Expected: FAIL - `ScreenManager` has no `Clock`/`TimeScale`/`RealDeltaSeconds`/`ScaledDeltaSeconds`, no two-arg constructor, and `GameScreen` has no `OnPause`/`OnResume` (build errors count as failing).

- [ ] **Step 3: Add the lifecycle hooks to `GameScreen`**

In `KhaozEngine.Screens/GameScreen.cs`, after the `UnloadContent` method (line 53), add:

```csharp
    /// <summary>Called when the engine clock pauses (TimeScale hits 0 or Pause()). Override to react.</summary>
    protected virtual void OnPause() { }

    /// <summary>Called when the engine clock resumes from a paused state. Override to react.</summary>
    protected virtual void OnResume() { }

    // ScreenManager dispatches pause/resume; the virtuals stay protected for subclasses.
    internal void RaisePause() => OnPause();
    internal void RaiseResume() => OnResume();
```

- [ ] **Step 4: Add the clock to `ScreenManager`**

In `KhaozEngine.Screens/ScreenManager.cs`, add the using at the top (after the existing `using KhaozEngine.Input;` on line 3):

```csharp
using KhaozEngine.Time;
```

Replace the constructor on line 34:

```csharp
    /// <summary>Creates the manager around an <see cref="InputManager"/> with a fresh <see cref="GameClock"/>.</summary>
    public ScreenManager(InputManager input) => Input = input;
```

with the clock field, two constructors, the `Clock` property, and the ambient forwarders:

```csharp
    private readonly GameClock _clock;

    /// <summary>The pause / time-scale clock. Read <see cref="ScaledDeltaSeconds"/> for sim, <see cref="RealDeltaSeconds"/> for UI.</summary>
    public GameClock Clock => _clock;

    /// <summary>Creates the manager around an <see cref="InputManager"/> with a fresh <see cref="GameClock"/>.</summary>
    public ScreenManager(InputManager input) : this(input, new GameClock()) { }

    /// <summary>Creates the manager sharing an external <see cref="GameClock"/> (e.g. one driven elsewhere).</summary>
    public ScreenManager(InputManager input, GameClock clock)
    {
        Input = input;
        _clock = clock;
        _clock.Paused += DispatchPause;
        _clock.Resumed += DispatchResume;
    }

    /// <summary>True when the clock is paused (see <see cref="GameClock.IsPaused"/>).</summary>
    public bool IsPaused => _clock.IsPaused;

    /// <summary>Simulation speed multiplier (see <see cref="GameClock.TimeScale"/>).</summary>
    public float TimeScale { get => _clock.TimeScale; set => _clock.TimeScale = value; }

    /// <summary>Last frame's unscaled delta seconds (UI, transitions, notifications).</summary>
    public float RealDeltaSeconds => _clock.RealDeltaSeconds;

    /// <summary>Last frame's simulation delta seconds (gameplay, world); 0 while paused.</summary>
    public float ScaledDeltaSeconds => _clock.ScaledDeltaSeconds;
```

- [ ] **Step 5: Advance the clock in `Update` and dispatch hooks**

In `KhaozEngine.Screens/ScreenManager.cs`, change the start of `Update` (lines 65-67) from:

```csharp
    public void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
```

to:

```csharp
    public void Update(GameTime gameTime)
    {
        _clock.Update(gameTime);
        float dt = _clock.RealDeltaSeconds;   // transitions/UI run on real time, live while paused
```

Then add the dispatch helpers immediately after the `Update` method's closing brace (before `AdvanceTransition`):

```csharp
    private void DispatchPause()
    {
        foreach (GameScreen s in _screens.ToArray()) s.RaisePause();
    }

    private void DispatchResume()
    {
        foreach (GameScreen s in _screens.ToArray()) s.RaiseResume();
    }
```

- [ ] **Step 6: Run the new tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ScreenManagerTimeTests"`
Expected: PASS - all 5 tests green.

- [ ] **Step 7: Run the full test suite (no regressions)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS - existing `ScreenManagerTests` and all other suites still green (default `TimeScale==1`, `Zero` GameTime gives `RealDeltaSeconds==0`, identical to prior behavior).

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Screens/GameScreen.cs KhaozEngine.Screens/ScreenManager.cs KhaozEngine.Tests/ScreenManagerTimeTests.cs
git commit -m "ScreenManager owns GameClock; real-dt transitions + OnPause/OnResume dispatch"
```

---

## Task 4: Release 2.2.0 (version, changelog, consumers, local-feed)

Single commit per the release ritual in `CLAUDE.md`.

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: `docs/CONSUMERS.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, line 9, change:

```xml
    <Version>2.1.0</Version>
```

to:

```xml
    <Version>2.2.0</Version>
```

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert immediately below the `All notable changes...` intro line, above `## KhaozEngine 2.1.0`:

```markdown
## KhaozEngine 2.2.0

- New package **KhaozEngine.Time** with `GameClock`: separates real delta time (UI, transitions,
  notifications) from a scaled simulation delta. `TimeScale` gives slow-mo (`<1`), normal (`1`), and
  fast-forward (`>1`); `Pause()`/`Resume()` freeze the sim orthogonally to `TimeScale` (resume keeps the
  intended speed); `Paused`/`Resumed` events fire on transitions; `IsPaused` is true when paused or
  `TimeScale == 0`.
- **KhaozEngine.Screens**: `ScreenManager` now owns a `GameClock` (new `ScreenManager(InputManager, GameClock)`
  overload to share one), exposes `Clock`/`IsPaused`/`TimeScale`/`RealDeltaSeconds`/`ScaledDeltaSeconds`,
  drives transitions on real dt (so they stay live while paused), and dispatches new
  `GameScreen.OnPause()`/`OnResume()` virtuals to stacked screens on pause transitions.
- Additive and opt-in. Default `TimeScale == 1` makes scaled dt identical to today, so the existing
  consumers are unchanged. Gameplay reads `ScaledDeltaSeconds` (e.g. `world.Update(ScaledDeltaSeconds)`);
  UI/transitions/notifications keep using real time. SpaceGame's fixed-timestep lockstep never reads the
  scaled delta, so determinism is preserved. All packages bump to 2.2.0.
```

- [ ] **Step 3: Update CONSUMERS.md engine version and matrix**

In `docs/CONSUMERS.md`, change line 6:

```markdown
**Engine current version:** `2.1.0` (all packages share one version, set in `Directory.Build.props`).
```

to:

```markdown
**Engine current version:** `2.2.0` (all packages share one version, set in `Directory.Build.props`).
```

Add a `Time` column to the matrix (header row, separator row, and each consumer row keep `-` since none have adopted it yet):

```markdown
| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Time |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 2.1.0 | 2.1.0   | 2.1.0 | 2.1.0 | 2.1.0   | -    |
| Nullwake  | `Nullwake/Nullwake.Core`             | 2.0.0 | 2.0.0   | 2.0.0 | -     | -       | -    |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 2.0.0 | 2.0.0   | -     | 2.0.0 | -       | -    |
```

And update the footer line at the bottom:

```markdown
_Last verified: 2026-06-09 against engine 2.2.0._
```

- [ ] **Step 4: Pack to local-feed**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: Build succeeded; `KhaozEngine.Time.2.2.0.nupkg` plus 2.2.0 packages for the others appear in `local-feed/` (older versions remain).

- [ ] **Step 5: Verify the new package is in the feed**

Run: `ls local-feed/KhaozEngine.Time.2.2.0.nupkg && ls local-feed | grep 2.2.0`
Expected: lists `KhaozEngine.Time.2.2.0.nupkg` and the 2.2.0 nupkgs for Input/Screens/UI/Ecs/Content.

- [ ] **Step 6: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md
git commit -m "Release KhaozEngine 2.2.0 (add KhaozEngine.Time: pause + time-scale)"
git tag v2.2.0
```

> Do NOT push `main` or the tag until the user confirms. Pushing the `v*` tag triggers CI publish to GitHub Packages.

- [ ] **Step 7: Ping the user**

Report that 2.2.0 is built into `local-feed`, summarize the new API, and ask whether to push `main` + the `v2.2.0` tag, and confirm they're ready to bump Hardpoint to adopt it.

---

## Notes for the implementer

- `GameTime` for headless tests: `new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt))` - only `ElapsedGameTime` matters to the clock.
- Floating-point asserts in these tests use exact values because the inputs are exact binary fractions (0.5, 0.25, etc.). Don't add tolerances unless a test uses a non-exact value.
- Do not touch `KhaozEngine.Ecs` - it stays clock-agnostic; gameplay screens pass `ScaledDeltaSeconds` into `world.Update`.
- `local-feed/` is gitignored but must exist before `dotnet restore`/`pack` (`mkdir -p local-feed`).
```
