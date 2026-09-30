using System;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.EntryOrderFixtures;

namespace KhaozEngine.Tests.ItemInstances;

public class InstanceEntryOrderRegistrationTests
{
    static readonly InstanceSlotKind[] Scalar = [InstanceSlotKind.Varint];
    static readonly InstanceReferenceTarget[] EntryReference = [new("mod", InstanceReferenceSite.Entry, 0)];

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void An_unknown_entry_order_is_refused_before_the_kind_is_registered(int order)
    {
        Refuses(Shape(default, InstanceCountWidth.Byte, Scalar, order), EntryReference);
    }

    [Fact]
    public void Sorted_order_requires_a_repeating_field()
    {
        Refuses(Shape(Scalar, InstanceCountWidth.None, default), []);
    }

    [Fact]
    public void Sorted_order_requires_a_supported_count_width()
    {
        Refuses(Shape(default, (InstanceCountWidth)255, Scalar), EntryReference);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sorted_order_refuses_nesting_in_the_header_or_entries(bool header)
    {
        InstanceSlotKind[] nested = [InstanceSlotKind.Varint, InstanceSlotKind.NestedPayload];
        Refuses(Shape(header ? nested : default, InstanceCountWidth.Byte, header ? Scalar : nested), EntryReference);
    }

    [Fact]
    public void Sorted_order_requires_one_entry_reference_instead_of_a_header_reference()
    {
        Refuses(Shape(Scalar, InstanceCountWidth.Byte, Scalar), [new("mod", InstanceReferenceSite.Header, 0)]);
    }

    [Fact]
    public void Sorted_order_refuses_ambiguous_multiple_entry_references()
    {
        Refuses(Shape(default, InstanceCountWidth.Byte, new[] { InstanceSlotKind.Varint, InstanceSlotKind.Varint }),
            [new("mod", InstanceReferenceSite.Entry, 0), new("item", InstanceReferenceSite.Entry, 1)]);
    }

    [Fact]
    public void Sorted_order_refuses_a_reference_to_a_non_scalar_slot()
    {
        Refuses(Shape(default, InstanceCountWidth.Byte, new[] { (InstanceSlotKind)255 }), EntryReference);
    }

    [Fact]
    public void Sorted_order_refuses_an_unknown_reference_site()
    {
        Refuses(Shape(default, InstanceCountWidth.Byte, Scalar), [new("mod", (InstanceReferenceSite)255, 0)]);
    }

    static void Refuses(InstanceFieldShape shape, InstanceReferenceTarget[] references)
    {
        var registry = new InstancePropertyRegistry();
        ArgumentException exception = Assert.Throws<ArgumentException>(() => registry.Register(
            InstanceKindBand.Game, 2048, InstancePropertyCodec.ShapeOnly, PropertyVisibility.Everyone,
            -1, shape, references));
        Assert.Contains("2048", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGet(2048, out _));
    }
}
