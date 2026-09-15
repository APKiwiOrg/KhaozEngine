using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The authoring value vocabulary of spec 2.3 and 3.7: the four edit operations and their DURABLE
/// numbering, the change set that holds one pending intent per target, the fork that is refused or applied
/// whole, and the family blocks of contracts 5.2.
/// <para>
/// Nothing here touches a database. These are the values a provider stores and the seam every backend
/// implements, so they are tested with no store, no file and no registry beyond the one they construct.
/// </para>
/// </summary>
public class AuthoringValueTests
{
    static readonly ContentTypeId Item = new(2);
    static readonly ContentTypeId Mod = new(1024);

    static ContentFieldEdit Int(string name, long value)
        => new(name, ContentFieldValue.OfNumber(ContentFieldKind.Int, value));

    [Fact]
    public void EditOperationNumbersArePinnedToTheDdlCheck()
    {
        // catalog_draft_edit.operation carries CHECK (operation IN (1, 2, 3, 4)) in spec 4.4's DDL, so the
        // numbering is durable and is written out rather than left to declaration order.
        Assert.Equal(1, (int)ContentEditOperation.Add);
        Assert.Equal(2, (int)ContentEditOperation.Update);
        Assert.Equal(3, (int)ContentEditOperation.Retire);
        Assert.Equal(4, (int)ContentEditOperation.Fork);
        Assert.Equal(4, Enum.GetValues<ContentEditOperation>().Length);
    }

    [Fact]
    public void SchemaModeIsTheJournalsTwoModes()
    {
        Assert.Equal(
            ["AutoCreate", "ValidateOnly"],
            Enum.GetNames<ContentAuthoringSchemaMode>());
    }

    [Fact]
    public void AnEditStoresTheChangedFieldsOnlyRatherThanTheWholeRow()
    {
        // Spec 3.7: "An edit is stored as the CHANGED FIELDS ONLY, never the whole row." That is what lets
        // the audit record a field-level before and after with no extra table, and what makes two operators
        // editing different fields of one row a merge rather than a last-write-wins clobber.
        ContentEdit edit = ContentEdit.Update(Item, 13, new ContentKey("stone_sword"), [Int("value", 45)]);

        Assert.Equal(ContentEditOperation.Update, edit.Operation);
        ContentFieldEdit only = Assert.Single(edit.Fields);
        Assert.Equal("value", only.Name);
        Assert.Equal(45, only.Value.Number);
    }

    [Fact]
    public void AnOrdinaryAddCarriesNoIdBecauseIdsAreAllocatedAtPublish()
    {
        ContentEdit edit = ContentEdit.Add(Item, new ContentKey("iron_sword"), [Int("value", 120)]);

        Assert.Equal(0, edit.DefinitionId);
        Assert.Equal(ContentEditOperation.Add, edit.Operation);
        Assert.Null(edit.FamilyId);
    }

    [Fact]
    public void AChangeSetKeepsTheEditsInTheOrderTheyWereApplied()
    {
        var set = new ContentChangeSet();
        set.Apply(ContentEdit.Update(Item, 13, new ContentKey("stone_sword"), [Int("value", 45)]));
        set.Apply(ContentEdit.Update(Item, 14, new ContentKey("oak_shield"), [Int("value", 7)]));
        set.Apply(ContentEdit.Add(Item, new ContentKey("iron_sword"), [Int("value", 120)]));

        Assert.Equal([13, 14, 0], set.Edits.Select(static edit => edit.DefinitionId).ToArray());
    }

    [Fact]
    public void ASecondEditOfTheSameTargetAndOperationReplacesTheFirstRatherThanQueueingTwo()
    {
        // Spec 4.4: the unique index on (type_id, definition_id, content_key) is what makes an edit
        // IDEMPOTENT per target, so a console that saves the same row twice updates the one edit.
        var set = new ContentChangeSet();
        set.Apply(ContentEdit.Update(Item, 13, new ContentKey("stone_sword"), [Int("value", 45)]));
        set.Apply(ContentEdit.Update(Item, 13, new ContentKey("stone_sword"), [Int("value", 46)]));

        ContentEdit only = Assert.Single(set.Edits);
        Assert.Equal(46, Assert.Single(only.Fields).Value.Number);
    }

