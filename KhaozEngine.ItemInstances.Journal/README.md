# KhaozEngine.ItemInstances.Journal

The journal side of the per-item instance record: how a paged container is NAMED in a stream's projection
sections, and how it is LOADED back out of them.

In the `Server` umbrella, over `KhaozEngine.ItemInstances` and `KhaozEngine.WorldStore`.

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

**Nothing needs escaping.** A journal section name is an identity capped at 128 characters over
`[A-Za-z0-9._:/-]`, and the slash is in that set. `Format` refuses a container name outside the rest of it, so
a name this type writes is always a name the journal accepts, and a container name carries no slash of its own
so one level keeps the parse unambiguous.

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
`EntryQuarantined` is an entry that carries a `KECQ` wrapper and still does. `EntryRescued` is an entry that
came back. `RemapAbandoned` is a rule that named an entry and could not be applied to it. `EntryUnwrappable`
is a record the validator quarantined that the page cannot hold the wrapper for.

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

## Event names

`ItemInstanceEvents` carries the durable strings an item operation writes into the journal: the `item-craft`
action kind, and the `item-generated` and `item-crafted` event types. A durable string is never renamed and
never switched on, which is why they are constants rather than an enum. The payload codecs for those two
events arrive with the generator and the crafting framework that emit them.

## Design

`docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md` sections 2.1, 5.2 and 5.5, over the shared contracts in
`docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md` sections 10.1 to 10.4.
