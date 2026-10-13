# Gesture input probe

Opt-in desktop diagnostics for the full-frame `PannableCanvas` and `DraggableBottomSheet` APIs.
Uses only engine projects, an embedded engine font, a generated white texture and a bounded grid.
It contains no game assets or consumer implementation. All text is developer output.

Build from the engine root:

```sh
build-slot --label gesture-probe-build -- dotnet build tools/GestureInputProbe/GestureInputProbe.csproj -c Release
```

Owner-run command from this checkout, after the build:

```sh
build-slot --label gesture-probe-device -- dotnet run --project "$PWD/tools/GestureInputProbe/GestureInputProbe.csproj" -c Release --no-build
```

The implementation session compiles this tool but does not launch it. Headless tests prove
transforms and admission. They cannot prove physical event delivery, rendering or device feel.
Use the default desktop GPU backend. Normal engine backend selection remains available.

The header shows stream availability, scalar wheel totals, precise-scroll availability, magnify
factor, event source and phase, event-time command modifier, native momentum, raw framebuffer
anchors and mapped anchors. It retains four recent samples and logs a summary at most twice per
second. `AppWindow.NativeGestureFailure` is shown and logged when it changes. Empty available
frames are normal. Scalar summaries are diagnostics and are never applied alongside samples.

Mouse drag pans the grid. Plain wheel pans, Ctrl/Cmd+wheel zooms, precise scroll pans and magnify
zooms at its event anchor. The sheet updates before the canvas. Drag its handle to resize, or drag
the body to expand and then scroll. The host owns the white texture, font, theme colors and batch
lifetime. Grid and body draws are clipped, with balanced clips and batch passes.

Tab switches keyboard focus between map and sheet. Space toggles the focused sheet. Arrows and
plus/minus navigate the focused map. C cancels both gesture owners. R cancels and centres the map.
Escape closes the tool.

Device checks, awaiting owner validation:

- macOS magnify over an off-centre grid intersection. It should remain under the fingers and show
  `MacOSNative` magnify samples with scale factors around 1.
- macOS two-finger pan in both axes, then release into momentum. Confirm `PreciseScroll`, phases,
  window-point deltas and momentum flags, with no second engine fling after native momentum ends.
- Begin on the sheet, then move outside it. Confirm no grid movement or map tap. Expand fully,
  scroll body content, unwind its offset and collapse. Confirm exact boundary handoff and handle taps.
- While dragging or zooming, lose focus, resize or press C. Held contacts must release before
  restarting. Return to the map with a fresh sequence. Move across DPI scales and inspect mapping.
- Windows Ctrl+wheel over an off-centre intersection. Confirm `ModifierWheelCompatibility`, the
  event-time command flag and one zoom effect even when Ctrl is released before the next frame.
  Check ordinary two-axis wheel behavior separately. Full native Windows precision pan is outside scope.

See [the consumer contract](../../docs/USING-KHAOZENGINE.md#pan-and-zoom) for units, ownership,
legacy canvas compatibility and generic target examples. Flightline adoption belongs to
[Flightline #3](https://github.com/APKiwiOrg/Flightline/issues/3).
