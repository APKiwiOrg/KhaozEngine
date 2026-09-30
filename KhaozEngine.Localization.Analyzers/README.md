# KhaozEngine.Localization.Analyzers

Roslyn analyzer and opt-in source generator enforcing KhaozEngine's `LocalizedText` localization contract.

- **KELOC001** (Warning): player-facing text passed as a raw string to a `[LocalizationStringSink]`-marked
  method or constructor (a sink a game marks itself). The engine's Gui sinks take only `LocalizedText`, so a
  raw string there is a compile error rather than a diagnostic. Pass a
  `StringId` (localizable) or `LocalizedText.Raw(...)` (non-localizable) instead.
- **KELOC002** (Warning): `LocalizedText.Raw(...)` used outside code marked `[LocalizationExempt]` or DEBUG
  conditional (`[Conditional("DEBUG")]` member/type, or inside a `#if DEBUG` region). Confirm the text is
  intentionally non-localizable, or mark the scope exempt.
- **KELOC003** (Warning): a hardcoded string literal drawn straight to the low-level 2D primitive
  `KhaozEngine.Render2D.SpriteBatch.DrawString(font, "text", ...)` (the sink games hit when they render UI
  without Gui widgets). Flags plain literals plus the literal segments of interpolated (`$"Score: {n}"`) and
  concatenated (`"Item " + i`) strings, each of length > 1 and containing a letter. Interpolation holes,
  non-literal concat operands, variables, numbers, format tokens, verbatim/raw literals, and single-character
  glyphs are left alone. Localize it (resolve a `StringId` through the catalog), use
  `LocalizedText.Raw("...").Resolve()` for non-localizable text, or mark the scope `[LocalizationExempt]` /
  DEBUG. Covers only the engine primitive - a game's own `SpriteBatch`-based text helpers are its own to guard.

The three analyzer diagnostics ship as warnings. Raise any to error in a consumer `.editorconfig`:

```ini
dotnet_diagnostic.KELOC001.severity = error
dotnet_diagnostic.KELOC002.severity = error
dotnet_diagnostic.KELOC003.severity = error
```

## Generate StringId keys from a neutral resx

Add the neutral resx as an analyzer AdditionalFile and name the exact class to generate:

```xml
<ItemGroup>
  <AdditionalFiles Include="Strings.resx"
                   KhaozStringIdType="MyGame.Strings" />
</ItemGroup>
```

The package's buildTransitive props expose this metadata to Roslyn. The SDK still embeds `Strings.resx`
normally. Do not add satellite files such as `Strings.fr.resx`. Translation coverage remains the job of
`KhaozEngine.Localization.TestKit`.

The generated class is `internal` by default and contains one public static readonly `StringId` per neutral
string key. Add `KhaozStringIdAccessibility="public"` when another assembly must reference the class.
Resource keys are ordered ordinally. Dots, underscores, and other separators are removed while the next letter
or digit is uppercased, so `Menu.Play` becomes `MenuPlay` and `fly_speed` becomes `FlySpeed`.

Generator configuration and input failures are errors:

- **KELOC004**: invalid target type, accessibility, or opted file extension.
- **KELOC005**: malformed resx shape or invalid resource key.
- **KELOC006**: two resource keys produce the same member name.
- **KELOC007**: the target type already exists or two opted resources request it.

The analyzer and generator flow automatically to any project referencing the `KhaozEngine.Game2D` or
`KhaozEngine.Game3D` umbrella metapackage. The marker attributes (`LocalizationExemptAttribute`,
`LocalizationStringSinkAttribute`) and the `StringId` / `LocalizedText` types live in `KhaozEngine.App`.
