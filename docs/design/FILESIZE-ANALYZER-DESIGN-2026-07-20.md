# File-Size Ratchet as a Roslyn Analyzer (KESIZE)

Status: shipped in 14.6.0, revised in 14.8.0 (see "14.8.0 revision" at the end). Issue: #254.

## Problem

The fleet's file-size ratchet (scripts/check-file-size.sh in game-template and the four games) is
shell-layer only. Every local layer is bypassable (FILESIZE_OK=1, --no-verify, another IDE), so the
first unbypassable gate is CI, which is also the most expensive place to find out. Origin incident:
Ruinborne v0.9.4's release tag died in CI on Program.cs 1478 vs baseline 1469 after four commits
passed the local hook (https://github.com/APKiwiOrg/Ruinborne/issues/138).

## Decision

A Roslyn analyzer, KhaozEngine.CodeHealth.Analyzers, enforcing the same ratchet at compile time as
errors: KESIZE001 (a file listed in .filesize-baseline exceeds its recorded size) and KESIZE002 (an
unlisted file exceeds the cap, default 800, overridable via the KhaozFileSizeCap compiler-visible
property). IDE-visible, fires on every build, and cannot be skipped by skipping a hook.

The package is named CodeHealth, not FileSize, deliberately: it is the home for future code-health
diagnostics (type-level god-class metrics like lines per type would land here as KESIZE003+), so a
second rule does not force a second package. Only the file ratchet ships now.

## Semantics: parity with check-file-size.sh

The script is the semantic authority and stays in service, so the analyzer mirrors it exactly:

- Line count is the number of newline characters (wc -l parity). A final line without a trailing
  newline does not count. Implemented as a character scan, not SourceText.Lines.Count, which is off
  by one against wc for files not ending in a newline.
- Baseline parsing matches the script's awk reader: a line whose first whitespace-delimited field is
  all digits is an entry, the path is the rest of the line (spaces allowed), everything else is
  skipped silently, and the first entry for a path wins.
- Exclusions match is_excluded: obj/, bin/, vendor/ path segments and .Designer.cs, .g.cs,
  .generated.cs, .AssemblyInfo.cs suffixes. The analyzer additionally skips trees Roslyn marks as
  generated code, which makes it slightly more lenient than the script on auto-generated headers.
  That direction is safe: the script remains as CI belt-and-braces, so more-lenient can never let a
  violation ship, while more-strict would have broken adoption.
- No .filesize-baseline AdditionalFile means the repo has not adopted the ratchet and the analyzer
  is silent, exactly like the script's missing-baseline skip. Adoption stays a deliberate act.
- A baselined file may shrink freely with no diagnostic and no baseline edit, matching the ratchet.
  Blessing growth is a hand-edit of the baseline, visible in review.

Two deliberate divergences beyond the generated-code leniency: the analyzer sees every compiled
file, including not-yet-tracked ones the script's git ls-files pass misses (stricter earlier, the
file would have been caught at first commit anyway), and the cap override is an MSBuild property
(KhaozFileSizeCap) where the script reads the FILESIZE_CAP env var. No repo overrides the cap today.
A repo that does must set both.

## Wiring

- The package packs the analyzer dll under analyzers/dotnet/cs with no lib output, the same shape as
  KhaozEngine.Localization.Analyzers.
- All FOUR umbrellas (Foundation, Game2D, Game3D, Server) reference it with PrivateAssets="none".
  KELOC rides only Game2D/Game3D, which would have missed the exact head that motivated this work
  (Ruinborne.Server references the Server umbrella). A direct include-all edge from every umbrella
  also removes any dependence on multi-hop transitive analyzer-asset flow.
