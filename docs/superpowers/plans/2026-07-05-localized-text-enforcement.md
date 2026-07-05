# LocalizedText compile-time localization enforcement - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make hardcoded player-facing UI text a compile error in KhaozEngine by introducing a `LocalizedText` value type that Gui sinks accept instead of `string`, plus a Roslyn analyzer that keeps the escape hatch honest.

**Architecture:** New value types (`StringId`, `LocalizedText`) + an ambient catalog holder (`LocalizationContext`) + two marker attributes live in `KhaozEngine.App`. `KhaozEngine.Gui` gains a `Gui -> App` project reference and grows `LocalizedText` overloads on every player-facing sink; the existing `string` overloads become `[Obsolete]` back-compat shims that delegate to the new overloads through `LocalizedText.Raw(...)` (so they stay behaviour-identical and warning-clean). A new `netstandard2.0` analyzer project (`KhaozEngine.Localization.Analyzers`) ships two warnings and flows to consumers via the Game2D/Game3D umbrellas. The Showcase sample becomes the worked adoption example.

**Tech Stack:** C# / net10.0, xUnit, Roslyn (`Microsoft.CodeAnalysis.CSharp`), .resx satellite resources, NuGet analyzer packaging.

## Global Constraints

- **Additive minor bump only.** Do NOT change the type or remove any existing public member. Add overloads; mark superseded members `[Obsolete]`. (SemVer: additive = minor.)
- **No em-dashes, en-dashes, or semicolons in any shipped prose** (CHANGELOG, READMEs, docs, XML comments, commit messages). Plain hyphens where a hyphen belongs are fine; semicolons in code are fine.
- **Every new behaviour ships with a headless test.** Engine runtime tests go in `KhaozEngine.Tests`; analyzer tests go in the new `KhaozEngine.Localization.Analyzers.Tests` project.
- **Lazy resolution:** `LocalizedText` stores id + args and re-resolves on every `Resolve()`. Never cache the resolved string in the value type.
- **The raw escape hatch must be greppable:** the literal token is `LocalizedText.Raw`.
- **Commit subjects:** conventional `area(scope): summary`. Use plain scopes (`localization`, `gui`, `analyzer`, `showcase`, `docs`) for per-item commits; only the final release/version-bump commit uses the new version as scope (e.g. `localization(9.34.0): ...`).
- **Build/test command:** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` for runtime tests; `dotnet test KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj` for analyzer tests. `mkdir -p local-feed` before any restore if missing.
- **Working dir:** the worktree at `/Users/antonio/KhaozEngine/.claude/worktrees/feature+localized-text-enforcement`. All paths below are relative to it.
- **Version at release is re-checked, not assumed.** Base is 9.33.0; the release task re-reads `main` + tags and takes the next FREE minor.

---

## File Structure

**New files (KhaozEngine.App):**
- `KhaozEngine.App/StringId.cs` - the localizable-key value type.
- `KhaozEngine.App/LocalizedText.cs` - the sink value type (localizable or raw).
- `KhaozEngine.App/LocalizationContext.cs` - ambient catalog holder.
- `KhaozEngine.App/LocalizationExemptAttribute.cs` - analyzer exemption marker.
- `KhaozEngine.App/LocalizationStringSinkAttribute.cs` - discouraged-sink marker.

**Modified (KhaozEngine.Gui, add `Gui -> App` ref):**
- `KhaozEngine.Gui/KhaozEngine.Gui.csproj` - add App project reference.
- `KhaozEngine.Gui/Label.cs`, `Button.cs`, `GuiDraw.cs`, `GuiSurface.cs`, `Tooltip.cs` - LocalizedText overloads + obsolete shims.
- `KhaozEngine.Gui/PopupPanel.cs` - route internal `DrawButton` calls through `LocalizedText.Raw` (no public API change).

**New analyzer projects:**
- `KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj` + `LocalizationAnalyzer.cs` (+ diagnostic descriptors).
- `KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj` + `AnalyzerHarness.cs` + `LocalizationAnalyzerTests.cs`.

**Modified (umbrellas + package config):**
- `Directory.Packages.props` - pin `Microsoft.CodeAnalysis.CSharp`.
- `KhaozEngine.Game2D/KhaozEngine.Game2D.csproj`, `KhaozEngine.Game3D/KhaozEngine.Game3D.csproj` - reference the analyzer.
- `KhaozEngine.slnx` - add the two analyzer projects.

**New/modified (Showcase worked example):**
- `KhaozEngine.Showcase/ShowcaseStrings.resx` + `ShowcaseStrings.cs` (StringId constants) + startup catalog wiring.
- `KhaozEngine.Showcase/RoomGui.cs`, `RoomMiniGame.cs` - migrate to LocalizedText.
- `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` - resx + analyzer reference.

**Modified (existing tests migrated off obsolete overloads):**
- `KhaozEngine.Tests/Gui/ButtonTests.cs`, `IconWidgetTests.cs`, `TooltipTests.cs`.

**Docs:**
- `CHANGELOG.md`, `README.md`, `KhaozEngine.App/README.md`, `KhaozEngine.Gui/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/ROADMAP.md`.

---

## Task 1: `StringId` value type (KhaozEngine.App)

**Files:**
- Create: `KhaozEngine.App/StringId.cs`
- Test: `KhaozEngine.Tests/App/StringIdTests.cs`

**Interfaces:**
- Produces: `readonly struct KhaozEngine.App.StringId : IEquatable<StringId>` with `string Key { get; }`, `StringId(string key)`, `static StringId Of(string key)`, `ToString() => Key`, value equality on `Key` (Ordinal). No implicit conversion from `string`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/App/StringIdTests.cs`:

```csharp
using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    public class StringIdTests
    {
        [Fact]
        public void Key_RoundTrips()
        {
            var id = new StringId("Menu.Play");
            Assert.Equal("Menu.Play", id.Key);
            Assert.Equal("Menu.Play", id.ToString());
        }

        [Fact]
        public void Of_IsEquivalentToCtor()
        {
            Assert.Equal(new StringId("A.B"), StringId.Of("A.B"));
        }

        [Fact]
        public void Equality_IsOrdinalOnKey()
        {
            Assert.Equal(new StringId("x"), new StringId("x"));
            Assert.NotEqual(new StringId("x"), new StringId("X"));
            Assert.True(new StringId("x").GetHashCode() == new StringId("x").GetHashCode());
        }

        [Fact]
        public void NullOrEmptyKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => new StringId(""));
            Assert.Throws<ArgumentNullException>(() => new StringId(null!));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~StringIdTests"`
Expected: FAIL - `StringId` does not exist (compile error).

- [ ] **Step 3: Implement**

Create `KhaozEngine.App/StringId.cs`:

```csharp
using System;

namespace KhaozEngine.App;

/// <summary>
/// A localization key: the typed handle a <see cref="LocalizedText"/> resolves against an
/// <see cref="IStringCatalog"/>. There is deliberately NO implicit conversion from <see cref="string"/> -
/// authoring a <see cref="StringId"/> is an explicit act (a constants class today, a generator later), so a
/// bare string literal can never slip into a player-facing sink.
/// </summary>
public readonly struct StringId : IEquatable<StringId>
{
    /// <summary>The catalog lookup key.</summary>
    public string Key { get; }

    /// <summary>Creates a key. The key must be non-empty.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public StringId(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Key = key;
    }

    /// <summary>Factory equivalent to the constructor, for a fluent call site.</summary>
    public static StringId Of(string key) => new(key);

    /// <inheritdoc />
    public bool Equals(StringId other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StringId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Key is null ? 0 : StringComparer.Ordinal.GetHashCode(Key);

    /// <summary>The raw key (for logging/debug).</summary>
    public override string ToString() => Key ?? "";

    public static bool operator ==(StringId a, StringId b) => a.Equals(b);
    public static bool operator !=(StringId a, StringId b) => !a.Equals(b);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~StringIdTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/StringId.cs KhaozEngine.Tests/App/StringIdTests.cs
git commit -m "localization: StringId value type (typed localization key, no implicit string conversion)"
```

---

## Task 2: `LocalizationContext` ambient catalog holder (KhaozEngine.App)

**Files:**
- Create: `KhaozEngine.App/LocalizationContext.cs`
- Test: `KhaozEngine.Tests/App/LocalizationContextTests.cs`