    [Fact]
    public void ASecondEditOfAnOccupiedTargetCollidesWhenItsOperationDiffers()
    {
        // Spec 4.4: "The collision is per TARGET and not per operation." An Update on item 13 followed by a
        // Retire of item 13 both name (2, 13, "stone_sword"), and the second is refused rather than silently
        // flipping the first edit's operation and dropping its fields.
        var set = new ContentChangeSet();
        set.Apply(ContentEdit.Update(Item, 13, new ContentKey("stone_sword"), [Int("value", 45)]));

        ContentEdit retire = ContentEdit.Retire(
            Item, 13, new ContentKey("stone_sword"), ContentRetirePolicy.Placeholder, 0);
        Assert.False(set.TryApply(retire, out ContentEdit? standing));
        Assert.NotNull(standing);
        Assert.Equal(ContentEditOperation.Update, standing.Operation);

        ContentAuthoringException refusal = Assert.Throws<ContentAuthoringException>(() => set.Apply(retire));
        Assert.Equal(ContentAuthoringException.EditTargetCollisionReason, refusal.Reason);
        Assert.Equal(Item, refusal.Type);
        Assert.Equal(13, refusal.Id);

        // The standing edit is untouched: the refusal changed nothing.
        ContentEdit only = Assert.Single(set.Edits);
        Assert.Equal(ContentEditOperation.Update, only.Operation);
        Assert.Equal(45, Assert.Single(only.Fields).Value.Number);
    }

