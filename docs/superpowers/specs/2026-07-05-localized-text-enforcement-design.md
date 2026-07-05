# Compile-time localization enforcement (LocalizedText + analyzer)

Status: approved design, ready for implementation plan.
Date: 2026-07-05. Engine base version: 9.33.0 (target bump: next free minor, additive).

## Goal

Make it a compile error to hardcode player-facing UI text in KhaozEngine. The Gui/UI API stops
accepting raw `string` for player-facing text and accepts a `LocalizedText` value type instead. A bare
string literal at a player-facing sink no longer compiles. There is an explicit, greppable escape hatch
(`LocalizedText.Raw`) for debug and genuinely non-localizable text, and a Roslyn analyzer that keeps the
escape hatch honest.

Enforcement mechanism, in one line: **`LocalizedText` has an implicit conversion only from `StringId`,
never from `string`** - so `Label(rect, "Play", font)` fails to compile while `Label(rect, Strings.Play,
font)` and `Label(rect, LocalizedText.Raw("v1.2"), font)` both work.

## Decisions (locked)

1. `LocalizedText` + `StringId` live in **`KhaozEngine.App`**, beside the existing
   `IStringCatalog`/`LocalizationManager`. `KhaozEngine.Gui` gains a `Gui -> App` project reference
   (acyclic: App depends only on Diagnostics; App never references Gui).
2. Analyzer exemption for `LocalizedText.Raw` is recognised by **both** a `[LocalizationExempt]`
   attribute (assembly/type/member) **and** DEBUG-conditional code (`[Conditional("DEBUG")]`
   member/type, or lexically inside a `#if DEBUG` region).
3. Ship the `StringId` type + hand-authored constants pattern now. A `.resx -> StringId` source
   generator is a **documented ROADMAP follow-up**, not in this change.
4. Sample migration: fully migrate `KhaozEngine.Showcase` as the worked example, and mechanically fix
   every other in-repo sample that calls the now-`[Obsolete]` string sinks so the whole solution builds
   warning-clean.

## Confirmed current API (KhaozEngine.App)

- `IStringCatalog`: `string Get(string key)` (missing key returns the key), `string Format(string key,
  params object?[] args)`, `bool TryGet(string key, out string value)`. `ResourceStringCatalog` reads
  `CultureInfo.CurrentUICulture` live, so `LocalizationManager.SetCulture` re-resolves without recreating
  the catalog.
- `LocalizationManager`: owns culture discovery + `SetCulture`; exposes a `Catalog` convenience.
- `ServiceLocator`: an instance registry (`IServiceProvider`), **no global singleton** - so lazy
  resolution needs a dedicated ambient holder, not `ServiceLocator`.
- **No `StringId` type exists today.** The catalog keys on raw `string`.
- The old `KhaozEngine.Localization` package was folded into App at 9.0.0; the leftover directory has no
  live csproj. The id `KhaozEngine.Localization.Analyzers` is free.

## New types in KhaozEngine.App

### `readonly struct StringId : IEquatable<StringId>`
- Wraps `string Key` (null/empty guarded).
- `StringId(string key)` and `static StringId Of(string key)`.
- `ToString() => Key`; equality/hash on `Key` with `StringComparison.Ordinal`.
- **No implicit conversion from `string`** - this is the enforcement pivot. Consumers author `StringId`
  values explicitly (a constants class today, a generator later), which is exactly the deliberate
  friction that turns every key into a catalog entry.

### `readonly struct LocalizedText`
Discriminated union of two cases, both stored, neither resolved at construction:
- Localizable: a `StringId` + optional `object?[]` format args.
- Raw: a literal `string` (never touches the catalog).

Members:
- `public static implicit operator LocalizedText(StringId id)` - args-less localizable.
- `public static LocalizedText Of(StringId id, params object?[] args)` - localizable with format args.
- `public static LocalizedText Raw(string text)` - the escape hatch (greppable token `LocalizedText.Raw`).
- `public string Resolve(IStringCatalog? catalog)`:
  - Raw -> the literal, verbatim.
  - Localizable + catalog + args -> `catalog.Format(Key, args)`.
  - Localizable + catalog, no args -> `catalog.Get(Key)`.
  - Localizable + no catalog -> `Key` (visible placeholder; never throws).
  - `default(LocalizedText)` -> `""`.