**Interfaces:**
- Consumes: existing `KhaozEngine.App.IStringCatalog`.
- Produces: `static class LocalizationContext` with `static IStringCatalog? Catalog { get; set; }`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/App/LocalizationContextTests.cs`:

```csharp
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    public class LocalizationContextTests
    {
        [Fact]
        public void Catalog_DefaultsNull_AndIsSettable()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = null;
                Assert.Null(LocalizationContext.Catalog);

                var fake = new DictionaryCatalog();
                LocalizationContext.Catalog = fake;
                Assert.Same(fake, LocalizationContext.Catalog);
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }

    // Minimal test catalog reused by later tests.
    internal sealed class DictionaryCatalog : IStringCatalog
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _map = new();
        public DictionaryCatalog Add(string key, string value) { _map[key] = value; return this; }
        public string Get(string key) => _map.TryGetValue(key, out var v) ? v : key;
        public string Format(string key, params object?[] args) => string.Format(Get(key), args);
        public bool TryGet(string key, out string value) { value = Get(key); return _map.ContainsKey(key); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationContextTests"`
Expected: FAIL - `LocalizationContext` does not exist.

- [ ] **Step 3: Implement**

Create `KhaozEngine.App/LocalizationContext.cs`:

```csharp
namespace KhaozEngine.App;

/// <summary>
/// The ambient <see cref="IStringCatalog"/> that <see cref="LocalizedText.Resolve()"/> reads when no catalog
/// is passed explicitly. An app sets this once at startup (there is no global <see cref="ServiceLocator"/>
/// singleton, and threading a catalog through every Gui draw call would be invasive). Null is legal - a
/// localizable <see cref="LocalizedText"/> then renders its key as a visible placeholder, never throwing.
/// Because <see cref="ResourceStringCatalog"/> reads <c>CurrentUICulture</c> live and <see cref="LocalizedText"/>
/// re-resolves on every access, a runtime locale switch shows up on the next draw with nothing to invalidate.
/// </summary>
public static class LocalizationContext
{
    /// <summary>The ambient catalog, or null when unset.</summary>
    public static IStringCatalog? Catalog { get; set; }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationContextTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/LocalizationContext.cs KhaozEngine.Tests/App/LocalizationContextTests.cs
git commit -m "localization: LocalizationContext ambient catalog holder"
```

---

## Task 3: `LocalizedText` value type (KhaozEngine.App)

**Files:**
- Create: `KhaozEngine.App/LocalizedText.cs`
- Test: `KhaozEngine.Tests/App/LocalizedTextTests.cs`

**Interfaces:**
- Consumes: `StringId`, `IStringCatalog`, `LocalizationContext` (all above), reuses `DictionaryCatalog` test helper from Task 2.
- Produces: `readonly struct LocalizedText` with `static implicit operator LocalizedText(StringId)`, `static LocalizedText Of(StringId id, params object?[] args)`, `static LocalizedText Raw(string text)`, `string Resolve(IStringCatalog? catalog)`, `string Resolve()`, `bool IsRaw`, `StringId Id`, `override string ToString()`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/App/LocalizedTextTests.cs`:

```csharp
using System.Globalization;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    public class LocalizedTextTests
    {
        [Fact]
        public void Raw_ResolvesLiterally_IgnoringCatalog()
        {
            var cat = new DictionaryCatalog().Add("v1.2", "SHOULD-NOT-USE");
            LocalizedText t = LocalizedText.Raw("v1.2");
            Assert.True(t.IsRaw);
            Assert.Equal("v1.2", t.Resolve(cat));
        }

        [Fact]
        public void StringId_ResolvesViaCatalog()
        {
            var cat = new DictionaryCatalog().Add("Menu.Play", "Play");
            LocalizedText t = new StringId("Menu.Play"); // implicit conversion
            Assert.False(t.IsRaw);
            Assert.Equal("Play", t.Resolve(cat));
        }

        [Fact]
        public void Of_WithArgs_UsesCatalogFormat()
        {
            var cat = new DictionaryCatalog().Add("Score.Fmt", "Score: {0}");
            LocalizedText t = LocalizedText.Of(new StringId("Score.Fmt"), 42);
            Assert.Equal("Score: 42", t.Resolve(cat));
        }

        [Fact]
        public void Localizable_NoCatalog_ReturnsKeyPlaceholder()
        {
            LocalizedText t = new StringId("Menu.Play");
            Assert.Equal("Menu.Play", t.Resolve(null));
        }

        [Fact]
        public void Default_ResolvesToEmpty()
        {
            LocalizedText t = default;
            Assert.Equal("", t.Resolve(null));
        }

        [Fact]
        public void Resolve_NoArg_UsesAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Play");
                LocalizedText t = new StringId("Menu.Play");
                Assert.Equal("Play", t.Resolve());
                Assert.Equal("Play", t.ToString());
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void LocaleSwitch_ReResolves()
        {
            // Swap the ambient catalog to model a locale change; the same LocalizedText value re-resolves.
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizedText t = new StringId("Menu.Play");
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Play");
                Assert.Equal("Play", t.Resolve());
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Jouer");
                Assert.Equal("Jouer", t.Resolve());
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizedTextTests"`
Expected: FAIL - `LocalizedText` does not exist.

- [ ] **Step 3: Implement**

Create `KhaozEngine.App/LocalizedText.cs`:

```csharp
namespace KhaozEngine.App;

/// <summary>
/// A piece of player-facing text that resolves lazily against the current locale. It is EITHER a localizable
/// <see cref="StringId"/> (with optional format args) OR a raw literal (<see cref="Raw"/>) for non-localizable
/// tokens - names, numbers, debug text. Gui sinks accept <see cref="LocalizedText"/> instead of
/// <see cref="string"/>, and the only implicit conversion is from <see cref="StringId"/> (never from
/// <see cref="string"/>), so a bare string literal at a sink fails to compile. The value stores the id + args
/// and re-resolves on every <see cref="Resolve()"/> - it never caches - so a runtime locale switch takes effect
/// on the next draw.
/// </summary>
public readonly struct LocalizedText
{
    private readonly StringId _id;
    private readonly object?[]? _args;
    private readonly string? _raw;
    private readonly bool _isRaw;

    private LocalizedText(StringId id, object?[]? args)
    {
        _id = id;
        _args = args;
        _raw = null;
        _isRaw = false;
    }

    private LocalizedText(string raw)
    {
        _id = default;
        _args = null;
        _raw = raw;
        _isRaw = true;
    }

    /// <summary>True when this is a raw literal (see <see cref="Raw"/>), false when it is a localizable key.</summary>
    public bool IsRaw => _isRaw;

    /// <summary>The underlying key when this is localizable (default when <see cref="IsRaw"/>).</summary>
    public StringId Id => _id;

    /// <summary>A localizable value from a key with no format args (implicit at the call site).</summary>
    public static implicit operator LocalizedText(StringId id) => new(id, null);

    /// <summary>A localizable value from a key with format args - resolved via <see cref="IStringCatalog.Format"/>.</summary>
    public static LocalizedText Of(StringId id, params object?[] args) => new(id, args);

    /// <summary>
    /// The escape hatch: text that is intentionally NOT localized (a proper name, a number, debug text). The
    /// literal token <c>LocalizedText.Raw</c> is greppable, and the analyzer flags it outside exempt/debug code.
    /// </summary>
    public static LocalizedText Raw(string text) => new(text ?? "");

    /// <summary>
    /// Resolve against an explicit catalog. Raw text returns verbatim. A localizable value resolves via
    /// <see cref="IStringCatalog.Get"/> (or <see cref="IStringCatalog.Format"/> when it has args); with a null
    /// catalog it returns the key as a visible placeholder. <c>default</c> resolves to the empty string.
    /// </summary>
    public string Resolve(IStringCatalog? catalog)
    {
        if (_isRaw) return _raw ?? "";
        if (_id.Key is null) return ""; // default(LocalizedText)
        if (catalog is null) return _id.Key;
        return _args is { Length: > 0 } ? catalog.Format(_id.Key, _args) : catalog.Get(_id.Key);
    }

    /// <summary>Resolve against the ambient <see cref="LocalizationContext.Catalog"/>.</summary>
    public string Resolve() => Resolve(LocalizationContext.Catalog);

    /// <summary>Convenience: resolves against the ambient catalog (for logs/debug).</summary>
    public override string ToString() => Resolve();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizedTextTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/LocalizedText.cs KhaozEngine.Tests/App/LocalizedTextTests.cs
git commit -m "localization: LocalizedText value type (lazy resolve, implicit from StringId only, Raw escape hatch)"
```

---

## Task 4: Marker attributes (KhaozEngine.App)

**Files:**
- Create: `KhaozEngine.App/LocalizationExemptAttribute.cs`
- Create: `KhaozEngine.App/LocalizationStringSinkAttribute.cs`
- Test: `KhaozEngine.Tests/App/LocalizationAttributesTests.cs`

**Interfaces:**
- Produces: `[AttributeUsage(...)] sealed class LocalizationExemptAttribute : Attribute` and `sealed class LocalizationStringSinkAttribute : Attribute`, both in `KhaozEngine.App`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/App/LocalizationAttributesTests.cs`:

```csharp
using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    public class LocalizationAttributesTests
    {
        [Fact]
        public void Exempt_TargetsAssemblyTypeMember()
        {
            var u = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(LocalizationExemptAttribute), typeof(AttributeUsageAttribute))!;
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Assembly));
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Class));
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Method));
        }

        [Fact]
        public void StringSink_TargetsMethodAndCtor()
        {
            var u = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(LocalizationStringSinkAttribute), typeof(AttributeUsageAttribute))!;
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Method));
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Constructor));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationAttributesTests"`
Expected: FAIL - attributes do not exist.

- [ ] **Step 3: Implement**

Create `KhaozEngine.App/LocalizationExemptAttribute.cs`:

```csharp
using System;

namespace KhaozEngine.App;

/// <summary>
/// Marks an assembly, type, or member as exempt from the localization analyzer's raw-text warning
/// (KELOC002): <see cref="LocalizedText.Raw"/> used anywhere inside the marked scope is intentional and the
/// analyzer stays silent. Use on debug overlays, tools, and sample chrome that legitimately are not localized.
/// </summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property |
    AttributeTargets.Field,
    Inherited = false, AllowMultiple = false)]
public sealed class LocalizationExemptAttribute : Attribute
{
}
```

Create `KhaozEngine.App/LocalizationStringSinkAttribute.cs`:

```csharp
using System;

namespace KhaozEngine.App;

/// <summary>
/// Marks a method or constructor as a discouraged raw-<see cref="string"/> player-facing sink. The
/// localization analyzer (KELOC001) flags CALLERS of any member carrying this attribute, so the engine's
/// obsolete string overloads - and any sink a game marks itself - are caught without hard-coding method names.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false, AllowMultiple = false)]
public sealed class LocalizationStringSinkAttribute : Attribute
{
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationAttributesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/LocalizationExemptAttribute.cs KhaozEngine.App/LocalizationStringSinkAttribute.cs KhaozEngine.Tests/App/LocalizationAttributesTests.cs
git commit -m "localization: [LocalizationExempt] + [LocalizationStringSink] marker attributes"
```

