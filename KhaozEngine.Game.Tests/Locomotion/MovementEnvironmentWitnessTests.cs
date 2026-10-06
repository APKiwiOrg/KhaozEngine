using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentWitnessTests
{
    [Fact]
    public void ResourceCountIsBoundedBeforeAnyEntryIsRead()
    {
        (string status, MovementScopeWitness? witness) = TryCreate(new OversizedUnreadableList());
        Assert.Equal("CapacityExceeded", status);
        Assert.Null(witness);
    }

    [Fact]
    public void IndividualIdentifierLimitRefusesWithoutAWitness()
    {
        (string status, MovementScopeWitness? witness) = TryCreate([new string('x', 1025)]);
        Assert.Equal("CapacityExceeded", status);
        Assert.Null(witness);
    }

    [Fact]
    public void AggregateIdentifierLimitRefusesWithoutATruncatedWitness()
    {
        string[] resources = FullByteBudget().Append("z").ToArray();
        (string status, MovementScopeWitness? witness) = TryCreate(resources);
        Assert.Equal("CapacityExceeded", status);
        Assert.Null(witness);
    }

    [Fact]
    public void ExactlyTheAggregateBudgetIsAccepted()
    {
        (string status, MovementScopeWitness? witness) = TryCreate(FullByteBudget());
        Assert.Equal("Known", status);
        Assert.Equal(64, witness!.ResourceIds.Count);
    }

    [Fact]
    public void ExactlyTheResourceCountIsAccepted()
    {
        string[] resources = Enumerable.Range(0, 256).Select(i => "resource-" + i).ToArray();
        (string status, MovementScopeWitness? witness) = TryCreate(resources);
        Assert.Equal("Known", status);
        Assert.Equal(256, witness!.ResourceIds.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidIdentifiersCannotProduceAWitness(string identifier)
    {
        (string status, MovementScopeWitness? witness) = TryCreate([identifier]);
        Assert.Equal("Invalid", status);
        Assert.Null(witness);
    }

    [Fact]
    public void MalformedUtf16CannotBeReplacedIntoAValidIdentifier()
    {
        (string status, MovementScopeWitness? witness) = TryCreate([new string((char)0xD800, 1)]);
        Assert.Equal("Invalid", status);
        Assert.Null(witness);
    }

    [Fact]
    public void IdentifierBudgetCountsUtf8BytesRatherThanCharacters()
    {
        (string status, MovementScopeWitness? witness) = TryCreate([new string((char)0x00E9, 600)]);
        Assert.Equal("CapacityExceeded", status);
        Assert.Null(witness);
    }

    [Fact]
    public void DuplicateIdentifiersAreInvalidRatherThanSilentlyCollapsed()
    {
        (string status, MovementScopeWitness? witness) = TryCreate(["same", "same"]);
        Assert.Equal("Invalid", status);
        Assert.Null(witness);
    }

    [Fact]
    public void WitnessOwnsAnOrdinallySortedImmutableCopy()
    {
        string[] resources = ["b", "A", "a"];
        (string status, MovementScopeWitness? witness) = TryCreate(resources);
        Assert.Equal("Known", status);
        resources[0] = "changed";
        Assert.Equal(new[] { "A", "a", "b" }, witness!.ResourceIds);
    }

    [Fact]
    public void RepackingBackingResourcesDoesNotChangePortableIdentity()
    {
        (string firstStatus, MovementScopeWitness? first) = TryCreate(["page-old-a", "page-old-b"]);
        (string secondStatus, MovementScopeWitness? second) = TryCreate(["page-new-combined"]);
        Assert.Equal("Known", firstStatus);
        Assert.Equal("Known", secondStatus);
        Assert.Equal(first!.Identity, second!.Identity);
        Assert.True(first!.CoversKnownEmptyRegions);
    }

    [Fact]
    public void ScopeAndWitnessIdentityMismatchIsNotRewritten()
    {
        (string status, MovementScopeWitness? witness) = TryCreate(["directory"], mismatch: true);
        Assert.Equal("Invalid", status);
        Assert.Null(witness);
    }

    static string[] FullByteBudget() => Enumerable.Range(0, 64)
        .Select(i => i.ToString("D4", CultureInfo.InvariantCulture) + new string('x', 1020)).ToArray();

    sealed class OversizedUnreadableList : IReadOnlyList<string>
    {
        public int Count => 257;
        public string this[int index] => throw new InvalidOperationException("Entries must not be read.");
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("Must not enumerate.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static (string Status, MovementScopeWitness? Witness) TryCreate(IReadOnlyList<string> resources,
        bool mismatch = false)
    {
        var identity = new MovementQueryIdentity("closure", 1u, "semantic-scope");
        var scopeIdentity = mismatch ? new MovementQueryIdentity("closure", 1u, "other-scope") : identity;
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 0ul);
        var space = new MovementSpaceKey("world", "cave");
        var scope = new MovementQueryScope(new Vector3(-1f), new Vector3(1f),
            0.5f, 2f, space, scopeIdentity, frame);
        MovementAvailability result = MovementScopeWitness.TryCreate(scope, identity, resources, true,
            out MovementScopeWitness? witness);
        return (result.ToString(), witness);
    }
}
