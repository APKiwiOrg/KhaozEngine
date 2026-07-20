# KhaozEngine.CodeHealth.Analyzers

Compile-time enforcement of the fleet's file-size ratchet (the god-class guard). The semantic
authority is `scripts/check-file-size.sh` in game-template: this analyzer applies the same rules in
the build and the IDE, where they cannot be bypassed by skipping a git hook.

## Diagnostics

| Id | Severity | Fires when |
|----|----------|------------|
| KESIZE001 | Error | A file listed in `.filesize-baseline` exceeds its recorded line count. A baselined file may shrink freely, it may never grow. |
| KESIZE002 | Error | A file NOT in the baseline exceeds the cap (default 800 lines). |

Line count is `wc -l` parity: the number of newline characters. Exclusions match the script:
`obj/`, `bin/`, `vendor/` path segments and `.Designer.cs`, `.g.cs`, `.generated.cs`,
`.AssemblyInfo.cs` suffixes, plus anything Roslyn marks as generated code.

Both messages carry their own remediation inline rather than only in the descriptor description,
because MSBuild prints the message and not the description: guidance an agent cannot see is guidance
that does not exist. The remediation routes a legitimate case to "ask the repo owner", never to a
split that exists only to silence the error.

## Exemptions (14.8.0)

A baseline line of the form `exempt <path>` takes a file out of both rules: no frozen size, no cap,
no diagnostic. Put the reason on a `#` line above it, since the path is the rest of the line.

```
# size is content, not structure: regenerated wholesale, one row per ISO country code
exempt MyGame.Content/Generated/CountryCodes.cs
```

This exists for files whose size is CONTENT rather than STRUCTURE (a generated lookup table, an
embedded data blob), where freezing the size only pressures the next contributor into splitting at an
arbitrary line, which is the failure the ratchet exists to prevent. It is not for a test fixture that
accreted cases or a screen or frame-loop class: those should be split by responsibility, and the
ratchet pressing you to do it is working correctly.

**The test is growth, not syntax.** Does the file grow only when the DATA grows, or also whenever its
subsystem gains a feature? Answer from `git log`, not from what the file looks like. "It is all
constants" is not the test: the engine's own `ShaderSources.cs` was 2624 lines of nothing but
`const string` with no logic, and was still the wrong candidate, because it grew with every renderer
feature. Constants that encode behaviour are structure. It was split by render domain in 14.8.1
rather than exempted.

An exempt entry wins over a numeric entry for the same path in either order. `--init` and
`--preview` never emit one, and `--update` preserves the ones you wrote, so an exemption is only ever
a deliberate hand-edit. A consumer pinned below 14.8.0 does not understand the line and reads the
path as unlisted, so it reports a cap violation instead: loud and correctly shaped rather than
silently wrong, but adopt exempt lines only once you are on a pin that understands them.

## Adoption

Zero-touch. The package ships buildTransitive props that discover the consuming repo's
`.filesize-baseline` by walking up from each project directory and hand it to the analyzer as an
AdditionalFile. A repo with no baseline has not adopted the ratchet and the analyzer stays silent.
Create a baseline with `scripts/check-file-size.sh --init`.

Knobs, both MSBuild properties: `KhaozFileSizeCap` overrides the 800-line cap for unlisted files,
`KhaozFileSizeBaselineDir` overrides the discovered baseline directory. One baseline per repo.

Blessing a deliberately large new file, or deliberate growth, is a hand-edit of
`.filesize-baseline`. There is no environment-variable escape hatch on purpose. In the engine and in
game-template that hand-edit is additionally gated by a write-time agent hook that turns it into a
confirmation prompt, so an agent cannot raise a frozen size or grant an exemption silently inside a
large diff. Ratcheting DOWN stays free: `scripts/check-file-size.sh --update` can only lower or drop
entries, never raise one.

Flows to consumers via all four umbrellas (`Foundation`, `Game2D`, `Game3D`, `Server`).
