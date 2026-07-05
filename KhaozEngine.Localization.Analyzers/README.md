# KhaozEngine.Localization.Analyzers

Roslyn analyzer enforcing KhaozEngine's `LocalizedText` localization contract.

- **KELOC001** (Warning): player-facing text passed as a raw string to a `[LocalizationStringSink]`-marked
  method or constructor (the engine's obsolete `string` Gui overloads, or a sink a game marks itself). Pass a
  `StringId` (localizable) or `LocalizedText.Raw(...)` (non-localizable) instead.
- **KELOC002** (Warning): `LocalizedText.Raw(...)` used outside code marked `[LocalizationExempt]` or DEBUG
  conditional (`[Conditional("DEBUG")]` member/type, or inside a `#if DEBUG` region). Confirm the text is
  intentionally non-localizable, or mark the scope exempt.

Both ship as warnings. Raise either to error in a consumer `.editorconfig`:

```ini
dotnet_diagnostic.KELOC001.severity = error
dotnet_diagnostic.KELOC002.severity = error
```

The analyzer flows automatically to any project referencing the `KhaozEngine.Game2D` or `KhaozEngine.Game3D`
umbrella metapackage. The marker attributes (`LocalizationExemptAttribute`, `LocalizationStringSinkAttribute`)
and the `StringId` / `LocalizedText` types live in `KhaozEngine.App`.
