# Content contracts: the shared ground under Scope A and Scope B

**Status:** CONTRACTS DRAFT, awaiting owner gate 0. Nothing here is implemented and nothing is approved.
This document exists so the two parallel specs cannot contradict each other. Scope A is the versioned
content catalog, [#882](https://github.com/APKiwiOrg/KhaozEngine/issues/882). Scope B is owned item
instances, affixes, sockets, crafting and the stat evaluation base,
[#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884). Consumers are
[Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208) and
[Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465). Neither spec is implemented before
the owner approves both.

Every rule below is written to be coded against: types, widths, byte order, ranges and reserved values.
Each contract section carries the rule, the engine precedent it follows or deliberately departs from with
the file cited, a short rationale, an explicit note on whether the decision is expensive to change once
data exists, and any question that has to go back to the owner.

The file-and-line citations come from three read-only surveys taken on 2026-09-14 against
KhaozEngine `1eb60de7`, Grimhollow `36f0139a` and Ruinborne `1b85e1aa`. Where a survey and this document
disagree, the survey is the fact and this document is the decision.

## 1. Purpose, scope, and the two programs

### 1.1 What this document is

Scope A and Scope B are being specified at the same time by different authors. They share an id space, a
byte format, a version stamp, a validator, a visibility vocabulary and a connect door. If each spec
invents its own, the first integration discovers a contradiction that costs a re-spec. So the shared
pieces are decided here, once, before either spec is written.

A spec may refine anything below. A spec may not contradict anything below. Section 18 says what happens
when one needs to.

### 1.2 What this document is not

It is not a design for either program. It says nothing about the authoring store's schema, the publish
pipeline's stages, the crafting primitive set, the affix table shape, the pack transport or the admin
console. Those are the specs' work. Where a contract constrains one of them, it says so and stops.
