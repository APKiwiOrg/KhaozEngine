# File-Size Ratchet as a Roslyn Analyzer (KESIZE)

Status: in progress. Issue: #254. Ships in 14.6.0.

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
script layers still cover those files.

## Fleet rollout

Engine 14.6.0 ships the package and its own adoption. game-template documents the compile-time
layer (script header and CODE-LAYOUT-STANDARD.md) with no wiring change. Each game adopts by pin
bump plus refresh-engine.sh, verified by a deliberate KESIZE001 fire-and-revert probe in that repo.
Template pin lag (10.90.0) means scaffolded games are born analyzer-less until the template repin
lands, filed as a game-template follow-up issue.