---

## Task 5: `Gui -> App` reference + `Label` widget LocalizedText overload

**Files:**
- Modify: `KhaozEngine.Gui/KhaozEngine.Gui.csproj` (add App reference)
- Modify: `KhaozEngine.Gui/Label.cs`
- Test: `KhaozEngine.Tests/Gui/LabelLocalizedTests.cs`

**Interfaces:**
- Consumes: `KhaozEngine.App.LocalizedText`, `LocalizationContext`, `[LocalizationStringSink]`.
- Produces: `Label(Rect, LocalizedText, SpriteFont)` ctor; `public LocalizedText Content` field; `Resolved` accessor (`string Resolved => Content.Resolve()`) for headless assertions; `[Obsolete]` string ctor + `[Obsolete]` string `Text` shim property.

- [ ] **Step 1: Add the App project reference**

In `KhaozEngine.Gui/KhaozEngine.Gui.csproj`, inside the first `<ItemGroup>` with the ProjectReferences, add:

```xml
    <ProjectReference Include="../KhaozEngine.App/KhaozEngine.App.csproj" />
```

- [ ] **Step 2: Write the failing test**

Create `KhaozEngine.Tests/Gui/LabelLocalizedTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class LabelLocalizedTests
    {
        static readonly Rect R = new(0, 0, 100, 20);

        [Fact]
        public void LocalizedCtor_ResolvesAtAccess()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new App.DictionaryCatalog().Add("Menu.Play", "Play");
                var label = new Label(R, new StringId("Menu.Play"), null!);
                Assert.Equal("Play", label.Resolved);

                LocalizationContext.Catalog = new App.DictionaryCatalog().Add("Menu.Play", "Jouer");
                Assert.Equal("Jouer", label.Resolved); // re-resolves, not cached
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void RawContent_ResolvesLiterally()
        {
            var label = new Label(R, LocalizedText.Raw("v1.2"), null!);
            Assert.Equal("v1.2", label.Resolved);
        }
    }
}
```

Note: `App.DictionaryCatalog` is the internal test catalog from Task 2 (namespace `KhaozEngine.Tests.App`); reference it via a `using KhaozEngine.Tests.App;` alias or move `DictionaryCatalog` to a shared `KhaozEngine.Tests/App/DictionaryCatalog.cs` file (do the move now if not already, updating Task 2's test to drop its inline copy).

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LabelLocalizedTests"`
Expected: FAIL - no `LocalizedText` ctor / no `Resolved`.

- [ ] **Step 4: Implement**

Rewrite `KhaozEngine.Gui/Label.cs` so the widget stores a `LocalizedText` (single source of truth) and the string members become obsolete shims:

```csharp
using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A non-interactive text widget: draws <see cref="Content"/> (resolved against the current locale) in
    /// <see cref="Font"/> aligned within <see cref="Bounds"/>, optionally word-wrapped. Pure presentation over
    /// the (tested) <see cref="TextLayout"/> helpers; the text re-resolves each <see cref="Draw"/>.
    /// </summary>
    public sealed class Label
    {
        public Rect Bounds;
        /// <summary>The (lazily resolved) label text.</summary>
        public LocalizedText Content;
        public SpriteFont Font;
        public Vector4 Color = Vector4.One;
        public TextAlign Align = TextAlign.Left;
        /// <summary>When true, the text word-wraps to <see cref="Bounds"/>.Width; otherwise it draws on one line.</summary>
        public bool Wrap;
        /// <summary>When true, a single (unwrapped) line is centered vertically within <see cref="Bounds"/>.</summary>
        public bool VerticalCenter = true;

        /// <summary>Create a label from localized text.</summary>
        public Label(Rect bounds, LocalizedText text, SpriteFont font)
        {
            Bounds = bounds; Content = text; Font = font;
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public Label(Rect bounds, string text, SpriteFont font)
            : this(bounds, LocalizedText.Raw(text), font) { }

        /// <summary>Obsolete shim for the former string field.</summary>
        [Obsolete("Use Content (LocalizedText). Setting Text stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string Text
        {
            get => Content.Resolve();
            set => Content = LocalizedText.Raw(value);
        }

        /// <summary>The current resolved text (for tests / measurement).</summary>
        public string Resolved => Content.Resolve();

        /// <summary>Draw the label's text into <see cref="Bounds"/>.</summary>
        public void Draw(SpriteBatch batch)
        {
            string text = Content.Resolve();
            if (Wrap)
            {
                TextLayout.DrawWrapped(batch, Font, text, new Vector2(Bounds.X, Bounds.Y), Bounds.Width, Align, (KhaozEngine.Primitives.Color)Color);
                return;
            }
            float y = VerticalCenter ? Bounds.Y + (Bounds.Height - Font.LineHeight) * 0.5f : Bounds.Y;
            TextLayout.DrawAligned(batch, Font, text, Bounds.X, Bounds.Width, y, Align, (KhaozEngine.Primitives.Color)Color);
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes; commit**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LabelLocalizedTests"`
Expected: PASS.

```bash
git add KhaozEngine.Gui/KhaozEngine.Gui.csproj KhaozEngine.Gui/Label.cs KhaozEngine.Tests/Gui/LabelLocalizedTests.cs KhaozEngine.Tests/App/DictionaryCatalog.cs KhaozEngine.Tests/App/LocalizationContextTests.cs
git commit -m "gui: Label widget takes LocalizedText (Gui->App ref); obsolete string ctor/field"
```

---

## Task 6: `Button` widget LocalizedText overload

**Files:**
- Modify: `KhaozEngine.Gui/Button.cs`
- Test: `KhaozEngine.Tests/Gui/ButtonLocalizedTests.cs`

**Interfaces:**
- Produces: `Button(Rect, LocalizedText, SpriteFont, Action?)` ctor; `public LocalizedText Content`; `string Resolved => Content.Resolve()`; `[Obsolete]` string ctor + `[Obsolete]` string `Label` shim property. `Draw` resolves `Content` and passes it to `GuiDraw.DrawButton` (LocalizedText overload from Task 7).

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Gui/ButtonLocalizedTests.cs`:

```csharp
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class ButtonLocalizedTests
    {
        static readonly Rect R = new(0, 0, 80, 30);

        [Fact]
        public void LocalizedCtor_ResolvesLabel()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Go", "Go");
                var b = new Button(R, new StringId("Menu.Go"), null!);
                Assert.Equal("Go", b.Resolved);
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }
}
```

- [ ] **Step 2: Run - FAIL (no LocalizedText ctor / Resolved).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ButtonLocalizedTests"`

- [ ] **Step 3: Implement**

Edit `KhaozEngine.Gui/Button.cs`: add `using System;` (present) and `using KhaozEngine.App;`. Replace the `public string Label;` field and the ctor with:

```csharp
        public Rect Bounds;
        /// <summary>The (lazily resolved) button caption.</summary>
        public LocalizedText Content;
        public SpriteFont Font;
        public Action? OnClick;
```

(remove `public string Label;`) and constructors:

```csharp
        /// <summary>Create a button from localized text.</summary>
        public Button(Rect bounds, LocalizedText label, SpriteFont font, Action? onClick = null)
        {
            Bounds = bounds; Content = label; Font = font; OnClick = onClick;
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public Button(Rect bounds, string label, SpriteFont font, Action? onClick = null)
            : this(bounds, LocalizedText.Raw(label), font, onClick) { }

        /// <summary>Obsolete shim for the former string field.</summary>
        [Obsolete("Use Content (LocalizedText). Setting Label stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string Label
        {
            get => Content.Resolve();
            set => Content = LocalizedText.Raw(value);
        }

        /// <summary>The current resolved caption (for tests / measurement).</summary>
        public string Resolved => Content.Resolve();
```

Update `Draw` to pass the LocalizedText:

```csharp
        public void Draw(SpriteBatch batch, Texture2D white) =>
            GuiDraw.DrawButton(batch, white, Font, Bounds, Content, Style, Enabled, Selected, _hover, _press);
```

