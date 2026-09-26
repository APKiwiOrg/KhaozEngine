# Inline rich text design

Issue: [#65](https://github.com/APKiwiOrg/KhaozEngine/issues/65)

Status: Implemented for the staged multi-issue release.

## Outcome

Localized chat, tooltips and game-owned dialogue can opt into semantic inline colour spans without changing any
plain text path. A translated template may write `[emphasis]important[/]` or `[key]E[/]`. The caller maps those
stable names to colours, so translations do not contain palette values. `[[` emits one literal `[`.

The first scope treats style as a semantic colour. It does not add bold, italic, font switching, per-span scale,
links or actions. Those features require mixed-font line metrics or interaction state and are separate work.

## Ownership

`KhaozEngine.Gui` owns localized markup because it already joins `LocalizedText`, GUI themes and resolved
`ColoredTextRun` values. `MarkupText` is the opt-in localized value. Its catalog template is trusted markup, while
every format argument is converted with the active culture and escaped before `IStringCatalog.Format` inserts it.
There is no raw root factory, so the existing localization analyzer contract retains one explicit raw escape hatch.

`KhaozEngine.Render2D` owns wrapping resolved coloured runs. `ColoredTextLayout.Wrap` concatenates the runs, uses
`TextLayout.Wrap` with preserved spaces, and projects every wrapped source slice back onto its colours. This is the
public seam a game-owned dialogue view uses. `SpriteBatch.DrawStringRuns` remains the draw primitive.

## Grammar and fallback

- `[name]` opens a semantic colour span. Names use ASCII letters, digits, dot, underscore and hyphen.
- `[/]` closes the innermost span, so a translator never has to repeat a style name.
- Spans may nest. A missing mapping inherits the enclosing colour, or the caller's default colour at the root.
- `[[` emits a literal opening bracket.
- An unmatched opener or closer, an invalid name, or an incomplete tag makes the complete resolved value render
  literally in the default colour. Parsing never throws and never drops player-facing text.

Plain `LocalizedText`, `TooltipLine.Of`, `TooltipLine.OfSegments` and ordinary `ChatEntry` values retain their
existing behavior. A string becomes markup only through `MarkupText` and a rich-text adapter.

## Consumer adapters

`TooltipLine.OfMarkup` resolves one `MarkupText` through caller-supplied styles. Existing tooltip wrapping then
uses the shared coloured-run layout.

`ChatEntry` keeps its existing constructor and `Content`. An additive markup constructor records an optional
`MarkupContent`. `ChatBox` takes the rich path only for that content, combining the timestamp, author, marked-up
body and repeat count before wrapping. Rich rows cache resolved runs, and cache identity includes the theme colours
and style map. Ordinary rows keep the existing uniform draw path. `ChatBoxTheme.InlineStyles` is empty by default,
so an unmapped semantic name falls back to the entry kind colour.

There is no engine dialogue widget. A game-owned dialogue view resolves `MarkupText`, wraps through
`ColoredTextLayout`, and draws each returned line through `SpriteBatch.DrawStringRuns`.

## Verification

Headless parser tests cover nesting, missing mappings, literal escape, malformed fallback and hostile formatted
arguments. Render2D tests cover wrap boundaries, hard breaks, repeated text and explicit blank lines. Tooltip tests
prove marked-up localized content keeps colour through wrapping. Chat tests prove the opt-in rich path, the unchanged
plain path, locale refresh and allocation-free steady drawing.
