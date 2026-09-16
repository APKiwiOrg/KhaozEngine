# KhaozEngine.ItemInstances.Journal

The journal side of the per-item instance record: how a paged container is NAMED in a stream's projection
sections, how it is LOADED back out of them, and how a tick's worth of operations against it becomes ONE
commit.

In the `Server` umbrella, over `KhaozEngine.ItemInstances` and `KhaozEngine.WorldStore`. The container
itself, the page codec, the remap pass and the visibility projection are `KhaozEngine.ItemInstances`, one
package down in `Foundation`, and nothing here re-states any of them.

## Why this is a package of its own

**It exists for a layering reason rather than a size one.** Composing a `JournalCommit` needs
`KhaozEngine.WorldStore`, which is a `Server` package. Putting this code inside `KhaozEngine.ItemInstances`
would drag `WorldStore` into `Foundation` and therefore into every client build. Putting it inside
`KhaozEngine.WorldStore` would give the journal an opinion about items, which the journal design ruled out and
which the code still honours: a grep for `ItemStack`, `ItemContainer` or `KhaozEngine.Items` across
`WorldStore`, both providers and both journal design docs returns zero hits. A third small package in the
`Server` umbrella keeps both properties.

## Section names

A page of a container is one projection section, named `<container>/p<NN>`, zero padded to two digits and
unpadded above page 99. A 1,000 slot bank is `bank/p00` through `bank/p09`, a 30 slot bag is `bag/p00`, an 11
slot worn set is `worn/p00`.

```csharp
string name = ContainerSectionNames.Format("bank", 9);            // bank/p09
ContainerSectionNames.Format("bank", 563);                        // bank/p563
ContainerSectionNames.TryParse(name, out string? container, out int pageIndex);
ContainerSectionNames.IsPageOf(name, "bank", out pageIndex);      // the question a load asks of a section
```

**Nothing needs escaping.** A journal section name is an identity capped at
`JournalLimits.EngineMaximumIdentityCharacters` characters over `[A-Za-z0-9._:/-]`, and the slash is in that
set. `Format` refuses BOTH halves of that rule, a container name outside the character set and a formatted
name over the cap, so a name this type writes is always a name the journal accepts, and a container name
carries no slash of its own so one level keeps the parse unambiguous. The length bound is on the name `Format`
WRITES rather than on the container, because the suffix is three characters at page 0 and five at page 100.
Without it the refusal arrives from the journal at the far end of a commit, with the batch already closed and
its pages already dirty.

`ContainerCommitBuilder.Open` asks `Format` for each container it is opened over, at page 0, so a name that
cannot be a section name never reaches a commit.

**`TryParse` is the ONE place a name is taken apart** and it is CANONICAL rather than tolerant: it accepts
exactly what `Format` writes, so `bank/p007` is refused rather than read as page 7. A tolerant parse would let
two readers disagree about which section a page belongs in, which is the failure the page-against-section
check exists to catch. It answers false rather than throwing, because a section name arrives from a store.

## Loading a container

```csharp
var context = new ContainerLoadContext(
    streamKey, "bank", properties, types, rules, stackable, logger, counter);
ContainerLoadResult result = ContainerLoad.Load(read.Sections, snapshot, context);
```

`Load` takes the whole projection read and keeps the sections that are this container's pages, so a caller
never re-implements the naming rule to filter first. It takes its whole world as arguments: no store read, no
file read, no ambient static. The order is the design:

1. **Decode the page.** A page that fails at the PAGE level (bad version, bad header, truncated, entries out
   of order) is quarantined as a UNIT, because a page that cannot be parsed has no entries to keep. It is not
   in `Pages`, nothing that did not load is ever written back, and the stored bytes stay as they are.
2. **Check the page against its section.** The header's own page index against the one the section name
   declares. The codec's `FirstSlot` check catches a header that disagrees with itself and cannot see a
   section name at all, so this is the half that needs the name.
3. **Apply the rules**, every rule whose `IntroducedIn` is strictly greater than the page stamp, in sequence
   order, in one pass. A rule that changed something marks the page DIRTY and moves its in-memory stamp. The
   page is not written: the rewrite is lazy and rides the next ordinary commit.
