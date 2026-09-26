# Committed Unicode text input

This is the layout and dead-key slice of [#61](https://github.com/APKiwiOrg/KhaozEngine/issues/61).
It leaves preedit display and candidate selection on the issue.

## Problem

`TextEntry` derives characters from physical `Key` values using a US layout. The mapping types the
wrong symbol on other layouts and cannot receive the character committed after a dead-key sequence.
`AppWindow` already captures physical keys through Silk.NET and GLFW, then publishes `InputState`
through `InputAccumulator`. That snapshot has no text channel.

The frozen `AppWindow.cs` file cannot grow under KESIZE. Its two GLFW keyboard callbacks therefore
live together in a cohesive `AppWindow.GlfwKeyboard.cs` partial file.

## Choice

Use GLFW's Unicode character callback on GLFW windows. It reports committed code points according
to the active OS layout and dead-key processing. `AppWindow` chains the existing callback just as it
does for key repeat, so Silk.NET keeps its own event path. A per-frame UTF-16 string and a source
availability flag travel through `InputAccumulator` and `InputState`. The flag matters even when the
string is empty. A dead key must not fall through to the old US key map in that frame.

`TextEntry` consumes committed text when the source is available and uses its current key map only
when it is unavailable, including headless snapshots. Backspace and paste remain key actions.
MaxLength continues to count UTF-16 units. A non-BMP scalar is admitted or rejected as a pair,
including when a filter is supplied, so editing never creates a lone surrogate. Backspace removes
one scalar. The existing `Func<string, char, bool>` filter API remains intact. Right Alt with Ctrl
may represent AltGr, so committed text from that chord is allowed while ordinary Ctrl and Super
shortcuts stay suppressed.

The alternatives were to extend the US map with layout tables or use Silk.NET's `KeyChar` event.
Layout tables cannot reproduce the OS input method and dead-key state. `KeyChar` exposes a UTF-16
`char`, which cannot carry one non-BMP scalar on its own. The GLFW callback already exists in the
package, returns full code points, and keeps OS-specific input in `AppWindow`.

| Source | Text correctness | Seam fit | Compatibility | Total |
|---|---:|---:|---:|---:|
| GLFW committed code points | 9 | 9 | 8 | 26 |
| Silk.NET `KeyChar` | 6 | 8 | 8 | 22 |
| Expanded key map | 2 | 3 | 10 | 15 |

## Boundaries and verification

`InputState` remains immutable and its new constructor arguments are optional for existing
headless callers. `InputState.WithoutScroll` and the dev automation snapshot composer preserve
committed text. `InputAccumulator` clears
pending text on snapshot and focus loss. Invalid Unicode scalar values are ignored.

Headless tests cover accumulation, frame clearing, focus loss, empty committed frames, layout
characters, dead-key outcomes, shortcut suppression, AltGr, filtering, max length, and non-BMP
deletion. The GUI package and consumer docs describe the new behavior and its preedit limit.
The staged 20.6.1 package version is reused, with a changelog entry and the normal Release tests,
whole-tree guards, and local pack. No release tag is created.
