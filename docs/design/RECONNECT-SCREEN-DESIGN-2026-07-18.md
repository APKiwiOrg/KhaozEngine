# Reconnecting screen primitive (design)

Date: 2026-07-18
Status: shipped.

A reusable connection-outage UI primitive: a headless policy controller that decides banner vs full
screen vs nothing, plus an asset-free themeable screen that renders the full-screen case. Ruinborne
drove the requirement (replacing a thin "server is updating" overlay banner with a proper screen), but
both pieces are game-agnostic and every networked KE game reuses them.

The product decisions (scope, placement, escape hatch, look) were locked with the user before this work
started and are recorded in the consumer's design doc
(`~/Ruinborne/docs/superpowers/specs/2026-07-18-reconnecting-screen-design.md`). This doc covers only
the engine-side API decisions, which were delegated here.

## What ships

Five types in `KhaozEngine.Gui` (namespace `KhaozEngine.Gui`, files under `Connection/`):

| Type | Role |
|------|------|
| `ConnectionStatusController` + `ConnectionStatusPolicyOptions` | the brain: signals in, UI mode out |
| `ConnectionStatusSignals` / `ConnectionStatusView` | the per-frame data contracts |
| `ConnectionPhase` / `ConnectionUiMode` / `ConnectionStatusKind` | the enums |
| `ReconnectScreen` + `ReconnectAction` | the visual, a `Screen` for the `ScreenStack` |
| `ReconnectScreenTheme` + `ReconnectStrings` | customisation + engine-owned keys with English fallback |

Deliberately NOT shipped: a banner. `Mode == Banner` means "the consumer draws its own banner". Consumers
already have good ones, and standardising the banner is a separate future call.

## Decisions

### 1. Both halves live in `KhaozEngine.Gui`, not `KhaozEngine.Game`

The obvious reading of "sibling of `BootScreen`" puts this in `KhaozEngine.Game`, where `BootScreen`
lives. That is wrong here, and the consumer contract is what settles it.

`BootScreen` is a `GameScene` (the full-frame 3D-world + HUD stack owned by `SceneManager`). The
reconnecting screen is pushed onto the Gui `ScreenStack` alongside the consumer's existing pause,
settings, and server-status screens, so it must be a Gui `Screen`. `Screen` is also the only one of the
two abstractions with `PassUpdateThrough`, which the locked design calls for by name to suppress world
input while the scrim is up.

Once the visual is a Gui `Screen`, the brain follows it: `Gui` already references `App` (for
`LocalizedText`), the controller needs nothing else, and splitting one small feature across two packages
to save a file would cost consumers an extra reference for no benefit.

The netcode-free requirement is satisfied either way. The controller takes primitive signals, so `Gui`
gains no dependency on `NetWorld`, `Netcode`, or `ServerStatus`.

### 2. No engine-side signals adapter

The brief allowed an optional `WorldConnectionState` + `ServerStatusView` to `ConnectionStatusSignals`
adapter in some package that already depends on both. Skipped, on purpose.

The mapping carries game policy, not engine policy: which server-status states count as a *planned*
update, and whether an ETA is trustworthy enough to show a countdown, are per-game calls. Ruinborne
already writes and unit-tests its own mapper (`RuinborneConnectionSignals`). An engine adapter would add
a package that depends on both netcode and status for one speculative second consumer, and would bake one
game's policy into the engine. Left out until a second game actually wants the same mapping.

### 3. `ReconnectScreenTheme` is a `sealed class`, not a `struct`

The spec sketched a `struct`. Both sibling themes (`BootScreenTheme`, `UpdateOverlayTheme`) are
`sealed class` with `static T Default => new()`, and a mutable struct carrying a delegate field is a
well-known C# footgun (a copy silently diverges from the original). The consumer's usage pattern is
identical either way:

```csharp
ReconnectScreenTheme theme = ReconnectScreenTheme.Default;
theme.Scrim = new Vector4(0f, 0f, 0f, 0.55f);
return theme;
```

`ReconnectAction` stays a `readonly struct` (a tiny immutable pair with `init` accessors), because it is
a genuine value with no mutation story.

### 4. Text fields are `LocalizedText`, not `StringId`

The spec sketched `StringId` theme fields. The engine is moving player-facing Gui sinks to
`LocalizedText` so a bare string literal fails to compile, and `BootScreenTheme.Title` is already
`LocalizedText`.

