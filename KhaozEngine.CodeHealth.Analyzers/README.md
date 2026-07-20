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

## Adoption

Zero-touch. The package ships buildTransitive props that discover the consuming repo's
`.filesize-baseline` by walking up from each project directory and hand it to the analyzer as an
AdditionalFile. A repo with no baseline has not adopted the ratchet and the analyzer stays silent.
Create a baseline with `scripts/check-file-size.sh --init`.

Knobs, both MSBuild properties: `KhaozFileSizeCap` overrides the 800-line cap for unlisted files,
`KhaozFileSizeBaselineDir` overrides the discovered baseline directory. One baseline per repo.

Blessing a deliberately large new file, or deliberate growth, is a hand-edit of
`.filesize-baseline`, which shows up in review. There is no environment-variable escape hatch on
purpose.

Flows to consumers via all four umbrellas (`Foundation`, `Game2D`, `Game3D`, `Server`).
