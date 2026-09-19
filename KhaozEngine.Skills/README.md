# KhaozEngine.Skills

Game-agnostic skill progression kernel. GPU-free, zero third-party dependencies, part of the
`KhaozEngine.Foundation` umbrella.

The engine owns the experience arithmetic and never learns what a skill IS: there is no enum, no name, no
icon, no display order, no training action and no balance number in this package. A game supplies dense
`int` indices and one small roster object. The design twin of `KhaozEngine.Items` and `KhaozEngine.Stats`:
kernel in the engine, meaning in the game.

**There is no time base of any kind.** Nothing here counts steps, deltas, timers or cooldowns. Every call is
a function of the numbers handed to it, so the same kernel serves a slow turn-based world and a continuous
one at any frame rate, and two games on different clocks agree on what an experience number means.

## `SkillXpCurve`

One experience table, with an IDENTITY. Experience is stored as a number rather than as a level, so the
same number reads as a different level the moment the curve moves, and the curve is content a game tunes.
That is why this is an object rather than two free functions: a saved record stores `Hash`, and a load that
finds a different one rescales.

- `SkillXpCurve.Configured(firstLevelCost, doublingLevels, maxLevel)` is the parametric family. The cost of
  a level doubles every `doublingLevels` levels from a first level costing `firstLevelCost`, each cost
  rounded whole, and a threshold is the running sum under it.
- `SkillXpCurve.Osrs` is the classic running-sum table to level 99. It has no parameters, so `Hash` is the
  word `osrs` rather than a digest, and that word is the DURABLE MEANING OF AN ABSENT CURVE SECTION: a
  record written before a game had a configurable curve was written under this one.
- `XpForLevel(level)` and `LevelFor(xp)` are the two questions, clamped to the table at both ends.

## `ISkillRoster` and `SkillRoster`

The one seam to a game's own skill identity, and the whole of it:

```csharp
public interface ISkillRoster
{
    int Count { get; }
    bool IsLocked(int index);
    int ParentOf(int index);   // -1 for a root
}
```

`SkillRoster` is the batteries-included implementation, so a game only writes its own when the roster is
computed from content rather than declared. Every skill starts OPEN and a root:

```csharp
ISkillRoster roster = SkillRoster.Of(9)
    .LockAll()                       // for a roster whose live set is the exception
    .Unlock(Vitality).Unlock(Melee).Unlock(Chopping)
    .Parent(Melee, Combat)
    .Parent(Chopping, Gathering)
    .Build();
```

`Build` refuses a parent chain that loops, because an award walks one step up and would never notice one.
A LOCKED skill refuses every award and every removal, and still holds and reports whatever experience it
was decoded with: locking stops a number moving, it does not erase it. The share goes exactly ONE level up,
so a roster nested deeper is legal and a grandparent is never paid.

## `SkillBook`

One character's experience, sized by the roster and priced by the curve, both handed in at construction.

```csharp
public sealed class SkillBook
{
    public SkillBook(ISkillRoster roster, SkillXpCurve curve);
    public static SkillBook Fresh(ISkillRoster roster, SkillXpCurve curve, params ReadOnlySpan<SkillSeed> seeds);

    public ISkillRoster Roster { get; }
    public SkillXpCurve Curve { get; }
    public int Count { get; }

    public double Xp(int skill);
    public int Level(int skill);
    public bool AddXp(int skill, double amount);      // true when the award CROSSED a level
    public bool RemoveXp(int skill, double amount);   // true when the removal DROPPED one
    public void SetXp(int skill, double xp);          // the decoder and rescale door
}
```

- **`Fresh` seeds LEVELS, not numbers.** `new SkillSeed(Vitality, 10)` is priced through the curve at the
  moment the book is made, so a curve change reprices every fresh character for free. A seed is written
  past the lock rule, because a starting level on a skill no system has opened yet is a design statement.
- **Experience saturates at `SkillXpLimits.MaxXp` (200,000,000)** and floors at zero. A non-finite or
  non-positive award is dropped and reports false, which is the same answer a locked skill gives, so no
  caller learns a second failure shape.
- **An index outside the roster throws.** It is a caller bug rather than bad data: the decoder skips an id
  it does not have long before a door here sees it.

## `SkillAwards`

An award to a child pays the child in full and its parent a share of the same amount.

```csharp
SkillAwardResult award = SkillAwards.Apply(book, Chopping, amount: 100d, shareBp: 5000);
// award.Crossed, award.Parent, award.ChildXp, award.ParentCrossed, award.ParentXp
int present = award.Presented(Chopping);   // the skill a level-up toast names, or -1
```