- Zero-touch consumer adoption: the package ships buildTransitive/KhaozEngine.CodeHealth.Analyzers.props,
  which discovers the consuming repo's .filesize-baseline with GetDirectoryNameOfFileAbove from each
  project directory and adds it as an AdditionalFile, plus surfaces KhaozFileSizeCap as a
  compiler-visible property. A game adopts by bumping its engine pin, nothing else. The discovered
  directory can be overridden with KhaozFileSizeBaselineDir. One baseline per repo.
  Rejected alternative: hand-adding AdditionalFiles wiring to every consumer's Directory.Build.props,
  which is N repos times every future game worth of copies of the same three lines.
- Engine dogfood: the engine adopts its own ratchet. Directory.Build.props applies the analyzer to
  every engine project as a ProjectReference with OutputItemType="Analyzer" (opt-out property
  KhaozSkipFileSizeAnalyzer for the analyzer itself, its tests, and the four umbrellas, which
  reference it as a packed dependency instead) and wires the repo-root .filesize-baseline, generated
  with the script's --init. A green engine build across every project is the parity proof for the
  line-count implementation.

## What stays, what the analyzer does not claim

The shell layers stay: pre-commit staged mode, pre-push --tree, CI --tree, and the agent write-time
--file hook. They fire earlier than a build and cost nothing to keep. Revisit retirement after the
analyzer has baked across the fleet for a few releases.

Honest bypass surface: an .editorconfig severity override, NoWarn, or building with
-p:RunAnalyzers=false can still silence the analyzer locally. Every one of those is a reviewable
diff or a nonstandard build invocation that CI does not use, so the CI trust model is unchanged
while the default local and IDE experience becomes unbypassable-by-accident. The FILESIZE_OK=1
hook idiom has no analyzer equivalent on purpose: the blessing mechanism is the baseline hand-edit.

Known coverage limit: a consumer project that references no engine umbrella gets no analyzer. The
script layers still cover those files. A related edge: a toolchain that hands the compiler relative
or PathMap-remapped source-tree paths would fail the baseline-root prefix match and silently degrade
to script-only enforcement, which the CI script backstop still covers.

## Fleet rollout

Engine 14.6.0 ships the package and its own adoption. game-template documents the compile-time
layer (script header and CODE-LAYOUT-STANDARD.md) with no wiring change. Each game adopts by pin
bump plus refresh-engine.sh, verified by a deliberate KESIZE001 fire-and-revert probe in that repo.
Template pin lag (10.90.0) means scaffolded games are born analyzer-less until the template repin
lands, filed as a game-template follow-up issue.

## 14.8.0 revision

Three things the 14.6.0 design got wrong, found by asking whether the ratchet could be too strict.

### The guidance was invisible, so the analyzer pushed toward the failure it warned about

The remediation ("put it in its own type", "do not split at an arbitrary line") was written into the
descriptor's `description`. MSBuild prints `messageFormat` and not `description`, so anything reading
build output, which is every agent and every CI log, saw only a bare number comparison. Verified by
growing a baselined file and reading the console: the message ended at "(this file may shrink, not
grow)".

That is worse than a missing nicety. An agent that hits the error, has no guidance, and does not want
to interrupt the user will split the file at whatever line makes the error stop. That is exactly the
two-god-halves outcome the ratchet exists to prevent, so the analyzer was actively producing the
failure mode its own unread `description` warned about. Both messages now carry their remediation
inline and both descriptors set `HelpLinkUri` (MSBuild prints the link too). `description` keeps only
what the message does not already say.

### "Visible in review" was not true here

The 14.6.0 blessing mechanism was a hand-edit of `.filesize-baseline`, justified as "a deliberate act
with a reviewable diff". That assumes a human reviewer. In this fleet an agent routinely authors,
merges, and pushes to `main` in one pass, so a one-line baseline raise buried in a large diff is seen
by nobody. The blessing was silent, which made the ratchet's whole strictness story rest on an
enforcement step that was not happening.