This is not a break for the consumer: `LocalizedText` has an implicit conversion from `StringId`, so
`theme.ReconnectingTitle = new StringId(RuinborneStrings.ReconnectTitle)` compiles unchanged. Typing the
field as `LocalizedText` additionally buys the format-args path (`LocalizedText.Of`) and the greppable
`LocalizedText.Raw` escape hatch, which the status lines need.

`ConnectionStatusSignals.MessageId` / `ConnectionStatusView.MessageId` stay `StringId?`, because that one
is a dynamic key pushed from a server, not a themed literal.

### 5. `Create` takes an `IDesignViewport`

The specced signature omitted it, but a `Screen` cannot render without one. `ScreenStack` does not expose
a viewport to its screens, so every Gui screen that needs bounds is handed one at construction
(`UpdateOverlayScreen`, and the consumer's own `ServerStatusScreen`, both do exactly this).

Two distinct rects are needed, which is why the viewport rather than a plain `Rect` is the parameter:

- `WindowBounds` for the scrim, so the fill covers the letterbox bars. A scrim sized from the design rect
  leaves the world showing in the bars.
- `DesignBounds` for centring the content.

### 6. Timer hygiene: accumulate the drop, compute the countdown

Two clocks, deliberately handled differently.

The **drop timer** is a `float` accumulator, because "how long has this outage lasted" has no absolute
reference. It is bounded (it stops accumulating once it passes the escalation threshold, since nothing
reads a larger value) and resets to zero the moment `Phase` returns to `Connected`.

The **countdown** is recomputed from the absolute `EtaUtc` every frame and never accumulated, so it
cannot drift against wall-clock or freeze. At or past zero it clamps: the screen drops the countdown and
falls back to the reconnecting title rather than rendering a negative timer.

### 7. Anti-flicker is a floor, not a delay

`MinScreenDurationSeconds` holds the screen up for a minimum time once it has been shown, so a sub-second
reflap (`Phase` bouncing back to `Connected` and out again) cannot flash the takeover away and back.
It deliberately does NOT delay the *first* appearance of a planned-update screen, which is immediate by
the locked design.

The hold is released early only by a genuine sustained reconnect, which is what `Phase == Connected`
past the floor represents.

### 8. The spinner is a ring of axis-aligned dots

"Asset-free indeterminate spinner drawn from a 1x1 white texture" has an obvious implementation (rotated
segment quads) that the 2D batch does not support for this call shape: `batch.Draw(white, rect, color)`
is axis-aligned. Rather than add a rotation path for one ornament, the spinner places N small squares
around a circle (positions from `cos`/`sin`) and pulses their alpha on a phase offset per dot. Reads as a
rotating ring, needs no rotation, and stays a handful of quads.

Its animation clock is the screen's own elapsed time, wrapped modulo the spin period so it stays bounded
however long an outage runs.

### 9. `SpriteFont`, not `DpiFont`

The Gui widget layer (`Label`, `Button`, `Panel`, `GuiSurface.Button`) is `SpriteFont` throughout, and the
sibling screen on the same consumer stack (`ServerStatusScreen`) takes a `SpriteFont`. `BootScreen` carries
a second `DpiFont` overload because it is a `KhaozEngine.Game` scene drawing in the point-space UI pass.
Matching the widget layer keeps this to one code path. A DPI overload can be added later without a break
if the crispness gap shows up in practice.

## Testing

The controller is the valuable logic and is fully headless-tested in `KhaozEngine.Gui.Tests`: planned goes
to `Screen` immediately, generic goes `Banner` then `Screen` past the threshold, reconnect returns `None`
and resets the drop timer, the anti-flicker floor holds across a sub-second reflap, and the countdown math
including the at/after-zero clamp.

The screen is not unit-tested. It is a renderer with no decision logic left in it (the controller owns
every decision), and it is validated by the consumer running the client, per the locked design.

## Follow-ups

Filed rather than built, so they do not silently disappear with this doc (a design doc nobody is actively
working is not a ledger anyone reads):

- #220: standardising the thin banner into the engine, so consumers stop hand-rolling one. Parked until a
  second game wants it.
- #221: a `DpiFont` overload for the screen if HiDPI text crispness proves to matter. Unmeasured, so check
  it during the Ruinborne adoption before building anything.
