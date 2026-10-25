<!--
Canonical copy: game-template/docs/programs/README.md. Copied verbatim into every repo that carries
scripts/check-scratch.sh. Do not hand-edit a copy. Change it in game-template and re-propagate.
-->
# Programs

How this repo keeps the state of long-running work. Three tiers, each with one job.
`scripts/check-scratch.sh` enforces the parts a machine can check, from the pre-commit hook and CI.

## The three tiers

**Living docs** are the current truth about the system. They are named by topic, never by date, and
describe the system as it is. Superseded text is rewritten or deleted, because git history is the
archive. A reason or gotcha sits next to the behaviour it explains and stands alone, with no link into
scratch. A behaviour change and its doc update land in the same commit.

**Program trackers** hold the state of a long-running program until it is done. A program is work that
spans more than one landing, so more than one plan, session or milestone. A single-landing feature
needs no tracker. Its issue and its plan are enough. Each open program has one tracker at
`docs/programs/<name>.md`, kebab-case.

**Scratch** is specs, plans, subagent ledgers, brainstorm output, frames and contact sheets, under
`docs/superpowers/` and `.superpowers/`. Both are ignored. Nothing tracked lives under them, and no
tracked file names a file inside them. A bare mention of the folder in a rule is fine. A plan for a
program milestone is named `<program>-m<N>-<topic>` so a new chat finds it locally by name. Scratch is
disposable: a plan is needed only while its milestone is in flight.

## Tracker shape

A tracker has a `#` title and these `##` sections, in this order, and no others. Goal, Milestones and
Next step are required.

- **Goal**: what done means, in a few lines.
- **Links**: the roadmap issue by full URL, and the living docs this program changes.
- **Milestones**: a table of milestone, status, issue and owner gate. Status is current state only. The
  issue closes from its landing commit, so the table carries no sha or date history.
- **Decisions in force**: the owner rulings and design calls the remaining milestones depend on, with
  stable ids. A ruling already reflected in a living doc is removed from here.
- **Open questions and owner gates**: what is waiting on a decision, and whose.
- **Next step**: one short paragraph a new chat can act on.

A tracker never holds logs, per-task progress, review rounds, outcome narratives, evidence or routing
lessons.

## Caps

A tracker is at most 8192 bytes. A design file is at most 32768 bytes. A tracker that outgrows its cap
is carrying history. Cut it back to current state rather than raising the cap.

## Design file

A program may keep the approved design of milestones not built yet in `docs/programs/<name>-design.md`,
next to its tracker. When a milestone lands, the part of the design it built is rewritten as current
truth into the living docs and deleted from the design file, so the file shrinks to nothing as the
program completes. A design file without its tracker fails the check.

## Lifecycle

1. Created when the program is planned, with its roadmap issue linking to it.
2. Updated in the same commit as each milestone landing.
3. At completion, the closing commit moves every decision still in force into the living docs, deletes
   the tracker and its design file, and closes the roadmap issue.

A milestone, or any branch that changes documented behaviour, is done only when the living docs describe
the new behaviour, every ruling, reason and gotcha from its plan that still applies is in a living doc or
the tracker's decisions in force, and the tracker is updated in the same commit.

## Cross-repo programs

A program that spans repos has its tracker in the repo that drives it, usually the game. Milestones in
another repo are linked by full issue URL, never the short `owner/repo#N` form.

## Blank tracker

```markdown
# <Program name>

## Goal

<What done means, in a few lines.>

## Links

- Roadmap: <full issue URL>
- Docs changed: <living doc paths>

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 <name> | open | <full issue URL> | <gate or none> |

## Decisions in force

- <ID>: <ruling or design call>

## Open questions and owner gates

- <question, and who decides>

## Next step

<One short paragraph a new chat can act on.>
```
