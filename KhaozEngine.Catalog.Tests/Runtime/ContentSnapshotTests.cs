using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The read seam of spec 2.2 and the one construction path of Appendix B's choice 3. Two properties matter
/// most here: the interface is SEVEN members and no more, so a later task cannot widen what a client
/// compiles against, and the builder is canonical, so the snapshot a publish builds and the snapshot a test
/// builds hold their rows in the same order.
/// <para>
/// Joins <c>AllocSensitive</c> because one test reads <c>GC.GetAllocatedBytesForCurrentThread</c>.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public class ContentSnapshotTests
{
    static readonly string[] SevenMembers =
        ["Identity", "IsRetired", "Rows", "Rules", "TryGetId", "TryGetRow", "VersionNumber"];

    [Fact]
    public void The_read_seam_declares_exactly_seven_members()
    {
        string[] declared = typeof(IContentSnapshot)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member is not MethodInfo { IsSpecialName: true })
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(SevenMembers, declared);
    }

    [Fact]
    public void ContentSnapshot_implements_the_read_seam()
    {
        ContentSnapshot snapshot = Builder().Build();

        Assert.IsAssignableFrom<IContentSnapshot>(snapshot);
    }

    [Fact]
    public void The_version_number_is_the_identity_pairs_number()
    {
        ContentSnapshot snapshot = Builder()
            .WithIdentity(7, new string('a', 64))
            .Build();

        Assert.Equal(7, snapshot.VersionNumber);
        Assert.Equal(new ContentVersionIdentity(7, new string('a', 64)), snapshot.Identity);
    }

    [Fact]
    public void Rows_come_back_ordered_by_id_whatever_order_they_went_in()
    {
        ContentSnapshot snapshot = Builder()
            .AddRow(Item(9, "oak_bow"))
            .AddRow(Item(2, "iron_sword"))
            .AddRow(Item(5, "pine_staff"))
            .Build();

        int[] ids = snapshot.Rows(CatalogSnapshotFixtures.ItemType).Select(row => row.Id).ToArray();

        Assert.Equal(new[] { 2, 5, 9 }, ids);
    }

    [Fact]
    public void Two_build_orders_produce_the_same_snapshot_row_for_row()
    {
        ContentRow sword = Item(2, "iron_sword");
        ContentRow staff = Item(5, "pine_staff");
        ContentRow bow = Item(9, "oak_bow");
        ContentRow metal = CatalogSnapshotFixtures.TagRow(1, "metal");
        ContentRow wood = CatalogSnapshotFixtures.TagRow(3, "wood");

        ContentSnapshot first = Builder()
            .AddRow(bow).AddRow(metal).AddRow(sword).AddRow(wood).AddRow(staff)
            .Build();
        ContentSnapshot second = Builder()
            .AddRow(wood).AddRow(staff).AddRow(sword).AddRow(bow).AddRow(metal)
            .Build();

        Assert.Equal(first.Types, second.Types);
        foreach (ContentTypeId type in first.Types)
        {
            IReadOnlyList<ContentRow> left = first.Rows(type);
            IReadOnlyList<ContentRow> right = second.Rows(type);
            Assert.Equal(left.Count, right.Count);
            for (int i = 0; i < left.Count; i++)
            {
                Assert.Same(left[i], right[i]);
            }
        }
    }

    [Fact]
    public void TryGetRow_finds_a_row_by_id_and_refuses_an_id_with_no_row()
    {
        ContentSnapshot snapshot = Builder().AddRow(Item(2, "iron_sword")).Build();

        Assert.True(snapshot.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out ContentRow? found));
        Assert.Equal("iron_sword", found.Key.ToString());
        Assert.False(snapshot.TryGetRow(CatalogSnapshotFixtures.ItemType, 3, out _));
        Assert.False(snapshot.TryGetRow(CatalogSnapshotFixtures.TagType, 2, out _));
    }

    [Fact]
    public void Rows_of_a_type_the_snapshot_carries_nothing_for_is_empty()
    {
        ContentSnapshot snapshot = Builder().AddRow(Item(2, "iron_sword")).Build();

        Assert.Empty(snapshot.Rows(CatalogSnapshotFixtures.TagType));
        Assert.Empty(snapshot.Rows(new ContentTypeId(4096)));
    }

    [Fact]
    public void TryGetId_is_ordinal_over_the_utf8_key()
    {
        ContentSnapshot snapshot = Builder()
            .AddRow(Item(2, "iron_sword"))
            .AddRow(Item(5, "iron_sword_2"))
            .Build();

        Assert.True(snapshot.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("iron_sword"), out int id));
        Assert.Equal(2, id);

        // Ordinal, so a case fold is a MISS rather than a hit, which is contracts 5.3's rule.
        Assert.False(snapshot.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("IRON_SWORD"), out _));

        // A prefix is a different key, so the compare is over the whole slice and not its head.
        Assert.False(snapshot.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("iron_swor"), out _));

        // The same key sliced out of a larger blob answers the same id as one built from a string.
        byte[] blob = System.Text.Encoding.UTF8.GetBytes("xxiron_swordxx");
        Assert.True(snapshot.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey(blob, 2, 10), out int sliced));
        Assert.Equal(2, sliced);
    }

    [Fact]
    public void IsRetired_answers_from_the_retired_bits_with_no_row_walk()
    {
        ContentSnapshot snapshot = Builder()
            .AddRow(Item(2, "iron_sword"))
            .AddRow(Item(5, "bronze_sword", isRetired: true))
            .Build();

        Assert.True(snapshot.IsRetired(CatalogSnapshotFixtures.ItemType, 5));
        Assert.False(snapshot.IsRetired(CatalogSnapshotFixtures.ItemType, 2));
        Assert.False(snapshot.IsRetired(CatalogSnapshotFixtures.ItemType, 11));
        Assert.False(snapshot.IsRetired(CatalogSnapshotFixtures.TagType, 5));

        // A row walk would materialise something. The bit read touches an array and a dictionary and nothing
        // else, so a hot loop over it allocates nothing at all.
        for (int i = 0; i < 64; i++)
        {
            snapshot.IsRetired(CatalogSnapshotFixtures.ItemType, 5);
        }

        CatalogAllocAssert.NoPerCallAllocation("IsRetired over a loaded snapshot", () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                Assert.True(snapshot.IsRetired(CatalogSnapshotFixtures.ItemType, 5));
            }
        });
    }

    [Fact]
    public void The_rules_are_the_ordered_set_the_builder_was_handed()
    {
        var rules = new List<RemapRule>
        {
            new(1, 3, CatalogSnapshotFixtures.ItemType, RemapRuleKind.ReplacedBy, 5, 2, default),
            new(2, 4, CatalogSnapshotFixtures.ItemType, RemapRuleKind.Retired, 9, 0, [RemapRule.RetirePolicyPlaceholder]),
        };

        ContentSnapshot snapshot = Builder().WithRules(rules).Build();

        Assert.Equal(2, snapshot.Rules.Count);
        Assert.Equal(1, snapshot.Rules[0].Sequence);
        Assert.Equal(RemapRuleKind.Retired, snapshot.Rules[1].Kind);

        // The list is copied, so a caller mutating its own list afterwards cannot reach into the snapshot.
        rules.Clear();
        Assert.Equal(2, snapshot.Rules.Count);
    }

    [Fact]
    public void A_duplicate_id_and_a_duplicate_key_are_kept_rather_than_refused()
    {
        // KEC0002 and KEC0036 are FINDINGS, so a snapshot that cannot hold the defect is a snapshot the
        // validator can never report it from.
        ContentRow first = Item(2, "iron_sword");
        ContentRow second = Item(2, "iron_sword");

        ContentSnapshot snapshot = Builder().AddRow(first).AddRow(second).Build();

        Assert.Equal(2, snapshot.Rows(CatalogSnapshotFixtures.ItemType).Count);
        Assert.True(snapshot.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out ContentRow? found));
        Assert.Same(first, found);
        Assert.True(snapshot.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("iron_sword"), out int id));
        Assert.Equal(2, id);
    }

    [Fact]
    public void A_row_of_a_type_the_registry_never_registered_is_refused()
    {
        var row = new ContentRow(
            new ContentTypeId(4096),
            1,
            new ContentKey("mystery"),
            0,
            false,
            []);

        Assert.Throws<ContentRegistrationException>(() => { Builder().AddRow(row); });
    }

    [Fact]
    public void The_builder_refuses_a_null_row_a_null_rule_list_and_a_null_hash()
    {
        Assert.Throws<ArgumentNullException>(() => { Builder().AddRow(null!); });
        Assert.Throws<ArgumentNullException>(() => { Builder().WithRules(null!); });
        Assert.Throws<ArgumentNullException>(() => { Builder().WithIdentity(1, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = new ContentSnapshotBuilder(null!); });
    }

    static ContentSnapshotBuilder Builder() => new(CatalogSnapshotFixtures.Registry());

    static ContentRow Item(int id, string key, bool isRetired = false)
        => CatalogSnapshotFixtures.ItemRow(id, key, stackable: false, 1, 100, 2, isRetired);
}
