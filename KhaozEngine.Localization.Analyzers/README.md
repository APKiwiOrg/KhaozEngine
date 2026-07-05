# KhaozEngine.Localization.Analyzers

Roslyn analyzer enforcing KhaozEngine's `LocalizedText` localization contract.

- **KELOC001** (Warning): player-facing text passed as a raw string to a `[LocalizationStringSink]`-marked
  method or constructor (the engine's obsolete `string` Gui overloads, or a sink a game marks itself). Pass a
  `StringId` (localizable) or `LocalizedText.Raw(...)` (non-localizable) instead.
- **KELOC002** (Warning): `LocalizedText.Raw(...)` used outside code marked `[LocalizationExempt]` or DEBUG
  conditional (`[Conditional("DEBUG")]` member/type, or inside a `#if DEBUG` region). Confirm the text is
  intentionally non-localizable, or mark the scope exempt.
- **KELOC003** (Warning): a bare string literal drawn straight to the low-level 2D primitive
  `KhaozEngine.Render2D.SpriteBatch.DrawString(font, "text", ...)` (the sink games hit when they render UI
  without Gui widgets). v1 flags only a non-interpolated, non-verbatim literal of length > 1 that contains a
  letter; interpolated text, variables, numbers, format tokens, and single-character glyphs are left alone.
  Localize it (resolve a `StringId` through the catalog), use `LocalizedText.Raw("...").Resolve()` for
  non-localizable text, or mark the scope `[LocalizationExempt]` / DEBUG. Covers only the engine primitive - a
  game's own `SpriteBatch`-based text helpers are its own to guard.

All three ship as warnings. Raise any to error in a consumer `.editorconfig`:

```ini
dotnet_diagnostic.KELOC001.severity = error
dotnet_diagnostic.KELOC002.severity = error
dotnet_diagnostic.KELOC003.severity = error
```

The analyzer flows automatically to any project referencing the `KhaozEngine.Game2D` or `KhaozEngine.Game3D`
umbrella metapackage. The marker attributes (`LocalizationExemptAttribute`, `LocalizationStringSinkAttribute`)
and the `StringId` / `LocalizedText` types live in `KhaozEngine.App`.