4. **Unwrap and re-offer every quarantined entry**, at the WRAPPER's own stamped version.
5. **Validate** the page's LIVE entries, then wrap what the validator quarantined, then emit one log line and
   one counter increment per record for the whole container. A quarantined entry is not swept again: its
   verdict is already stored, and the validator would read its wrapper as a payload and report the wrong
   reason ([#936](https://github.com/APKiwiOrg/KhaozEngine/issues/936)).

**Rules run BEFORE the validator and that is what gives a drift finding its meaning:** a
`unknown-definition` or `unknown-content-reference` finding means NO RULE COVERED IT.

**Nothing here throws for a stored byte.** A page that fails is a finding, an entry that fails is a finding,
and the exceptions this can raise are all about the arguments: a null, or a rule set the publish validator
should have refused.

## What comes back

| On `ContainerLoadResult` | What it is |
|---|---|
| `Pages` | every page that decoded, ascending by page index. A page that failed whole is not here |
| `Reports` | one `InstanceValidationReport` per page, the validator's own accumulated findings |
| `Findings` | everything the validator cannot say, because it never saw it |
| `Dirty` | the pages that owe the next ordinary commit a rewrite |
| `QuarantinedRecords` | how many records are out of play |

A `ContainerLoadFinding` is one of five kinds. `PageQuarantined` is a page that failed as a unit.
`EntryQuarantined` is an entry that carries a `KECQ` wrapper and still does, and it is also what an entry
flagged quarantined over NO payload answers: there is no wrapper behind the flag to carry a reason or a
stamp, so the reason is `field-malformed`, and the entry is left out of the page rather than seated live,
which would clear the flag. The page codec refuses that shape at both of its own doors, so the seat door is
the second one it would meet. `EntryRescued` is an entry that came back. `RemapAbandoned` is a rule that named an entry and could not be applied to it. `EntryUnwrappable`
is a record the validator quarantined that the page cannot hold the wrapper for. Each one carries the section
name an operator greps for, the page index taken from that NAME rather than from the header, the absolute
slot (or `ContainerLoadFinding.NoSlot` on a whole-page finding), a reason token and the version the record
stands at.

`ContainerLoadReason` is the load path's own two tokens, beside the page tokens of `ItemContainerPageReason`
and the quarantine tokens of `InstanceQuarantineReason`. `page-section-mismatch` is a page whose header names
a different page than the section it arrived in, which only the section NAME can answer. `remap-abandoned` is
a rule that named an entry and could not be applied, counted under its own token IN ADDITION to whatever the
validator then says about that entry, because "a rule could not be applied" and "an id does not resolve" are
different questions. Neither token has a durable ordinal and neither ever will: a `KECQ` reason byte comes
from the wrap table, and nothing on this path wraps anything under either of these.

## The unwrap step, and why it is a step

Spec 12.3 promises it outright: a quarantined item is visibly broken and recoverable in full, because the
bytes are kept verbatim and the first load after the missing rule publishes re-validates and restores the item
exactly. Nothing in the five steps of the load did that, and the page stamp moves whenever any OTHER entry on
the page changes, so the rule that would rescue an entry can stop applying to its page before it ever reaches
it ([#929](https://github.com/APKiwiOrg/KhaozEngine/issues/929)).

The wrapper's own stamped version is what closes it: it is the version the record failed under, it never moves
with the page, and the pass is run at it. A record that validates again is seated live and the page is DIRTY,
so the next ordinary commit writes the rescued bytes. A record that still fails keeps its wrapper exactly as
it stands, stamped version included, so the next rule published still reaches it from where it failed.

**Only the two DRIFT reasons are offered to the rules.** A record wrapped under a structural reason cannot be
helped by a remap rule, re-decoding one on every load costs something, and its bytes are not canonical by
definition, so the page's own door would refuse to seat them live anyway.

## Quarantine does not dirty a page

Exactly two things dirty a page: an operation that changed a slot, and a remap that changed an id. Wrapping an
entry at load is neither, so the stored bytes stay as they are and the same wrapper is derived again on the
next load. That is also what keeps the recovery exact: nothing was rewritten, so there is nothing to undo.

**One record shape cannot carry its wrapper at all**: a wrapper IS a payload, and a container refuses a
payload on a slot whose instance id is 0, which is every plain stack. Such a record is reported as
`EntryUnwrappable` and counted like any other quarantined record, and its bytes are untouched
([#935](https://github.com/APKiwiOrg/KhaozEngine/issues/935)).

## How a load's dirty page reaches a commit

`Load` builds its OWN `ItemContainerPage` objects and hands them back, and a `PagedItemContainer` builds its
own. They are not the same pages, and `Seat` never dirties one, so a page a load-time remap changed does not
become a projection write by itself. That is the spec's shape rather than a gap: the load is pure, and the
rewrite is lazy and rides the next ordinary commit.

Two routes take it there and a host picks one.

1. **Run the pass over the container's own pages.** The host holds the `PagedItemContainer`, seats the loaded
   entries into it, and runs `InstanceRemapPass.Apply` over `container.Pages[i]` itself. The pass dirties
   THAT page, and the next `ContainerCommitBuilder.Close` writes it beside whatever the batch's operations
   changed. `A_hole_survives_a_load_a_save_and_a_remap_and_costs_zero_bytes` in
   `KhaozEngine.ItemInstances.Tests` is this route end to end.
2. **Re-seat what the load already rewrote.** The host walks `ContainerLoadResult.Dirty`, and writes each of
   those pages' slots into its container through `SetSlotAt`, which is an OPERATION and dirties the
   container's own page. Use this when the load's pass has already done the work and re-running it would be
   the second copy.

**The load and the container must share ONE stackable predicate.** `ContainerLoadContext`'s and the
`PagedItemContainer`'s are separate arguments, so two different rules can be handed in, and the pages would
then disagree about which entries may merge, which is a difference that only shows up on the next merge.

## Committing a batch of page operations

```csharp
var batch = ContainerCommitBuilder.Open(streamKey, actionKind, scope, containers, tick);
batch.Apply(ContainerOperation.Craft(...));   // repeated, against an in-memory working copy
JournalCommit commit = batch.Close(identityFactory);
// once the commit has LANDED:
batch.MarkCommitted();
```

`Close` emits ONE `JournalOperationIdentity`, ONE `JournalEvent` per logical operation in order, ONE
`JournalProjectionWrite` per page the containers report dirty, and ONE result. **The audit trail is not
collapsed, only the projection is**: twenty crafts in one held action are twenty `item-crafted` events and one
page write, which is spec 6.7's 1,380 KB and twenty commits becoming 9.4 KB and one.

**It changes nothing in the journal.** One identity per commit is what `JournalCommit` already takes, and a
commit already carries several sections of one stream, so the store, both provider schemas and the store
conformance suite are untouched. That is option A of spec 6.2. Option B, merging identities inside the
executor, is what would have needed all three, and spec 6.3 prices it so the owner can choose it knowingly.

**The dirty set IS the page list, so a lazy remap rewrite rides the batch for free.** `Close` writes every page
the containers report dirty, which is the pages the operations changed plus the pages a load-time remap
changed. The remap never causes a commit of its own, it only joins one, and a batch holding no operation is
refused for exactly that reason.

**`Close` does not clear the dirty flags and `MarkCommitted` does.** A batch whose commit fails terminally
leaves its pages owing the next commit a rewrite, which is the state the consumer's resync agrees with.

**A `Close` that THROWS leaves the batch where it was.** The batch is flagged closed and the window is closed
last, after the commit is built and validated, so a throw on the way there leaves the batch still open with
its pages still dirty, closable again once the caller has fixed what threw, rather than holding something that
had committed nothing and could do nothing.

`ContainerCommitOptions` carries the journal facts that are not operations, each with a right answer a caller
usually takes: `ExpectedVersion` (the ADMITTED head rather than the committed one whenever something is
already queued), the `item-container` projection schema at the page codec's own version, the
`item-container.result` result schema, the `JournalLimits` the batch is bounded by, and
`QueueBehindAdmitted`, which is on by default so a click lands behind a held craft rather than being refused.

### The window and its five closers

`ContainerBatchWindow` is ONE SERVER TICK, and it closes on the FIRST of the five, first-wins. It DECIDES and
it does not count: every total it checks arrives as an argument from the builder that owns it, so there is no
second copy of a number that could drift from the commit being built. `ContainerBatchCloseReason` is the
answer, `Open` while it is still taking operations:

| Closer | What it is |
|---|---|
| `TickBoundary` | the tick moved. A tick rather than a timer, so nothing durable lives in a window whose length is a configuration value |
| `SecondStream` | an operation naming a container this batch was not opened over. A different atomic unit, never widened by an unrelated batch |
| `PresentAtCommit` | value moving between accounts, which never shares an identity with anything else |
| `ClientOperation` | a client originated operation. It heads its own batch, because `ResolveOperationAsync` is keyed on ONE id |
| `LimitReached` | 128 events, 64 projection writes, the 64 KiB intent or the 8 MiB commit, all read from `JournalLimits` |

A refused `Apply` changes NOTHING: the working copy is untouched, no event is written, and the caller opens the
next batch for that operation. An operation the working copy cannot PERFORM is a different thing and throws,
because a game refuses an illegal action before the journal ever sees it.

### Whose identity, and what the intent holds

A SERVER minted batch's intent is the canonical ORDERED operation list,
`[Count: varint][ per operation: [Kind: varint][Parameters] ]`. A CLIENT headed batch's intent is the client
operation's own canonical encoding ALONE, under the client's own id, and the server work riding behind it
contributes no intent bytes at all.

**That second half is load bearing.** The client resubmits after a reconnect with the intent it built from its
own click, and it never saw the quest advance or the sweep the click caused. If the batch's intent were the
whole list, the resubmit would hash differently, `ResolveOperationAsync` would answer `OperationConflict`, and
the consumer would treat a COMMITTED withdraw as a failed one: the admitted view rolls back, everything queued
behind it is superseded transitively, the player is told the action failed, and a re-click applies it twice.

**One client operation may HEAD a batch of the server work it directly causes. Two client operations never
share one.**

### The operation vocabulary

`ContainerOperationKind` is the six kinds and a `None` that is refused, each with a durable varint that is
never renumbered and never reordered, because the number is written into a normalized intent and into an
event payload. `ContainerOperationOrigin` is who caused one: `Server`, which has no id of its own to lose and
may ride any batch, or `Client`, which carries the operation id it will resubmit after a reconnect and
therefore heads its own batch.

Every kind names the slots it touches, so the pages a batch will write are known before it is applied. That is
what lets the window close on the projection write cap with no mutation to undo, and it is what makes a replay
land where the original did rather than wherever a free-slot search would put it today.

| Kind | What it does | Canonical parameters, in order | Event |
|---|---|---|---|
| `Move` | relocates a whole entry, or units of a plain stack, into an EMPTY slot, possibly in another container of the same stream | container, slot, destination container, destination slot, count, instance id | `item-moved` |
| `Split` | moves units of a plain stack into an empty slot of the same container | container, slot, destination slot, count, instance id | `stack-split` |
| `Merge` | folds one occupied slot into another under spec 4.6's byte equality | container, slot, destination slot, instance id, destination instance id | `stack-merged` |
| `Grant` | seats an arriving entry at a named slot, merging into it or opening it under the capacity gate | container, slot, definition id, count, instance id | `item-granted` |
| `Take` | removes units from a slot | container, slot, count, instance id | `item-taken` |
| `Craft` | rewrites an owned item's payload in place and consumes the currency that paid for it | container, slot, currency container, currency slot, currency definition, count, instance id | `item-crafted` |

Every varint is unsigned and minimal, a container name is `[Length: varint][UTF8]`, and an instance id goes
through `InstanceIdAllocator.WriteId` so the high node's sign bit cannot make two encodings of one id.
**Nothing that is an OUTCOME is in the encoding**: not the payload a grant seats, not the payload a craft
leaves, and not the origin or the present-at-commit flag, which are routing rather than intent.

**The declared instance id is in the intent AND held against the working copy** (spec 15.1). Without it a
replayed operation whose slot has been refilled by a different item would hash identically and apply to the
wrong one.

**A craft carries its event body and every other kind writes its own canonical encoding as one.** Spec 10.6
owns the `item-crafted` body and the crafting framework encodes it, so this package carries those bytes rather
than freezing a format under a durable event name before its first writer exists.

**A craft that consumes NO currency carries no currency fields**, and `Validate` refuses one that does. The
intent writes all four whether or not the craft reads them, and a craft with a currency definition id of 0
reads none of them, so a currency slot left set by a caller's own defaults gave one action two encodings and
one resubmit resolved as a conflict rather than as a replay.

**The vocabulary names a container by NAME**, which is what its section names are filed under, and the craft
intent of spec 10.6 names container IDs. The two have to be reconciled before a craft message crosses a wire
([#942](https://github.com/APKiwiOrg/KhaozEngine/issues/942)).

## Event names

`ItemInstanceEvents` carries the durable strings an item operation writes into the journal: the `item-craft`
action kind (`CraftActionKind`), the `item-generated` and `item-crafted` event types, and the five a
container operation writes (`item-moved`, `stack-split`, `stack-merged`, `item-granted`, `item-taken`).
`All` is the seven event types in spec order. A durable string is never renamed and never switched on, which
is why they are constants rather than an enum, and `EventTypeOf` switches on the operation KIND rather than
on a stored string: the number is this build's and the string is the durable one.
The payload codecs for `item-generated` and `item-crafted` arrive with the generator and the crafting framework
that emit them.

**These events are written and nothing here reads one back.** The commit builder encodes an event body per
operation and the package ships no decoder for one, so a correction replay or an audit tool has bytes it
cannot take apart yet ([#941](https://github.com/APKiwiOrg/KhaozEngine/issues/941)).

## Design

`docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md` sections 2.1, 5.2, 5.5, 5.6 and 6.1 to 6.6, over the shared
contracts in `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md` sections 10.1 to 10.4.