- `public string Resolve()` -> `Resolve(LocalizationContext.Catalog)`.
- `public override string ToString() => Resolve()` - convenience for logs/debug (ambient).
- `bool IsRaw`, `StringId Id` accessors for tests/consumers.

**Lazy is load-bearing:** `LocalizedText` stores id + args and re-resolves on every `Resolve()` call.
Combined with `ResourceStringCatalog` reading `CurrentUICulture` live, a runtime locale switch shows up
on the next draw with no cache to invalidate. Widgets store the `LocalizedText` and call `Resolve()` in
`Draw`; the immediate-mode `GuiSurface` resolves inline per call (already per-frame).

### `static class LocalizationContext`
- `public static IStringCatalog? Catalog { get; set; }` - the ambient catalog the app sets once at
  startup. `LocalizedText.Resolve()` reads it. Null is legal (headless tests, no-localization apps):
  localizable text then renders its key.
- Rationale: there is no global `ServiceLocator`, and threading a catalog through every Gui draw call is
  invasive. An ambient holder mirrors how `CurrentUICulture` is already ambient.

### Marker attributes (in App, so engine + analyzer + consumers all reference them)
- `[LocalizationExempt]` - `AttributeUsage(Assembly | Class | Struct | Method | Constructor | Property |
  Field, Inherited=false)`. Marks a scope where `LocalizedText.Raw` is intentional and the analyzer
  stays silent.
- `[LocalizationStringSink]` - `AttributeUsage(Method | Constructor)`. Marks the discouraged raw-string
  player-facing overloads so the analyzer is generic (not method-name-hardcoded) and consumer-extensible
  (a game can mark its own string sinks).

## Gui overloads (KhaozEngine.Gui)

For each player-facing sink, add a `LocalizedText` overload and mark the existing `string` overload
`[Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or
LocalizedText.Raw(...) for non-localizable text.")]` + `[LocalizationStringSink]`. New overloads resolve
at draw time via `text.Resolve()`.

Sinks:
- `Label` widget ctor `Label(Rect, string, SpriteFont)` -> add `Label(Rect, LocalizedText, SpriteFont)`;
  store the `LocalizedText`, resolve in `Draw`.
- `Button` widget ctor `Button(Rect, string, SpriteFont, Action?)` -> add the `LocalizedText` ctor;
  resolve in `Draw`.
- `GuiSurface.Label(font, string, pos, color)` and `Label(font, rect, string, color, align)`.
- `GuiSurface.Button(font, rect, string)` and `Button(font, rect, string, style, enabled, selected)`.
- `GuiDraw.DrawButton(batch, white, font, rect, string label, ...)`.
- `Tooltip` line-add path: the point where a caller supplies tooltip line text takes `LocalizedText`;
  the string entry point is obsoleted. (`TooltipLine` stays a leaf render struct holding resolved
  `string Text`; the sink is at the builder API where lines are added, resolved when the line is built.)

Deliberate non-sink: `GuiSurface.IconButton(rect, string iconId, ...)` - `iconId` is an icon-atlas key,
not player-facing text. Left untouched, called out in docs so the exclusion is intentional.

Testability seam: widgets expose the resolved text (or accept an explicit catalog in a test path) so the
resolve-at-draw behaviour is checkable headlessly without a GPU device.

## Analyzer: KhaozEngine.Localization.Analyzers

New `netstandard2.0` Roslyn analyzer project (compiler analyzers must target netstandard2.0), referencing
`Microsoft.CodeAnalysis.CSharp`. Packaged as an analyzer nupkg (`analyzers/dotnet/cs`).

Diagnostics (category `Localization`, both default **Warning**):
- **KELOC001** - a call to any method/ctor marked `[LocalizationStringSink]` (the obsolete string
  overloads, or consumer-marked sinks). Message: player-facing text passed as a raw string bypasses
  localization; use a `StringId` or `LocalizedText.Raw(...)`.
- **KELOC002** - a call to `KhaozEngine.App.LocalizedText.Raw(...)` whose enclosing symbol chain has no
  `[LocalizationExempt]` **and** is not DEBUG-conditional. DEBUG-conditional = the containing method or
  type carries `[System.Diagnostics.Conditional("DEBUG")]`, or the call sits lexically inside an active
  `#if DEBUG` region. Message: `LocalizedText.Raw` bypasses localization; confirm the text is
  intentionally non-localizable or mark the scope `[LocalizationExempt]`.