- [ ] **Step 4: Run - PASS. (Depends on Task 7's `DrawButton(LocalizedText)` overload; if implementing strictly in order, temporarily call `GuiDraw.DrawButton(batch, white, Font, Bounds, Content.Resolve(), ...)` until Task 7 lands, then switch. Prefer doing Task 7 before this step's final compile.)**

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/Button.cs KhaozEngine.Tests/Gui/ButtonLocalizedTests.cs
git commit -m "gui: Button widget takes LocalizedText; obsolete string ctor/field"
```

---

## Task 7: `GuiDraw.DrawButton` LocalizedText overload

**Files:**
- Modify: `KhaozEngine.Gui/GuiDraw.cs`

**Interfaces:**
- Produces: `static void DrawButton(SpriteBatch, Texture2D, SpriteFont, Rect, LocalizedText label, in GuiStyle, bool enabled, bool selected, bool hover, bool press)` (real impl); `[Obsolete]`+`[LocalizationStringSink]` string overload delegating via `LocalizedText.Raw`.

- [ ] **Step 1: Implement (covered by Button/GuiSurface tests; no separate unit test needed for the pure draw shim)**

In `KhaozEngine.Gui/GuiDraw.cs`, add `using System;` and `using KhaozEngine.App;` at the top. Replace the existing `DrawButton(...string label...)` method (lines ~216-235) with the LocalizedText real impl plus an obsolete string shim:

```csharp
        public static void DrawButton(SpriteBatch batch, Texture2D white, SpriteFont font, Rect rect, LocalizedText label,
            in GuiStyle style, bool enabled, bool selected, bool hover, bool press)
        {
            Vector4 fill = !enabled ? style.DisabledFill
                : selected ? style.SelectedFill
                : press ? style.Press
                : hover ? style.Hover
                : style.Fill;
            Vector4 border = selected ? style.SelectedBorder : style.Border;
            Vector4 text = enabled ? style.Text : style.DisabledText;

            if (hover && enabled) HoverGlow(batch, white, rect, style);
            FillStyled(batch, white, rect, style, fill, border);

            string s = label.Resolve();
            Vector2 size = font.Measure(s);
            var pos = new Vector2(
                rect.X + (rect.Width - size.X) * 0.5f,
                rect.Y + (rect.Height - font.LineHeight) * 0.5f);
            batch.DrawString(font, s, pos, (Color)text);
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/> label.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public static void DrawButton(SpriteBatch batch, Texture2D white, SpriteFont font, Rect rect, string label,
            in GuiStyle style, bool enabled, bool selected, bool hover, bool press) =>
            DrawButton(batch, white, font, rect, LocalizedText.Raw(label), style, enabled, selected, hover, press);
```

- [ ] **Step 2: Build to verify Gui compiles**

Run: `dotnet build KhaozEngine.Gui/KhaozEngine.Gui.csproj`
Expected: Build succeeds (Button.Draw now resolves against the LocalizedText overload).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Gui/GuiDraw.cs
git commit -m "gui: GuiDraw.DrawButton takes LocalizedText; obsolete string overload delegates via Raw"
```

---

## Task 8: `GuiSurface` LocalizedText overloads (Label x2, Button x2, StatChip)

**Files:**
- Modify: `KhaozEngine.Gui/GuiSurface.cs`
- Test: `KhaozEngine.Tests/Gui/GuiSurfaceLocalizedTests.cs`

**Interfaces:**
- Produces LocalizedText overloads for: `Label(SpriteFont, LocalizedText, Vector2, Vector4)`, `Label(SpriteFont, Rect, LocalizedText, Vector4, GuiAlign)`, `Button(SpriteFont, Rect, LocalizedText)`, `Button(SpriteFont, Rect, LocalizedText, GuiStyle, bool, bool)`, `StatChip(Rect, string iconId, LocalizedText label, LocalizedText value, SpriteFont, GuiStyle)`. Each existing string method becomes `[Obsolete]`+`[LocalizationStringSink]` delegating via `LocalizedText.Raw`. `IconButton` is left unchanged (its string is an icon-atlas key).

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Gui/GuiSurfaceLocalizedTests.cs`. GuiSurface supports a null `SpriteBatch` for headless use, but the LocalizedText overloads with a null batch early-return without resolving. To assert resolution headlessly, this test drives a `DictionaryCatalog` and verifies the overloads exist and return the right click semantics; text-resolution correctness is covered by the LocalizedText/DrawButton unit tests. Keep this test minimal - it guards the overloads compile and the click path is intact:

```csharp
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class GuiSurfaceLocalizedTests
    {
        [Fact]
        public void Button_LocalizedOverload_ClickSemanticsIntact()
        {
            var ui = new GuiSurface(null!);
            var pointer = new Pointer();
            ui.Begin(null, pointer); // headless
            bool clicked = ui.Button(null!, new Rect(0, 0, 50, 20), new StringId("Any"));
            Assert.False(clicked); // no tap this frame
        }
    }
}
```

- [ ] **Step 2: Run - FAIL (no LocalizedText Button overload).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GuiSurfaceLocalizedTests"`

- [ ] **Step 3: Implement**

In `KhaozEngine.Gui/GuiSurface.cs` add `using System;` and `using KhaozEngine.App;`. For each sink, keep the body on the LocalizedText overload and make the string one an obsolete shim. Concretely:

`Label(font, text, pos, color)` - replace the existing string method with:

```csharp
        public void Label(SpriteFont font, LocalizedText text, Vector2 pos, Vector4 color)
        {
            if (_batch is null) return;
            _batch.DrawString(font, text.Resolve(), pos, (Color)color);
        }

        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Label(SpriteFont font, string text, Vector2 pos, Vector4 color) =>
            Label(font, LocalizedText.Raw(text), pos, color);
```

`Label(font, rect, text, color, align)` - replace with a LocalizedText real impl (resolve once, then the existing measure/align/draw), plus an obsolete string shim delegating via `LocalizedText.Raw(text)`:

```csharp
        public void Label(SpriteFont font, Rect rect, LocalizedText text, Vector4 color, GuiAlign align = GuiAlign.Center)
        {
            if (_batch is null) return;
            const float pad = 6f;
            string s = text.Resolve();
            Vector2 size = font.Measure(s);
            float x = align switch
            {
                GuiAlign.Left => rect.X + pad,
                GuiAlign.Right => rect.Right - size.X - pad,
                _ => rect.X + (rect.Width - size.X) * 0.5f,
            };
            float y = rect.Y + (rect.Height - font.LineHeight) * 0.5f;
            _batch.DrawString(font, s, new Vector2(x, y), (Color)color);
        }

        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Label(SpriteFont font, Rect rect, string text, Vector4 color, GuiAlign align = GuiAlign.Center) =>
            Label(font, rect, LocalizedText.Raw(text), color, align);
```

`Button(font, rect, label)` and `Button(font, rect, label, style, enabled, selected)` - the second one holds the real impl; both get LocalizedText overloads and obsolete string shims:

```csharp
        public bool Button(SpriteFont font, Rect rect, LocalizedText label) =>
            Button(font, rect, label, Style);

        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public bool Button(SpriteFont font, Rect rect, string label) =>
            Button(font, rect, LocalizedText.Raw(label), Style);

        public bool Button(SpriteFont font, Rect rect, LocalizedText label, GuiStyle style, bool enabled = true, bool selected = false)
        {
            _blocked.Add(rect);
            Pointer p = _pointer;
            bool clicked = enabled && p.IsTapIn(rect);
            bool hovering = enabled && p.IsHoveringIn(rect);
            if (hovering) _hoveredRect = rect;
            if (_batch is null) return clicked;
            bool pressing = p.IsPressingIn(rect);
            GuiDraw.DrawButton(_batch, _white, font, rect, label, style, enabled, selected, hovering, pressing);
            return clicked;
        }

        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public bool Button(SpriteFont font, Rect rect, string label, GuiStyle style, bool enabled = true, bool selected = false) =>
            Button(font, rect, LocalizedText.Raw(label), style, enabled, selected);
```

`StatChip(rect, iconId, label, value, font, style)` - LocalizedText for `label` and `value` (values are frequently `Raw` numbers); obsolete string shim:

```csharp
        public void StatChip(Rect rect, string iconId, LocalizedText label, LocalizedText value, SpriteFont font, GuiStyle style)
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
            string lbl = label.Resolve();
            string val = value.Resolve();
            string text = string.IsNullOrEmpty(val) ? lbl : $"{lbl}  {val}";
            _batch.DrawString(font, text, new Vector2(textX, ty), (Color)style.Text);
        }

        [Obsolete("Pass LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void StatChip(Rect rect, string iconId, string label, string value, SpriteFont font, GuiStyle style) =>
            StatChip(rect, iconId, LocalizedText.Raw(label), LocalizedText.Raw(value), font, style);
```

Leave `IconButton(Rect, string iconId, ...)` UNCHANGED (icon-atlas key, not player text). Update the `<see cref="Button(SpriteFont, Rect, string)"/>` docs in the class summary if the compiler warns about ambiguous cref (it will still resolve to the obsolete overload; acceptable).

- [ ] **Step 4: Run - PASS.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GuiSurfaceLocalizedTests"`

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/GuiSurface.cs KhaozEngine.Tests/Gui/GuiSurfaceLocalizedTests.cs
git commit -m "gui: GuiSurface Label/Button/StatChip take LocalizedText; obsolete string overloads"
```

---

## Task 9: `Tooltip` LocalizedText Show overloads + `TooltipLine.Of`

**Files:**
- Modify: `KhaozEngine.Gui/Tooltip.cs`
- Test: `KhaozEngine.Tests/Gui/TooltipLocalizedTests.cs`

**Interfaces:**
- Produces: `TooltipLine.Of(LocalizedText text, Vector4 color)` (resolves at build); `Show(LocalizedText title, IReadOnlyList<TooltipLine> lines, Vector2 anchor)`; `Show(LocalizedText title, LocalizedText titleRight, IReadOnlyList<TooltipLine> lines, Vector2 anchor)`. The two string `Show` overloads become `[Obsolete]`+`[LocalizationStringSink]` delegating via `LocalizedText.Raw`. The `TooltipLine(string, Vector4)` record ctor stays (low-level, holds already-resolved text).

Note (documented deviation from strict lazy): tooltip titles/lines resolve at `Show`/`Of` time, not at `Draw`. Tooltip content is rebuilt each hover, so this is the pragmatic resolution point.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Gui/TooltipLocalizedTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class TooltipLocalizedTests
    {
        [Fact]
        public void Of_ResolvesLineTextViaAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Tip.Body", "Body text");
                var line = TooltipLine.Of(new StringId("Tip.Body"), Vector4.One);
                Assert.Equal("Body text", line.Text);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Of_Raw_ResolvesLiterally()
        {
            var line = TooltipLine.Of(LocalizedText.Raw("42%"), Vector4.One);
            Assert.Equal("42%", line.Text);
        }
    }
}
```

- [ ] **Step 2: Run - FAIL (no `TooltipLine.Of`).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TooltipLocalizedTests"`

- [ ] **Step 3: Implement**

In `KhaozEngine.Gui/Tooltip.cs` add `using KhaozEngine.App;`. Add the factory to `TooltipLine`:

