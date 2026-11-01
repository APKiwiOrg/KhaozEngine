# KhaozEngine.CodeHealth.Analyzers

Compile-time file-size and test-health ratchets in the build and IDE. The semantic authority for
the file-size ratchet (the god-class guard) is `scripts/check-file-size.sh` in game-template. The
test-health ratchet counts brittle assertions and non-public reflection against a separate baseline.

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

## Test-health ratchet

KETEST rules run only in compilations that reference xunit and have a `.testhealth-baseline` supplied
as an AdditionalFile. Without that file they stay silent, including in report mode. Consumers adopt
by adding the file. The package's buildTransitive props discover it by walking up from each project
directory. `KhaozTestHealthBaselineDir` overrides that discovered directory independently of the
file-size baseline.

| Id | Mode | Counts and exclusions |
|----|------|-----------------------|
| KETEST001 | Error above the per-file allowance | Each `Exception.Message` access inside `Assert.Contains`, `Equal`, `NotEqual`, `StartsWith`, `EndsWith`, `Matches`, `True` or `False`. `DoesNotContain` and `DoesNotMatch` absence checks never count because they are disclosure contracts. Unrelated properties named `Message` do not count. Prefer the exception type and structured members such as parameter name, error code or status. |
| KETEST002 | Error above the per-file allowance | Each `BindingFlags.NonPublic` reference and each `UnsafeAccessor` attribute application. Public-only reflection does not count. Prefer the public API or an internal seam exposed through `InternalsVisibleTo`. |
| KETEST003 | Report-only review aid | Test methods whose every direct assertion runs inside a loop or `Assert.All` over a source that is not provably non-empty. Proven non-empty loops and tests with an assertion outside those loops do not count. Assert the expected count or non-emptiness before iterating. |

KETEST003 appears only with `-p:KhaozTestHealthMode=report`, never gates normal builds and never enters
the baseline. Only 6 of 20 sampled findings in this repository were real, because loops over local
tables and test-owned case helpers look possibly empty to a syntactic check. Review its candidates
instead of treating them as enforced debt.

Both ratchets use `AnalyzerPathPolicy` for baseline-relative paths and the generated and vendored
file exclusions listed above. The baseline grammar is `<count> <rule> <path>` or
`exempt <rule> <path>`, with paths relative to its directory and a `#` reason line above each exemption:

```text
2 KETEST001 MyGame.Tests/ValidationTests.cs
1 KETEST002 MyGame.Tests/LegacyStructureTests.cs
# Disclosure wording is the contract for this test file. Approved by the repo owner.
exempt KETEST001 MyGame.Tests/DisclosureTests.cs
```

KETEST001 and KETEST002 have separate per-file counts, with zero allowed for an unlisted pair.
Exemptions and any baseline growth require owner approval. The exception path for a legitimate
wording or structural-reflection contract is an approved exemption, never a suppression. An exemption
wins over a numeric entry for the same rule and path. KETEST003 is never baselined.

`scripts/check-test-health.sh` supports three modes, each building a non-incremental Release report:

- `--init` records initial KETEST001 and KETEST002 counts. It refuses an existing numeric baseline and
  preserves adoption-time comments and exemptions. Initial adoption needs owner approval.
- `--preview` prints reductions and growth without writing the baseline.
- `--update` lowers counts or drops resolved entries, preserves exemptions and never raises counts.
  Preview and update return a failure for unapproved growth.

Ratchet down through the shared build queue:

```sh
build-slot --heavy --label testhealth-update -- sh scripts/check-test-health.sh --update
```

Report mode emits non-exempt findings as warnings without subtracting baseline allowances, including
KETEST003 candidates. To review them without promoting report warnings to errors:

```sh
build-slot --heavy --label testhealth-report -- dotnet build KhaozEngine.slnx -c Release --no-incremental -p:KhaozTestHealthMode=report -p:TreatWarningsAsErrors=false
```

A `.testhealth-baseline` change forces a FULL selective CI run. Tests marked
`[Trait("Category", "Diagnostic")]` report instead of gating, so default CI excludes them with
`Category!=LiveSocket&Category!=Diagnostic`. Run them on demand through the `cross-platform-gpu`
workflow's `renderTestFilter` dispatch input set to `Category=Diagnostic`.