Severity is Warning out of the box; a consumer raises either to error in `.editorconfig`
(`dotnet_diagnostic.KELOC001.severity = error`). This is the "ship as warning first, a repo can raise it
to error" requirement.

Flow to consumers: the analyzer package is added as a dependency of the `KhaozEngine.Game2D` and
`KhaozEngine.Game3D` umbrellas (which pack `ProjectReference`s as package dependencies), so any game
referencing an umbrella gets the analyzer. Packaging is configured so the analyzer assets flow through
the umbrella dependency; validated by inspecting the packed nuspec and by a direct
`OutputItemType="Analyzer"` reference in the migrated sample to prove the diagnostics fire in-repo.

Tests: a dedicated `KhaozEngine.Localization.Analyzers.Tests` xUnit project using
`Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit`. Cases: KELOC001 fires on a marked string-sink
call and stays silent on the `LocalizedText` overload; KELOC002 fires on `Raw()` in normal code and stays
silent under `[LocalizationExempt]` (assembly/type/member), under `[Conditional("DEBUG")]`, and inside
`#if DEBUG`.

## Sample migration

- `KhaozEngine.Showcase`: the worked example. Add a small `ShowcaseStrings.resx` + a hand-authored
  `ShowcaseStrings` static class of `StringId` constants for the localizable labels, register a
  `ResourceStringCatalog` over it into `LocalizationContext.Catalog` at startup, and use
  `LocalizedText.Raw` (with a scoped `[LocalizationExempt]` where a block is genuinely debug/chrome) for
  non-localizable tokens.
- Every other in-repo sample that calls the obsoleted string sinks: mechanically migrate to
  `LocalizedText` (mostly `LocalizedText.Raw`, since sample chrome is not localized) so the solution
  builds warning-clean. Enumerate the affected samples during implementation.

## Docs + release ritual

- `CHANGELOG.md` entry (newest-first, one-line digest first).
- Bump `<KhaozEngineVersion>` (re-read the up-to-date `main` + tags at release; take the next FREE
  minor - additive change).
- Update the guard-checked declarations: `docs/ROADMAP.md` "Current released version" and the
  `README.md` `<PackageReference>` example (`scripts/check-doc-versions.sh`).
- `README.md` package table: add the `KhaozEngine.Localization.Analyzers` row and note the umbrellas
  carry it. Note the new `Gui -> App` edge in the layering description.
- App package `README.md`: `StringId`, `LocalizedText`, `LocalizationContext`, `[LocalizationExempt]`,
  `[LocalizationStringSink]`. Gui package `README.md`: the `LocalizedText` overloads + obsolete string
  ones.
- `docs/USING-KHAOZENGINE.md`: a "Compile-time localization enforcement" section = the consumer adoption
  story (StringId catalog + LocalizedText + Raw/debug exemption + analyzer severity + how to raise to
  error). This is deliverable 5.
- `docs/DEPENDENCY-SEAMS.md`: the new `Gui -> App` edge and the analyzer package.
- `docs/ROADMAP.md`: add the `.resx -> StringId` source generator as a future follow-up.
- `dotnet pack -c Release -o ./local-feed`, commit, `scripts/tag-release.sh`, push `main` + tag.

## Testing (TDD, KhaozEngine.Tests unless noted)

- `StringId`: equality/hashing on Key, `ToString`, `Of`/ctor guards.
- `LocalizedText`: Raw resolves literally; StringId resolves via catalog; args -> `Format`; no-catalog ->
  key placeholder; `default` -> `""`; locale switch (swap `CurrentUICulture`, or swap catalog) re-resolves
  on the next `Resolve()`.
- `LocalizationContext`: ambient `Resolve()` reads the set catalog; null-catalog path.
- Gui: a `Label`/`Button` built from `LocalizedText` resolves via the ambient catalog at draw (via the
  resolved-text seam, no GPU).
- Analyzer test project: KELOC001/KELOC002 positive + negative (exempt attribute at each scope, DEBUG
  conditional, `#if DEBUG`).

## Out of scope (this change)

- `.resx -> StringId` source generator (ROADMAP follow-up).
- Migrating the consumer games (Hardpoint/Nullwake/SpaceGame/Ruinborne) - they adopt on their next engine
  bump using the documented story.
- Localizing non-`string`-sink Gui surfaces beyond those listed (e.g. `TextInput` user content is not
  player-facing chrome).