Fixed with agent write-time hooks in `.claude/settings.json` and `.codex/settings.json`, in this repo
and in game-template. They emit `permissionDecision: "ask"`, deliberately NOT `"deny"`. Deny is the
right shape for the em-dash and retired-backlog-file guards, where there is no legitimate case. Here
there is: the whole point is a workaround the user can approve, and a hard deny would leave no path
forward after they say yes.

Two hooks, because one was not enough. The `Write|Edit` hook covers the obvious edit. A `Bash` hook
covers shell writes, which the first one misses entirely: the gap was found by writing to the
baseline with `printf >>` during verification and getting no prompt. The Bash hook asks only when the
command both names `.filesize-baseline` and contains a write-shaped token, so reads stay free, and
`scripts/check-file-size.sh --update` stays free without needing an exception because its command
text never names the file. That asymmetry is the design: tooling-driven ratcheting DOWN is free,
hand-edited growth is confirmed.

Honest limit, unchanged in spirit from the bypass note above: a compound command that hides a write,
or an agent that edits via some path neither matcher sees, still gets through. These hooks stop the
casual and accidental path, which is the one that actually happens. They are not an adversarial
control.

### The ratchet was a category error for content-shaped files

The ratchet's own rationale is about frame-loop owners and screens: classes that accrete features
because they are the cheapest place to put the next one. A file whose size is CONTENT rather than
STRUCTURE was never the target. Freezing such a file means every legitimate addition either
interrupts the user or pressures someone into an arbitrary split.

`ShaderSources.cs` (2624 lines of const shader strings) was used as the exemplar here when this
section was written. **That was wrong, and 14.8.1 both corrected it and turned it into the rule's
best teaching case.** See "14.8.1: the exemption test is growth, not syntax" below.

So `.filesize-baseline` gains an `exempt <path>` line form, taking a file out of both rules
entirely. Design points worth keeping:

- An exemption is granted ONCE per file, where a raise is paid per growth event. That asymmetry is
  the reason to have it at all: it makes the honest case cheap and the debt case expensive.
- `exempt` wins over a numeric entry for the same path in either order, since it is the more explicit
  statement. `--init`/`--preview` never emit one and `--update` preserves them, so an exemption is
  always a deliberate hand-edit, and therefore always passes the confirm hook above.
- The reason goes on a `#` line above the entry, not trailing, because the path is the rest of the
  line (the same tolerance numeric entries already have, so paths may contain spaces).
- Tests are explicitly NOT exemption candidates. A fixture that accreted cases should be split into a
  test class per feature area, so the pressure there is correct and the wording says so.

Rejected: a richer per-entry annotation format. It would have made the script's awk reader and the
analyzer's parser both meaningfully more complex to serve one case, when a distinct keyword line that
existing parsers already skip does the same job.

Compatibility, accepted rather than engineered around: a parser that predates this (an old engine
pin, an un-refreshed script) skips the `exempt` line silently and reads the path as unlisted, so an
over-cap file reports a cap violation. That is a loud, correctly-shaped failure rather than silent
corruption, which is the right direction to fail. Documented in the baseline header, the package
README, and USING-KHAOZENGINE.md.

### Correction to the 14.6.0 text above

The generated-code-leniency argument claims "the script remains as CI belt-and-braces, so
more-lenient can never let a violation ship". That is true in the game repos. It is NOT true in this
one: the engine has no shell file-size layer at all (no pre-commit, no pre-push, no CI invocation of
`check-file-size.sh`, which here is baseline-management only). In the engine the analyzer is the sole
enforcement, so its leniencies have no backstop. Left as is, since the leniencies are narrow and
erring lenient in the repo that owns the analyzer is the safer direction, but the claim as written
was wrong and should not be relied on.

**Superseded by #554.** The engine's `ci.yml` now runs `check-file-size.sh --tree` in its convention
step, on every push and PR, both the selective and the full path. So the CI backstop this correction
said did not exist does exist, and the original 14.6.0 claim holds here as well. The gap it closes is
real rather than theoretical: the selective path compiles only the affected slice, so the analyzer
never sees an unaffected project's growth on a normal push.