`shareBp` is basis points of `SkillAwards.ShareDenominator` (10,000), integer because a published tuning
number has to mean the same thing on every machine. The result carries both TOTALS so a caller can record
the award without reading the book back, which matters for a durable log that stores whole totals rather
than deltas: reading them off the book afterwards is the same numbers one step later, where a second award
can have landed in between. **The kernel writes nothing anywhere**, and the game turns the result into its
own events, presentation and log lines.

A refused award (locked child) reports `Parent = -1`, `Crossed = false` and `ChildXp` as the book stands,
because the result never lies about the book. A caller mirroring awards into a log asks its own roster
whether the skill is locked rather than looking for a second failure shape here.

## `SkillXpRescale`

Carries a saved character from the curve they earned under to the one in force, keeping the LEVEL and the
fraction past it. A curve change never moves anybody's level. It is deliberately not "keep the number",
which reads as a demotion, and not "keep the level and drop the progress", which steals up to a level of
grind.

```csharp
if (!string.Equals(saved.Hash, current.Hash, StringComparison.Ordinal))
    SkillXpRescale.Rebase(book, saved, current);
```

`Preserve(from, to, xp)` is the single-number half. A maxed skill arrives maxed, a cap that came DOWN pins
the character to the new top rather than dropping them, an untrained skill is left exactly alone, and the
result is held one bit below the next threshold so a fraction of a whisker under one cannot hand out a free
level.

## `SkillProgress`

The readout arithmetic the curve cannot answer on its own: `FractionToNext`, `XpForNext`,
`RemainingToNext`, `RemainingToNextWhole` and `IsMaxed`. Pure, total and clamped, so a stored NaN reads as
an empty bar rather than one of undefined width. `RemainingToNextWhole` is the one a panel prints: the
remainder against the FLOORED experience shown above it, so the two lines add up to the next threshold
instead of reading as nothing owed under a bar short of full.

## `SkillBookCodec`

The durable and wire form: `byte version`, `byte count`, then `count` fixed-width entries of one id byte
and an eight-byte little-endian double. Little-endian by construction on every host.

- **A COUNT change needs no version bump.** An entry is fixed width and keyed by id, an older blob's
  missing skills decode as zero, and a newer blob's higher ids are skipped forward-compatibly. Adding a
  skill to a game is not a migration.
- **`Version` is 2, and a version is never assigned into 0xF0 to 0xFF** (`ReservedVersionFloor`). A
  composite record format that wraps a skill blob tells the two apart by this very byte, so a collision
  would read every bare skill blob as a malformed composite and quarantine it.
- **An unknown version is refused by number rather than guessed at.** `Validate` names it and `TryDecode`
  returns false. There is no legacy reader hook: a pre-version-2 format belongs to one game's own older
  roster, where ids changed meaning, and only that game can say what its id 2 used to be. So a game with
  older bytes dispatches on `SkillBookCodec.VersionOf(blob)`, migrates its own bytes into a version 2 blob,
  and hands the result here.
- **`Validate` is roster-free**, so a store can vet bytes without knowing which game wrote them. It returns
  a quarantine reason or null, and refuses a truncated blob, a duplicate id, a non-finite, negative or
  over-ceiling experience, and (with the optional `requiredIndex`) a blob that lost the one skill a game's
  character sheet is meaningless without. Null or empty input is "no state", not a fault.

## `SkillXpCurveCodec`

The twelve-byte curve section, three little-endian int32 in the order first level cost, doubling levels,
max level. It ships here so two games do not reimplement a format that is already decided.

**Decode REBUILDS through `SkillXpCurve.Configured` rather than trusting the bytes**, so a section holding
zeroes or a negative reads as unreadable instead of building a curve whose thresholds are all zero and
whose every level is the cap. A `maxLevel` over `MaxLevelCeiling` is corruption and refused too, since a
curve builds its thresholds eagerly. Unreadable means the caller leaves the stored experience exactly as it
is: guessing a curve would move every level in the book.

`Encode` refuses a non-parametric curve, because an ABSENT section is how `SkillXpCurve.Osrs` is stored and
writing one would make the two indistinguishable.

## Usage

```csharp
using KhaozEngine.Skills;

ISkillRoster roster = SkillRoster.Of(SkillCount).Parent(Chopping, Gathering).Build();
SkillXpCurve curve = SkillXpCurve.Configured(firstLevelCost: 114, doublingLevels: 6, maxLevel: 100);

SkillBook book = SkillBook.Fresh(roster, curve, new SkillSeed(Vitality, 10));
SkillAwardResult award = SkillAwards.Apply(book, Chopping, 100d, shareBp: 5000);

byte[] blob = SkillBookCodec.Encode(book);
string? reason = SkillBookCodec.Validate(blob, requiredIndex: Vitality);   // null is well formed
SkillBookCodec.TryDecode(blob, roster, curve, out SkillBook back, requiredIndex: Vitality);
```