```csharp
    /// <summary>A single line of text in a <see cref="Tooltip"/>.</summary>
    public readonly record struct TooltipLine(string Text, Vector4 Color)
    {
        /// <summary>Build a line from localized text (resolved now against the ambient catalog).</summary>
        public static TooltipLine Of(LocalizedText text, Vector4 color) => new(text.Resolve(), color);
    }
```

Add LocalizedText `Show` overloads and obsolete the string ones. Replace the two `Show(string ...)` methods:

```csharp
        /// <summary>Show with a localized title + body lines, anchored near <paramref name="anchor"/> (pixels).</summary>
        public void Show(LocalizedText title, IReadOnlyList<TooltipLine> lines, Vector2 anchor) =>
            Show(title, LocalizedText.Raw(""), lines, anchor);

        /// <summary>Show with a two-column localized title + body lines.</summary>
        public void Show(LocalizedText title, LocalizedText titleRight, IReadOnlyList<TooltipLine> lines, Vector2 anchor)
        {
            _title = title.Resolve() ?? "";
            _titleRight = titleRight.Resolve() ?? "";
            _lines.Clear();
            for (int i = 0; i < lines.Count; i++) _lines.Add(lines[i]);
            _anchor = anchor;
            IsVisible = true;
            _showedThisFrame = true;
        }

        [Obsolete("Pass a LocalizedText title; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...).")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Show(string title, IReadOnlyList<TooltipLine> lines, Vector2 anchor) =>
            Show(LocalizedText.Raw(title), LocalizedText.Raw(""), lines, anchor);

        [Obsolete("Pass a LocalizedText title; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...).")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public void Show(string title, string titleRight, IReadOnlyList<TooltipLine> lines, Vector2 anchor) =>
            Show(LocalizedText.Raw(title), LocalizedText.Raw(titleRight), lines, anchor);
```

Leave `ComputeBounds` (takes `string title` for pure layout) UNCHANGED - it is not a player-facing authoring sink; titles reach it already resolved.

- [ ] **Step 4: Run - PASS.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TooltipLocalizedTests"`

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/Tooltip.cs KhaozEngine.Tests/Gui/TooltipLocalizedTests.cs
git commit -m "gui: Tooltip LocalizedText Show overloads + TooltipLine.Of; obsolete string Show"
```

---

## Task 10: Route `PopupPanel` internal DrawButton calls through Raw

**Files:**
- Modify: `KhaozEngine.Gui/PopupPanel.cs`

**Interfaces:** No public API change. PopupPanel keeps its `string` text properties (migrating them would be a breaking type change; out of scope for an additive minor). Its private helper that calls `GuiDraw.DrawButton(...string...)` switches to the LocalizedText overload so the engine build stays warning-clean.

- [ ] **Step 1: Implement**

In `KhaozEngine.Gui/PopupPanel.cs` add `using KhaozEngine.App;`. At the private `DrawButton` helper (around line 220), change the `GuiDraw.DrawButton` call to wrap the text:

```csharp
            GuiDraw.DrawButton(batch, white, BodyFont, r, LocalizedText.Raw(text), ButtonStyle(color), enabled,
```

(only the `text` argument changes to `LocalizedText.Raw(text)`; the rest of the call is unchanged).

- [ ] **Step 2: Build to verify no CS0618 from PopupPanel**

Run: `dotnet build KhaozEngine.Gui/KhaozEngine.Gui.csproj -warnaserror:CS0618`
Expected: Build succeeds (no obsolete-usage warnings inside Gui).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Gui/PopupPanel.cs
git commit -m "gui: PopupPanel routes internal DrawButton through LocalizedText.Raw (no public API change)"
```

---

## Task 11: Analyzer project + KELOC001 (string-sink caller)

**Files:**
- Modify: `Directory.Packages.props` (pin `Microsoft.CodeAnalysis.CSharp`)
- Create: `KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj`
- Create: `KhaozEngine.Localization.Analyzers/LocalizationDiagnostics.cs`
- Create: `KhaozEngine.Localization.Analyzers/LocalizationAnalyzer.cs`
- Create: `KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj`
- Create: `KhaozEngine.Localization.Analyzers.Tests/AnalyzerHarness.cs`
- Create: `KhaozEngine.Localization.Analyzers.Tests/LocalizationAnalyzerTests.cs`
- Modify: `KhaozEngine.slnx` (add both projects)

**Interfaces:**
- Produces: `KELOC001` diagnostic id (Warning, category `Localization`) fired on invocations of a method/ctor marked `KhaozEngine.App.LocalizationStringSinkAttribute`.

- [ ] **Step 1: Pin the compiler package**

In `Directory.Packages.props`, add near the other `PackageVersion` entries:

```xml
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
```

(If restore reports 4.14.0 unavailable, use the highest 4.x that restores against the installed SDK; the analyzer only needs `DiagnosticAnalyzer`/`OperationAnalysisContext`, stable since 3.x.)

- [ ] **Step 2: Create the analyzer project**

Create `KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Localization.Analyzers</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <DevelopmentDependency>false</DevelopmentDependency>
    <Description>Roslyn analyzer that flags hardcoded player-facing UI text (KELOC001) and LocalizedText.Raw usage outside exempt/debug code (KELOC002) for KhaozEngine. Flows to consumers via the Game2D/Game3D umbrellas. Ships as warnings; a repo can raise either to error.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <!-- Ship the analyzer assembly in the analyzers path so it runs in consuming projects. -->
    <None Include="$(OutputPath)\netstandard2.0\$(AssemblyName).dll" Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

Create `KhaozEngine.Localization.Analyzers/README.md`:

```markdown
# KhaozEngine.Localization.Analyzers

Roslyn analyzer enforcing KhaozEngine's LocalizedText localization contract.

- **KELOC001** (Warning): player-facing text passed as a raw string to a `[LocalizationStringSink]`-marked
  method or constructor. Use a `StringId` (localizable) or `LocalizedText.Raw(...)` (non-localizable).
- **KELOC002** (Warning): `LocalizedText.Raw(...)` used outside code marked `[LocalizationExempt]` or DEBUG
  conditional. Confirm the text is intentionally non-localizable, or mark the scope exempt.

Raise to error in a consumer `.editorconfig`:

    dotnet_diagnostic.KELOC001.severity = error
    dotnet_diagnostic.KELOC002.severity = error

Flows automatically to any project referencing the `KhaozEngine.Game2D` or `KhaozEngine.Game3D` umbrella.
```

- [ ] **Step 3: Diagnostics descriptors**

Create `KhaozEngine.Localization.Analyzers/LocalizationDiagnostics.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace KhaozEngine.Localization.Analyzers;

internal static class LocalizationDiagnostics
{
    public const string Category = "Localization";

    public static readonly DiagnosticDescriptor RawStringSink = new(
        id: "KELOC001",
        title: "Player-facing text passed as a raw string",
        messageFormat: "'{0}' takes player-facing text; pass a StringId or LocalizedText.Raw(...) instead of a raw string",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A raw string at a player-facing sink bypasses localization. Use a StringId (localizable) or LocalizedText.Raw(...) for non-localizable text.");

    public static readonly DiagnosticDescriptor RawOutsideExempt = new(
        id: "KELOC002",
        title: "LocalizedText.Raw outside exempt or debug code",
        messageFormat: "LocalizedText.Raw bypasses localization; confirm the text is intentionally non-localizable or mark the scope [LocalizationExempt]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "LocalizedText.Raw is the non-localizable escape hatch. Outside [LocalizationExempt] scopes or DEBUG-conditional code its every use should be a deliberate, reviewed decision.");
}
```

- [ ] **Step 4: Write the failing analyzer test (KELOC001)**

Create `KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsPublishable>false</IsPublishable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj" />
  </ItemGroup>
</Project>
```

Create `KhaozEngine.Localization.Analyzers.Tests/AnalyzerHarness.cs` (hand-rolled harness - no external testing package, so no version churn):

```csharp
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.Localization.Analyzers.Tests;

internal static class AnalyzerHarness
{
    // Minimal stand-ins for the App attributes and LocalizedText so test snippets compile without
    // referencing KhaozEngine.App (keeps the analyzer test project dependency-free).
    private const string Stubs = @"
namespace KhaozEngine.App {
    using System;
    [AttributeUsage(AttributeTargets.All)] public sealed class LocalizationExemptAttribute : Attribute {}
    [AttributeUsage(AttributeTargets.Method|AttributeTargets.Constructor)] public sealed class LocalizationStringSinkAttribute : Attribute {}
    public readonly struct StringId { public StringId(string k){} }
    public readonly struct LocalizedText {
        public static implicit operator LocalizedText(StringId id) => default;
        public static LocalizedText Raw(string s) => default;
    }
}
";

    public static async Task<ImmutableArray<Diagnostic>> Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var stubTree = CSharpSyntaxTree.ParseText(Stubs);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAsm",
            new[] { tree, stubTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new LocalizationAnalyzer()));
        var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diags.Where(d => d.Id.StartsWith("KELOC")).ToImmutableArray();
    }
}
```

Create `KhaozEngine.Localization.Analyzers.Tests/LocalizationAnalyzerTests.cs` with the KELOC001 cases:

```csharp
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Localization.Analyzers.Tests;

public class LocalizationAnalyzerTests
{
    [Fact]
    public async Task KELOC001_FiresOnStringSinkCall()
    {
        var src = @"
using KhaozEngine.App;
class Sink { [LocalizationStringSink] public static void Label(string s){} }
class C { void M(){ Sink.Label(""hi""); } }
";
        var diags = await AnalyzerHarness.Run(src);
        Assert.Contains(diags, d => d.Id == "KELOC001");
    }

    [Fact]
    public async Task KELOC001_SilentOnLocalizedOverload()
    {
        var src = @"
using KhaozEngine.App;
class Sink {
    public static void Label(LocalizedText t){}
    [LocalizationStringSink] public static void Label(string s){}
}
class C { void M(){ Sink.Label(new StringId(""k"")); } }
";
        var diags = await AnalyzerHarness.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "KELOC001");
    }
}
```

- [ ] **Step 5: Run to verify tests fail (analyzer not implemented)**

Run: `dotnet test KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj --filter "FullyQualifiedName~KELOC001"`
Expected: FAIL - `LocalizationAnalyzer` does not exist / no diagnostics.

- [ ] **Step 6: Implement the analyzer (KELOC001 only for now)**

Create `KhaozEngine.Localization.Analyzers/LocalizationAnalyzer.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.Localization.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LocalizationAnalyzer : DiagnosticAnalyzer
{
    private const string StringSinkAttr = "KhaozEngine.App.LocalizationStringSinkAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            LocalizationDiagnostics.RawStringSink,
            LocalizationDiagnostics.RawOutsideExempt);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext ctx)
    {
        var invocation = (Microsoft.CodeAnalysis.Operations.IInvocationOperation)ctx.Operation;
        IMethodSymbol target = invocation.TargetMethod;
        if (HasAttribute(target, StringSinkAttr))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawStringSink,
                invocation.Syntax.GetLocation(),
                target.Name));
        }
    }

    private static bool HasAttribute(ISymbol symbol, string fullName)
    {
        foreach (AttributeData a in symbol.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == fullName) return true;
        }
        return false;
    }
}
```

Note: a constructor call (`new Button(rect, "x", font)`) is an `IObjectCreationOperation`, not `IInvocation`. Add handling for it in the same analyzer in Task 12's edit (register `OperationKind.ObjectCreation` and check the constructor symbol's attributes) OR add it now. For KELOC001 to catch obsolete widget CTORS, register object-creation here too:

```csharp
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
```

and:

```csharp
    private static void AnalyzeObjectCreation(OperationAnalysisContext ctx)
    {
        var creation = (Microsoft.CodeAnalysis.Operations.IObjectCreationOperation)ctx.Operation;
        if (creation.Constructor is { } ctor && HasAttribute(ctor, StringSinkAttr))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawStringSink,
                creation.Syntax.GetLocation(),
                ctor.ContainingType.Name));
        }
    }
