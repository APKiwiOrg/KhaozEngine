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
STRUCTURE was never the target. `ShaderSources.cs` (2624 lines of const shader strings) is the
exemplar: freezing it means every legitimate shader addition either interrupts the user or pressures
someone into an arbitrary split.

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