    [Fact]
    public void TheTargetIsTheTypeTheIdAndTheKeyTogether()
    {
        // Two Add edits for the same key collide on the key half of the index even though both carry id 0,
        // and two rows of different types never collide at all.
        var set = new ContentChangeSet();
        set.Apply(ContentEdit.Add(Item, new ContentKey("iron_sword"), [Int("value", 120)]));

        Assert.False(set.TryApply(
            ContentEdit.Retire(Item, 0, new ContentKey("iron_sword"), ContentRetirePolicy.Placeholder, 0),
            out _));
        Assert.True(set.TryApply(ContentEdit.Add(Mod, new ContentKey("iron_sword"), [Int("tier_1_max", 60)]), out _));
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void ForkCarriesFiveThingsAndNoIdForTheCopy()
    {
        // Spec 3.7: the source id, the copy's new key, the Bool flag field to set on the copy, the changed
        // fields for the ORIGINAL, and no id for the copy, because ids are allocated at publish.
        ContentEdit fork = ContentEdit.Fork(
            Mod,
            412,
            new ContentKey("added_fire_damage"),
            new ContentKey("added_fire_damage_legacy"),
            "legacy",
            [Int("tier_1_max", 60)]);

        Assert.Equal(ContentEditOperation.Fork, fork.Operation);
        Assert.Equal(412, fork.DefinitionId);
        Assert.Equal(new ContentKey("added_fire_damage_legacy"), fork.ForkKey);
        Assert.Equal("legacy", fork.ForkFlagField);
        Assert.Equal(60, Assert.Single(fork.Fields).Value.Number);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ForkRefusesAnEmptyForkKey(string? forkKey)
    {
        // A key is immutable once published (contracts 5.3) and the engine will not invent one, so a fork
        // with no key for the copy is refused where it is built rather than at publish.
        ContentKey copy = forkKey is null ? default : new ContentKey(forkKey);
        Assert.Throws<ArgumentException>(() => ContentEdit.Fork(
            Mod, 412, new ContentKey("added_fire_damage"), copy, "legacy", [Int("tier_1_max", 60)]));
    }

    [Fact]
    public void ForkRefusesAnEmptyFlagFieldName()
    {
        Assert.Throws<ArgumentException>(() => ContentEdit.Fork(
            Mod,
            412,
            new ContentKey("added_fire_damage"),
            new ContentKey("added_fire_damage_legacy"),
            "   ",
            [Int("tier_1_max", 60)]));
    }

    [Fact]
    public void ForkIsOneEditSoADraftCannotHoldThreeSeparableOnes()
    {
        // Spec 3.7: allocating the id, copying the fields and appending the rule are one atomic statement,
        // and an author who did them as three edits could have the publish succeed with the rule missing,
        // which is the state nothing can detect afterwards. One edit against one target is what enforces it.
        var set = new ContentChangeSet();
        set.Apply(ContentEdit.Fork(
            Mod,
            412,
            new ContentKey("added_fire_damage"),
            new ContentKey("added_fire_damage_legacy"),
            "legacy",
            [Int("tier_1_max", 60)]));

        Assert.False(set.TryApply(
            ContentEdit.Update(Mod, 412, new ContentKey("added_fire_damage"), [Int("tier_1_max", 70)]),
            out _));
        Assert.Single(set.Edits);
    }

    [Fact]
    public void RetirePolicyNumbersMatchTheRemapRulePayloadByte()
    {
        Assert.Equal(0, (int)ContentRetirePolicy.None);
        Assert.Equal(RemapRule.RetirePolicyPlaceholder, (byte)ContentRetirePolicy.Placeholder);
        Assert.Equal(RemapRule.RetirePolicyReplacement, (byte)ContentRetirePolicy.Replacement);
    }

    [Fact]
    public void ADraftCarriesItsBaseVersionAndItsOpenedStamps()
    {
        var opened = new DateTimeOffset(2026, 9, 15, 4, 30, 0, TimeSpan.Zero);
        var set = new ContentChangeSet();
        set.Apply(ContentEdit.Update(Item, 13, new ContentKey("stone_sword"), [Int("value", 45)]));

        var draft = new ContentDraft(47, "oid:8f2c", opened, "autumn price pass", set);

        Assert.Equal(47, draft.BaseVersion);
        Assert.Equal("oid:8f2c", draft.OpenedBy);
        Assert.Equal(opened, draft.OpenedAtUtc);
        Assert.Equal("autumn price pass", draft.Note);
        Assert.Equal(1, draft.EditCount);
    }

    [Fact]
    public void AFamilyBlockIsAlignedSoMembershipIsTwoComparisons()
    {
        // Contracts 5.2: a block holding [base, base + size) has base % size == 0, which makes membership
        // (id & ~(size - 1)) == base.
        var block = new ContentFamilyBlock(1, 0, 1024, 16, 1024, 1);

        Assert.Equal(1040, block.TopExclusive);
        Assert.False(block.IsFull);
        Assert.True(block.Contains(1024));
        Assert.True(block.Contains(1039));
        Assert.False(block.Contains(1040));
        Assert.False(block.Contains(1023));
    }

    [Fact]
    public void AFamilyWalksItsOrderedBlockList()
    {
        // Contracts 5.2: when a block fills a SECOND block is reserved for the same family, so a membership
        // test is a short loop rather than a set lookup.
        var family = new ContentFamily(
            1,
            Item,
            "swords",
            16,
            isRetired: false,
            createdInVersion: 3,
            [
                new ContentFamilyBlock(1, 0, 1024, 16, 1040, 3),
                new ContentFamilyBlock(1, 1, 4096, 16, 4100, 9),
            ]);

        Assert.True(family.Contains(1030));
        Assert.True(family.Contains(4099));
        Assert.False(family.Contains(2048));
        Assert.True(family.Blocks[0].IsFull);
        Assert.False(family.Blocks[1].IsFull);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(65536, true)]
    [InlineData(1024, true)]
    [InlineData(8, false)]
    [InlineData(131072, false)]
    [InlineData(48, false)]
    public void AFamilyBlockSizeIsAPowerOfTwoBetweenSixteenAndSixtyFiveThousand(int blockSize, bool legal)
    {
        Assert.Equal(legal, ContentFamily.IsLegalBlockSize(blockSize));
        if (legal)
        {
            return;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentFamily(
            1, Item, "swords", blockSize, isRetired: false, createdInVersion: 1, []));
    }

    [Fact]
    public void AVersionRecordCarriesBothManifestHashesAsTwoIdentities()
    {
        string server = new('9', 64);
        string client = new('1', 64);
        var record = new ContentVersionRecord(
            48, server, client, 4120, 4118, 1, 47, "oid:8f2c", "autumn price pass", DateTimeOffset.UnixEpoch);

        Assert.Equal(new ContentVersionIdentity(48, server), record.ServerIdentity);
        Assert.Equal(new ContentVersionIdentity(48, client), record.ClientIdentity);
    }

    [Fact]
    public void AnAuditEntryIsOneFieldChangeAndNamesBothActorAndOperator()
    {
        // Spec 4.6: the unit of an audit row is ONE FIELD, and spec 10.10 keeps both columns because actor
        // is what the engine authenticated and operator is what the console asserted.
        var entry = new ContentAuditEntry(
            1,
            DateTimeOffset.UnixEpoch,
            "admin-endpoint",
            "oid:8f2c",
            ContentAuditActions.DraftEdit,
            Item,
            13,
            new ContentKey("stone_sword"),
            "value",
            "42",
            "45",
            0,
            "autumn price pass");

        Assert.Equal("admin-endpoint", entry.Actor);
        Assert.Equal("oid:8f2c", entry.Operator);
        Assert.Equal("draft-edit", entry.Action);
        Assert.Equal("value", entry.FieldName);
        Assert.Equal("42", entry.BeforeValue);
        Assert.Equal("45", entry.AfterValue);
    }

    [Fact]
    public void TheStoreSeamCarriesTheTwentyTwoMembersEveryProviderImplements()
    {
        // Spec 2.3 and the phase 1 plan name these so a provider implements ONE shape. A member added here
        // without being added to every backend is the drift this pins.
        string[] expected =
        [
            "AllocateAsync",
            "AllocateInFamilyAsync",
            "ApplyEditsAsync",
            "CreateFamilyAsync",
            "DiscardDraftAsync",
            "ExportBundleAsync",
            "GetActiveVersionAsync",
            "GetOpenDraftAsync",
            "GetPinnedVersionAsync",
            "GetRowHistoryAsync",
            "GetSchemaVersionAsync",
            "GetStoreEpochAsync",
            "GetVersionAsync",
            "ImportBundleAsync",
            "InitializeAsync",
            "ListAuditAsync",
            "ListRowsAsync",
            "ListVersionsAsync",
            "LoadSnapshotAsync",
            "PublishAsync",
            "RollbackToAsync",
            "SetPinnedVersionAsync",
        ];

        string[] actual = typeof(IContentAuthoringStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static member => member.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ABundleCarriesTheFivePartsOfALosslessExport()
    {
        // Spec 10.9: a format version, the registered type list with their schemas, every live row with its
        // id, key and fields, every family with its blocks, and the full remap rule list.
        var schema = new ContentFieldSchema([
            new ContentFieldEntry("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
        ]);
        var bundle = new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "0123456789abcdef0123456789abcdef",
            48,
            [new ContentBundleType(Item, "item", ContentVisibility.Client, 1024, null, schema)],
            [new ContentBundleRow(Item, 13, new ContentKey("stone_sword"), false, null, [Int("value", 45)])],
            [],
            []);

        Assert.Equal(1, bundle.FormatVersion);
        Assert.Equal(48, bundle.SourceVersion);
        Assert.Equal("item", Assert.Single(bundle.Types).TypeKey);

        // A bundle row's id is OPTIONAL: named, it is imported with it, unnamed, it is allocated in edit
        // ordinal order.
        Assert.Equal(13, Assert.Single(bundle.Rows).Id);
        Assert.Null(new ContentBundleRow(Item, null, new ContentKey("iron_sword"), false, null, []).Id);
    }

    [Fact]
    public void APublishRequestRequiresItsExpectedBaseVersionAndLeavesTheBuildsOptional()
    {
        // Spec 10.6: expectedBaseVersion is optimistic concurrency and it is REQUIRED. The minimum builds
        // are consumer supplied, and omitted they carry FORWARD rather than resetting to 0.
        var request = new ContentPublishRequest("admin-endpoint", "oid:8f2c", "autumn price pass", 47);

        Assert.Equal(47, request.ExpectedBaseVersion);
        Assert.Null(request.MinimumServerBuild);
        Assert.Null(request.MinimumClientBuild);
    }
}