```

- [ ] **Step 7: Register the projects in the solution**

In `KhaozEngine.slnx`, add project entries for `KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj` and `KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj` (mirror the format of an existing `<Project Path="..." />` entry in that file).

- [ ] **Step 8: Run - PASS.**

Run: `dotnet test KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj --filter "FullyQualifiedName~KELOC001"`
Expected: PASS (2 tests).

- [ ] **Step 9: Commit**

```bash
git add Directory.Packages.props KhaozEngine.Localization.Analyzers KhaozEngine.Localization.Analyzers.Tests KhaozEngine.slnx
git commit -m "analyzer: KhaozEngine.Localization.Analyzers project + KELOC001 (string-sink callers)"
```

---

## Task 12: KELOC002 (Raw outside exempt/debug) + exemption logic

**Files:**
- Modify: `KhaozEngine.Localization.Analyzers/LocalizationAnalyzer.cs`
- Modify: `KhaozEngine.Localization.Analyzers.Tests/LocalizationAnalyzerTests.cs`

**Interfaces:**
- Produces: KELOC002 reporting on `LocalizedText.Raw(...)` invocations whose enclosing symbol chain has no `[LocalizationExempt]` and is not DEBUG-conditional (`[Conditional("DEBUG")]` on the containing method/type, or lexically inside a `#if DEBUG` region).

- [ ] **Step 1: Write the failing tests**

Append to `LocalizationAnalyzerTests.cs`:

```csharp
    [Fact]
    public async Task KELOC002_FiresOnRawInNormalCode()
    {
        var src = @"
using KhaozEngine.App;
class C { void M(){ var x = LocalizedText.Raw(""v1""); } }
";
        var diags = await AnalyzerHarness.Run(src);
        Assert.Contains(diags, d => d.Id == "KELOC002");
    }

    [Fact]
    public async Task KELOC002_SilentUnderExemptType()
    {
        var src = @"
using KhaozEngine.App;
[LocalizationExempt] class C { void M(){ var x = LocalizedText.Raw(""v1""); } }
";
        var diags = await AnalyzerHarness.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
    }

    [Fact]
    public async Task KELOC002_SilentUnderExemptMethod()
    {
        var src = @"
using KhaozEngine.App;
class C { [LocalizationExempt] void M(){ var x = LocalizedText.Raw(""v1""); } }
";
        var diags = await AnalyzerHarness.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
    }

    [Fact]
    public async Task KELOC002_SilentUnderConditionalDebugMethod()
    {
        var src = @"
using KhaozEngine.App;
class C { [System.Diagnostics.Conditional(""DEBUG"")] void M(){ var x = LocalizedText.Raw(""v1""); } }
";
        var diags = await AnalyzerHarness.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
    }

    [Fact]
    public async Task KELOC002_SilentInsideIfDebugRegion()
    {
        // AnalyzerHarness parses without DEFINE DEBUG, so code under #if DEBUG is INACTIVE and not analyzed.
        // Add a DEBUG-defined harness variant to prove active-region exemption.
        var src = @"
using KhaozEngine.App;
class C { void M(){
#if DEBUG
    var x = LocalizedText.Raw(""v1"");
#endif
} }
";
        var diags = await AnalyzerHarness.RunWithDebug(src);
        Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
    }
```

- [ ] **Step 2: Add the DEBUG-defined harness variant**

In `AnalyzerHarness.cs`, add a `RunWithDebug` overload that parses with the `DEBUG` preprocessor symbol so `#if DEBUG` code is active:

```csharp
    public static Task<ImmutableArray<Diagnostic>> RunWithDebug(string source) =>
        Run(source, parseOptions: new CSharpParseOptions().WithPreprocessorSymbols("DEBUG"));

    public static async Task<ImmutableArray<Diagnostic>> Run(string source, CSharpParseOptions? parseOptions = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var stubTree = CSharpSyntaxTree.ParseText(Stubs, parseOptions);
        // ... (same body as before, using `tree`/`stubTree`) ...
    }
```

(Refactor the original `Run(string)` to delegate to `Run(source, null)`.)

- [ ] **Step 3: Run - the KELOC002 tests FAIL (not implemented).**

Run: `dotnet test KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj --filter "FullyQualifiedName~KELOC002"`

- [ ] **Step 4: Implement KELOC002 in the analyzer**

In `LocalizationAnalyzer.cs`, add the constants and extend `AnalyzeInvocation` to detect `LocalizedText.Raw`, walking the enclosing symbols for exemption. Full additions:

```csharp
    private const string ExemptAttr = "KhaozEngine.App.LocalizationExemptAttribute";
    private const string LocalizedTextType = "KhaozEngine.App.LocalizedText";
```

Extend `AnalyzeInvocation`:

```csharp
    private static void AnalyzeInvocation(OperationAnalysisContext ctx)
    {
        var invocation = (Microsoft.CodeAnalysis.Operations.IInvocationOperation)ctx.Operation;
        IMethodSymbol target = invocation.TargetMethod;

        if (HasAttribute(target, StringSinkAttr))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawStringSink, invocation.Syntax.GetLocation(), target.Name));
            return;
        }

        if (target.Name == "Raw" &&
            target.ContainingType?.ToDisplayString() == LocalizedTextType &&
            !IsExempt(ctx.ContainingSymbol))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawOutsideExempt, invocation.Syntax.GetLocation()));
        }
    }
```

Add the exemption walk (attribute on the symbol chain up to the assembly, or a `[Conditional("DEBUG")]` method/type):

```csharp
    private static bool IsExempt(ISymbol? symbol)
    {
        for (ISymbol? s = symbol; s is not null; s = s.ContainingSymbol)
        {
            if (HasAttribute(s, ExemptAttr)) return true;
            if (IsConditionalDebug(s)) return true;
            if (s is IAssemblySymbol) break;
        }
        // Assembly-level [LocalizationExempt].
        return symbol is not null && HasAttribute(symbol.ContainingAssembly, ExemptAttr);
    }

    private static bool IsConditionalDebug(ISymbol s)
    {
        foreach (AttributeData a in s.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute" &&
                a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value as string == "DEBUG")
            {
                return true;
            }
        }
        return false;
    }
```

(`#if DEBUG` region exemption needs no code: under a non-DEBUG build the region is inactive and never analyzed; under a DEBUG build the region is active but the enclosing project is expected to mark debug scopes exempt or accept the warning. The `KELOC002_SilentInsideIfDebugRegion` test passes because in the DEBUG-defined harness the code IS active - so to make it silent we must also treat "inside an active `#if DEBUG` block" as exempt. Implement that by checking the syntax's leading directive context:)

```csharp
        // Inside AnalyzeInvocation's Raw branch, before reporting, also allow active #if DEBUG regions:
        if (IsInsideDebugDirective(invocation.Syntax)) return;
```

with:

```csharp
    private static bool IsInsideDebugDirective(SyntaxNode node)
    {
        // Walk up; if the nearest active conditional directive is `#if DEBUG` (or a DEBUG-including
        // condition), treat as exempt. Uses the directive stack on the containing token.
        var token = node.GetFirstToken();
        foreach (var trivia in token.LeadingTrivia)
        {
            // Cheap heuristic retained for clarity; the robust path uses the syntax tree's directive walk.
        }
        var dir = node.SyntaxTree.GetRoot().DescendantTrivia()
            .Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IfDirectiveTrivia))
            .ToList();
        // Determine if `node` sits within a #if DEBUG ... #endif span that is active.
        foreach (var d in dir)
        {
            if (d.GetStructure() is Microsoft.CodeAnalysis.CSharp.Syntax.IfDirectiveTriviaSyntax ifDir &&
                ifDir.IsActive && ifDir.BranchTaken &&
                ifDir.Condition.ToString().Contains("DEBUG"))
            {
                var start = ifDir.ParentTrivia.SpanStart;
                if (node.SpanStart > start) return true;
            }
        }
        return false;
    }