## 14.8.1: the exemption test is growth, not syntax

14.8.0 shipped the exemption with `ShaderSources.cs` as its canonical example, in this doc, the
script's usage comment, the generated baseline header, the package README, USING-KHAOZENGINE.md and
game-template's CODE-LAYOUT-STANDARD. The very first time the rule was applied to a real file, that
example turned out to be wrong, and wrong in the worst direction: it named as the poster child a file
that FAILS the rule.

### Why it looked like a candidate and was not

Every syntactic signal said content. 2624 lines, 47 `public const string` members, zero methods, zero
C# control flow (the 115 `if`/`for`/`return` hits are all GLSL inside verbatim strings). If the test
were "is it all constants", it passes outright.

Its history says the opposite:

```
2026-07-17  2327   cross-cascade blend band in sampleKeyShadow
2026-07-17  2451   void decals
2026-07-18  2492   shadow atlas construction seam
2026-07-18  2610   MoltenCracks Voronoi decal fill
2026-07-20  2624   per-instance dissolve
```

About 300 lines in three days, every one of them a renderer FEATURE. That is behaviour accreting
exactly the way a frame-loop owner accretes it, differing only in being written in GLSL rather than
C#. Exempting it would have removed the ratchet from the fastest-growing file in the engine, which is
the precise opposite of what the ratchet is for.

So the criterion is restated everywhere as a GROWTH test, answered from `git log` rather than from
what the file looks like: does it grow only when the DATA grows, or also whenever its subsystem gains
a feature? Constants that encode behaviour are structure. The worked example is now a genuinely
static one (a regenerated country-code table), and the near-miss is written up as the counter-example
because the next reader will have the same instinct.

### The alternative that was taken instead

Split by render domain into `ShaderSources.<Domain>.cs` partial-class files: Lighting, Model,
Terrain, Shadow, Effects, Post, Decal, Sky. These are responsibility boundaries, not line-count cuts,
which is what makes this a legitimate split rather than the two-god-halves failure. `partial` keeps
all 158 external call sites working untouched, and const-concatenation across the partials
(`ModelFrag` splicing in `LightingCommonGlsl`) still resolves at compile time exactly as before.

The ratchet result: `.filesize-baseline` went from 24 entries to 23. The 2624-line entry did not get
frozen at a lower number, it left the debt list entirely, because every partial is comfortably under
the cap. Growth signal is now per-domain, so "the decal shaders are growing" is reportable in a way
"ShaderSources is growing" never was.

### How the move was verified

A pure-move refactor of 2600 lines of shader text is exactly where a silent one-byte change becomes a
rendering bug that no unit test catches, so it was proven three independent ways:

1. **Source-level reconstruction.** The split was performed by a script that reassembles the original
   member region from the pieces it is about to write, and refuses to write anything unless the
   reassembly matches the source byte-for-byte. It also refuses if the declared groups are not in
   source order, which caught one real ordering mistake (`Decal` placed before `Post`).
2. **Compiled-value equality.** A temporary harness reflected over every `public const string` on
   `ShaderSources` and recorded length plus SHA256 per member, before and after. All 47 matched
   exactly. This is the strongest of the three, because it proves the values the compiler actually
   produces, including the cross-file const splicing, rather than the text that produced them.
3. **GPU goldens on real Metal.** `KE_GPU_TESTS=1` turns 250 otherwise-skipped tests on. All 250 ran
   and passed. Worth noting for future work here: a plain `dotnet test` SKIPS every GPU golden, so a
   shader change that only ever saw the default suite has not been visually verified at all.

### Residual case

`DecalFrag` alone is 449 lines and is one indivisible const, so `ShaderSources.Decal.cs` is ~500 and a
future decal feature could push that single file toward the cap with no further split available.
That is the one place in this family where an exemption might genuinely be the right answer later.
It is not the right answer for a 2624-line grab bag today.