```

(Add `using System.Linq;` and `using Microsoft.CodeAnalysis.CSharp;` to the analyzer file. If `IsInsideDebugDirective` proves brittle in practice, the fallback documented behaviour is: `#if DEBUG` code is exempt only in non-DEBUG builds; DEBUG-build debug scopes use `[LocalizationExempt]` or `[Conditional("DEBUG")]`. Keep the test aligned with whichever is implemented - if the span heuristic is dropped, change `KELOC002_SilentInsideIfDebugRegion` to assert the warning is present under RunWithDebug and add a note.)

- [ ] **Step 5: Run - PASS.**

Run: `dotnet test KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj`
Expected: PASS (all KELOC001 + KELOC002 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Localization.Analyzers/LocalizationAnalyzer.cs KhaozEngine.Localization.Analyzers.Tests/AnalyzerHarness.cs KhaozEngine.Localization.Analyzers.Tests/LocalizationAnalyzerTests.cs
git commit -m "analyzer: KELOC002 (LocalizedText.Raw outside exempt/debug) + exemption walk"
```

---

## Task 13: Umbrella wiring + packaging verification

**Files:**
- Modify: `KhaozEngine.Game2D/KhaozEngine.Game2D.csproj`
- Modify: `KhaozEngine.Game3D/KhaozEngine.Game3D.csproj`

**Interfaces:** Consumers of either umbrella get the analyzer transitively.

- [ ] **Step 1: Reference the analyzer from the umbrellas**

In `KhaozEngine.Game2D/KhaozEngine.Game2D.csproj`, inside the `<ItemGroup>` with the other ProjectReferences, add (analyzer flows as a package dependency when the umbrella is packed):

```xml
    <ProjectReference Include="../KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj" />
```

Game3D references Game2D, so it inherits the analyzer; no separate entry is required there. (Add the same line to `KhaozEngine.Game3D` only if a build check shows it is not transitively applied.)

- [ ] **Step 2: Pack and verify the packaging**

Run:

```bash
mkdir -p local-feed
dotnet pack KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj -c Release -o ./local-feed
unzip -l local-feed/KhaozEngine.Localization.Analyzers.9.*.nupkg | grep analyzers
dotnet pack KhaozEngine.Game2D/KhaozEngine.Game2D.csproj -c Release -o ./local-feed
unzip -p local-feed/KhaozEngine.Game2D.9.*.nupkg 'KhaozEngine.Game2D.nuspec' | grep -i "Localization.Analyzers"
```

Expected: the analyzer nupkg contains `analyzers/dotnet/cs/KhaozEngine.Localization.Analyzers.dll`; the Game2D nuspec lists a `<dependency id="KhaozEngine.Localization.Analyzers" ...>`.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Game2D/KhaozEngine.Game2D.csproj KhaozEngine.Game3D/KhaozEngine.Game3D.csproj
git commit -m "analyzer: flow KhaozEngine.Localization.Analyzers via Game2D/Game3D umbrellas"
```

---

## Task 14: Showcase migration (worked example)

**Files:**
- Create: `KhaozEngine.Showcase/ShowcaseStrings.resx`
- Create: `KhaozEngine.Showcase/ShowcaseStrings.cs`
- Modify: `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`
- Modify: `KhaozEngine.Showcase/RoomGui.cs`, `KhaozEngine.Showcase/RoomMiniGame.cs`
- Modify: the Showcase startup file that builds the app (find via grep) to set `LocalizationContext.Catalog`.

**Interfaces:** Showcase builds warning-clean using `StringId` (localizable labels) + `LocalizedText.Raw` (non-localizable chrome), with the analyzer referenced so KELOC fires in-repo.

- [ ] **Step 1: Add a resx + StringId constants**

Create `KhaozEngine.Showcase/ShowcaseStrings.resx` (standard resx; add a `<data>` entry per localizable label used in RoomGui/RoomMiniGame, keys like `Menu.Settings`, `Menu.Widgets`, `Menu.Immediate`, `Menu.OverlayDemo`, `Common.Back`, `Settings.Title`, `Settings.Volume`, `Settings.Fullscreen`, `MiniGame.Play`, `MiniGame.BackToMenu`, `MiniGame.Retry`, `Overlay.Title`, `Widgets.Title`, `Gui.Title`, ...). Value = the current English literal.

Create `KhaozEngine.Showcase/ShowcaseStrings.cs`:

```csharp
using KhaozEngine.App;

namespace KhaozEngine.Showcase;

/// <summary>
/// Hand-authored localization keys for the showcase, mirroring <c>ShowcaseStrings.resx</c>. This is the
/// consumer pattern until the engine ships a resx-to-StringId source generator (see ROADMAP).
/// </summary>
internal static class ShowcaseStrings
{
    public static readonly StringId MenuSettings = new("Menu.Settings");
    public static readonly StringId MenuWidgets = new("Menu.Widgets");
    public static readonly StringId MenuImmediate = new("Menu.Immediate");
    public static readonly StringId MenuOverlayDemo = new("Menu.OverlayDemo");
    public static readonly StringId CommonBack = new("Common.Back");
    public static readonly StringId SettingsTitle = new("Settings.Title");
    public static readonly StringId SettingsVolume = new("Settings.Volume");
    public static readonly StringId SettingsFullscreen = new("Settings.Fullscreen");
    public static readonly StringId MiniGamePlay = new("MiniGame.Play");
    public static readonly StringId MiniGameBackToMenu = new("MiniGame.BackToMenu");
    public static readonly StringId MiniGameRetry = new("MiniGame.Retry");
    // ... one per resx key used.
}
```

- [ ] **Step 2: Wire the catalog at startup**

Find the showcase entry/setup: `grep -rn "ResourceManager\|new LocalizationManager\|LocalizationContext\|static.*Main\|GameApp" KhaozEngine.Showcase`. In the setup path (where the app/services are built), add:

```csharp
var rm = new System.Resources.ResourceManager("KhaozEngine.Showcase.ShowcaseStrings", typeof(ShowcaseStrings).Assembly);
LocalizationContext.Catalog = new ResourceStringCatalog(rm);
```

(`using KhaozEngine.App;` at the top.) Confirm the resx's generated resource name matches the `ResourceManager` base name (default `<RootNamespace>.ShowcaseStrings`).

- [ ] **Step 3: Migrate RoomGui.cs / RoomMiniGame.cs call sites**

Replace each `new Label(rect, "Settings", font)` etc. with the StringId where the text is real UI copy, and `LocalizedText.Raw(...)` where it is non-localizable (e.g. `"70%"` readout, `"PAUSED - Esc to resume"` if treated as chrome). Examples:

```csharp
// localizable:
_settings = new Button(mid with { Y = mid.Y - 96 }, ShowcaseStrings.MenuSettings, _a.Small, () => Manager.Add(new SettingsScreen(_a, _vp)));
_title = new Label(Layout.Resolve(d, Anchor.Top, d.Width, 44, marginY: 20), ShowcaseStrings.SettingsTitle, _a.Big) { Align = TextAlign.Center };
// non-localizable readout:
_readout = new Label(new Rect(d.X + 370, d.Y + 86, 50, 24), LocalizedText.Raw("70%"), _a.Small) { Align = TextAlign.Right };
```

For the tooltip lines in RoomGui.cs (lines ~250-251), use `TooltipLine.Of`:

```csharp
new[] {
    TooltipLine.Of(LocalizedText.Raw("Auto-sized, flips when"), new Vector4(0.78f, 0.82f, 0.92f, 1f)),
    TooltipLine.Of(LocalizedText.Raw("it would clip the top."), new Vector4(0.78f, 0.82f, 0.92f, 1f)),
}
```

For any block that is purely developer/demo chrome with many Raw calls, mark that type `[KhaozEngine.App.LocalizationExempt]` so KELOC002 stays quiet there and demonstrates the exemption. Keep at least one intentional un-exempt `LocalizedText.Raw` in a `[Conditional("DEBUG")]`-marked or exempt spot so the sample teaches the pattern.

- [ ] **Step 4: Reference the analyzer in Showcase**

In `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`, add:

```xml
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Update="ShowcaseStrings.resx">
      <Generator></Generator>
    </EmbeddedResource>
  </ItemGroup>
```

(The Showcase already project-references the engine; the analyzer reference proves KELOC fires on real showcase code. Ensure the resx is embedded so `ResourceManager` finds it.)

- [ ] **Step 5: Build Showcase warning-clean**

Run: `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`
Expected: no CS0618 (all string sinks migrated) and no unexpected KELOC warnings (localizable via StringId, chrome exempt or intentionally Raw).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Showcase
git commit -m "showcase: migrate to LocalizedText (resx + StringId catalog + Raw for chrome); reference analyzer"
```

---

## Task 15: Migrate remaining engine tests off obsolete overloads

**Files:**
- Modify: `KhaozEngine.Tests/Gui/ButtonTests.cs`, `KhaozEngine.Tests/Gui/IconWidgetTests.cs`, `KhaozEngine.Tests/Gui/TooltipTests.cs`

**Interfaces:** Existing behaviour tests keep passing, now constructing widgets via `LocalizedText.Raw`.

- [ ] **Step 1: Update the three test files**

Replace string literals at the migrated sinks with `LocalizedText.Raw(...)`:
- `ButtonTests.cs`: `new Button(Btn, "Go", null!)` -> `new Button(Btn, LocalizedText.Raw("Go"), null!)` (all 4 sites), add `using KhaozEngine.App;`.
- `IconWidgetTests.cs`: `ui.StatChip(rect, Icons.Coin, "Gold", "120", font: null!, GuiStyle.Default)` -> `ui.StatChip(rect, Icons.Coin, LocalizedText.Raw("Gold"), LocalizedText.Raw("120"), font: null!, GuiStyle.Default)`, add `using KhaozEngine.App;`.
- `TooltipTests.cs`: `new TooltipLine(s, Vector4.One)` stays valid (the record ctor is not obsolete). Only migrate any `Tooltip.Show("title", ...)` calls to `Show(LocalizedText.Raw("title"), ...)`; add `using KhaozEngine.App;` if such calls exist.

- [ ] **Step 2: Run full Gui + App test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Gui|FullyQualifiedName~App"`
Expected: PASS, no CS0618 warnings from these files.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/Gui/ButtonTests.cs KhaozEngine.Tests/Gui/IconWidgetTests.cs KhaozEngine.Tests/Gui/TooltipTests.cs
git commit -m "test: migrate Gui widget tests to LocalizedText.Raw"
```

---

## Task 16: Documentation sweep

**Files:**
- Modify: `README.md` (package table + umbrella note + layering)
- Modify: `KhaozEngine.App/README.md`
- Modify: `KhaozEngine.Gui/README.md`
- Modify: `docs/USING-KHAOZENGINE.md` (new adoption section)
- Modify: `docs/DEPENDENCY-SEAMS.md` (Gui->App edge + analyzer)
- Modify: `docs/ROADMAP.md` (resx->StringId generator follow-up; version string handled in Task 17)

- [ ] **Step 1: README package catalog**

Add a `KhaozEngine.Localization.Analyzers` row to the package table in `README.md` (summary: "Roslyn analyzer enforcing the LocalizedText localization contract - KELOC001/002; flows via the Game2D/Game3D umbrellas"). In the umbrella tables, note that Game2D/Game3D carry the analyzer. In the layering description, note the new `Gui -> App` edge.

- [ ] **Step 2: App package README**

Document `StringId`, `LocalizedText` (implicit-from-StringId, `Of`, `Raw`, `Resolve`), `LocalizationContext.Catalog`, `[LocalizationExempt]`, `[LocalizationStringSink]`.

- [ ] **Step 3: Gui package README**

Note the sinks now take `LocalizedText`; the `string` overloads are `[Obsolete]`. Note `IconButton`'s string is an icon-atlas key (not localized).

- [ ] **Step 4: USING-KHAOZENGINE adoption section**

Add "Compile-time localization enforcement": how to (1) author a `.resx` + `StringId` constants, (2) register a `ResourceStringCatalog` into `LocalizationContext.Catalog` at startup, (3) pass `StringId`/`LocalizedText.Of` at sinks, (4) use `LocalizedText.Raw` + `[LocalizationExempt]` for non-localizable/debug text, (5) raise KELOC001/002 to error in `.editorconfig`. Mirror the Showcase example.

- [ ] **Step 5: DEPENDENCY-SEAMS + ROADMAP**

In `docs/DEPENDENCY-SEAMS.md`, add the `Gui -> App` edge and the analyzer package. In `docs/ROADMAP.md`, add a future item: "resx-to-StringId source generator (emit StringId constants from .resx keys, removing hand-authored constant classes)".

- [ ] **Step 6: Grep sweep for stale docs**

Run: `grep -rn "LocalizedText\|StringId\|LocalizationStringSink\|LocalizationExempt\|Localization.Analyzers" --include="*.md" . | grep -v "/obj/\|/bin/"`
Verify every doc that should mention the new API does, and nothing describes removed behaviour.

- [ ] **Step 7: Commit**

```bash
git add README.md KhaozEngine.App/README.md KhaozEngine.Gui/README.md docs/USING-KHAOZENGINE.md docs/DEPENDENCY-SEAMS.md docs/ROADMAP.md
git commit -m "docs: LocalizedText enforcement - catalog, adoption story, seams, generator roadmap"
```

---

## Task 17: Release ritual

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`)
- Modify: `CHANGELOG.md`
- Modify: `docs/ROADMAP.md` ("Current released version"), `README.md` (`<PackageReference>` example version)

- [ ] **Step 1: Re-check for concurrent work and the free version**

Run:

```bash
git fetch
git log --oneline origin/main -5
git tag | sort -V | tail -5
grep KhaozEngineVersion Directory.Build.props
```

If `origin/main` advanced, merge it into this branch first, resolve conflicts (especially `Directory.Build.props` version + `CHANGELOG.md`), and re-run the full test suite on the merged result. Take the next FREE minor above the current released version (base assumption 9.34.0; bump past any tag already taken).

- [ ] **Step 2: Bump version + CHANGELOG**

Set `<KhaozEngineVersion>` to the free version. Add a newest-first `CHANGELOG.md` entry, one-line digest first, e.g.:

```markdown
## 9.34.0
Compile-time localization enforcement: player-facing Gui text now takes a LocalizedText value type instead of a raw string, with a Roslyn analyzer (KELOC001/002) and an explicit LocalizedText.Raw escape hatch.

- New in KhaozEngine.App: StringId (typed localization key, no implicit string conversion), LocalizedText (lazy resolve against the ambient LocalizationContext.Catalog; implicit from StringId, explicit Raw for non-localizable text), LocalizationContext, and the [LocalizationExempt] / [LocalizationStringSink] attributes.
- KhaozEngine.Gui sinks (Label, Button widgets; GuiSurface Label/Button/StatChip; GuiDraw.DrawButton; Tooltip.Show + TooltipLine.Of) gained LocalizedText overloads; the string overloads are [Obsolete] and delegate via LocalizedText.Raw. Gui now references App. IconButton is unchanged (its string is an icon-atlas key).
- New KhaozEngine.Localization.Analyzers: KELOC001 flags raw-string sink calls, KELOC002 flags LocalizedText.Raw outside [LocalizationExempt]/DEBUG code. Ships as warnings; raise to error in .editorconfig. Flows to consumers via the Game2D/Game3D umbrellas.
- Showcase migrated as the worked example (resx + StringId catalog + Raw for chrome).
```

- [ ] **Step 3: Update guard-checked declarations**

Set `docs/ROADMAP.md` "Current released version" and the `README.md` `<PackageReference>` example to the new version. Run:

```bash
bash scripts/check-doc-versions.sh
```

Expected: passes.

- [ ] **Step 4: Full solution build + test**

Run:

```bash
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
dotnet test KhaozEngine.Localization.Analyzers.Tests/KhaozEngine.Localization.Analyzers.Tests.csproj
```

Expected: build + all tests pass.

- [ ] **Step 5: Pack to local-feed**

Run:

```bash
mkdir -p local-feed
dotnet pack KhaozEngine.slnx -c Release -o ./local-feed
```

(Or pack the affected packages; the slnx pack is cumulative.)

- [ ] **Step 6: Commit, tag, and finish**

```bash
git add Directory.Build.props CHANGELOG.md docs/ROADMAP.md README.md
git commit -m "localization(9.34.0): LocalizedText compile-time enforcement + analyzer (release)"
```

Then follow the merge-to-main + `scripts/tag-release.sh` + push ritual (Task 18 / finishing).

---

## Task 18: Merge, tag, push (finishing)

- [ ] **Step 1:** `git fetch`; if `origin/main` moved, merge into this branch, re-test, re-bump if the version/tag was taken (repeat Task 17 Step 1 logic).
- [ ] **Step 2:** Merge the branch into local `main` (fast-forward), verify tests on the merged result.
- [ ] **Step 3:** `bash scripts/tag-release.sh` (creates the annotated `vX.Y.Z` tag from `<KhaozEngineVersion>`; do NOT hand-type `git tag`).
- [ ] **Step 4:** Push `main` + the tag. Remove the worktree and delete the merged branch.

---

## Self-Review

**Spec coverage:**
- Deliverable 1 (LocalizedText + StringId + Raw + implicit-from-StringId + lazy resolve): Tasks 1-3. ✔
- Deliverable 2 (Gui overloads + [Obsolete] string): Tasks 5-10. ✔ (IconButton correctly excluded; PopupPanel handled additively.)
- Deliverable 3 (analyzer, warning-first, raise-to-error, flows via umbrellas): Tasks 11-13. ✔
- Deliverable 4 (migrate samples + CHANGELOG + version + pack + tag): Tasks 14, 17, 18. ✔
- Deliverable 5 (consumer adoption doc): Task 16 Step 4. ✔
- Decision: types in App (Task 5 adds Gui->App). ✔ Exemption = attribute + DEBUG (Task 12). ✔ StringId manual now + generator roadmap (Task 16 Step 5). ✔ Sample scope = Showcase + all breaking sinks (Tasks 14-15). ✔

**Placeholder scan:** No "TBD/TODO". The one heuristic risk is `IsInsideDebugDirective` (Task 12) - it carries an explicit fallback + test-alignment note, not a placeholder.

**Type consistency:** `Content` (LocalizedText) used consistently on Label/Button; `Resolved` accessor consistent; `LocalizedText.Raw`, `LocalizedText.Of`, `StringId.Of`, `LocalizationContext.Catalog`, `TooltipLine.Of`, `LocalizationStringSinkAttribute`/`LocalizationExemptAttribute` names consistent across analyzer + App + Gui tasks.

**Known implementation risks to watch:**
1. Analyzer transitive flow through the metapackage (Task 13) - verified by nuspec inspection + Showcase direct reference; full transitive validation is a documented manual check.
2. `#if DEBUG` active-region detection (Task 12) - has a documented fallback.
3. `Microsoft.CodeAnalysis.CSharp` version vs SDK Roslyn (Task 11) - pick the highest that restores.
