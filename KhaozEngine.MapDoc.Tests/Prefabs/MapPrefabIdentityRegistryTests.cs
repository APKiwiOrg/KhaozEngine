using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabIdentityRegistryTests
{
    [Theory]
    [InlineData(MapPrefabChildKind.Placement)]
    [InlineData(MapPrefabChildKind.Record)]
    public void BindingUniqueness_IdentifiesTheTypedWorldNamespace(MapPrefabChildKind kind)
    {
        var f = PrefabFixtures.Create();
        var instance = f.Document.Prefabs!.Instances.Single();
        var first = new MapPrefabBinding(new(kind, "first"), "world", null, false);
        var duplicate = first with { Child = new(kind, "second") };
        f.Document.Prefabs = new(1, 1, [instance with { Bindings = new([first, duplicate]) }], []);
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => new MapPrefabIdentityRegistry(f.Document));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, refusal.Reason);
        Assert.Equal(("world", kind, instance.Id, "prefab binding"),
            (refusal.EntityId, refusal.ChildKind, refusal.InstanceId, refusal.SubjectKind));
        Assert.Equal("world", refusal.WorldId);
        Assert.Equal(duplicate.Child, refusal.Child);
        Assert.Equal(before, Snapshot(f.Document));

        // One reservation per (kind, world ID) is accepted, including equal text in another kind's namespace.
        var otherKind = kind == MapPrefabChildKind.Record ? MapPrefabChildKind.Surface : MapPrefabChildKind.Record;
        f.Document.Prefabs = new(1, 1, [instance with { Bindings = new([first, duplicate with { Child = new(otherKind, "second") }]) }], []);
        new MapPrefabIdentityRegistry(f.Document).Finish();
    }

    [Fact]
    public void DuplicateTransfer_IdentifiesItsWorldNamespace()
    {
        var f = PrefabFixtures.Create("active-transfer");
        var transfer = Assert.Single(f.Document.Prefabs!.Transfers);
        f.Document.Prefabs = new(1, 1, f.Document.Prefabs.Instances, [transfer, transfer with { InstanceId = "other" }]);
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => new MapPrefabIdentityRegistry(f.Document));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, refusal.Reason);
        Assert.Equal((transfer.Binding.WorldId, transfer.Binding.Child.Kind, "other", "prefab transfer"),
            (refusal.EntityId, refusal.ChildKind, refusal.InstanceId, refusal.SubjectKind));
        Assert.Equal(before, Snapshot(f.Document));
        f.Document.Prefabs = new(1, 1, f.Document.Prefabs.Instances, [transfer]);
        Feed(f.Document).Finish();
    }

    [Theory]
    [InlineData("live")]
    [InlineData("packed")]
    [InlineData("instance")]
    [InlineData("removed")]
    [InlineData("binding")]
    public void TransferFromReservedBinding_RequiresItsExactUnpackedSource(string fault)
    {
        var f = PrefabFixtures.Create();
        var source = f.Document.Prefabs!.Instances.Single();
        var binding = source.Bindings.Entries.First();
        var acceptedSource = source with { Removed = true, Unpacked = true, Bindings = new([binding]) };
        var acceptedTransfer = new MapPrefabBindingTransfer(source.Id, binding) { Retired = true };
        var submittedSource = fault switch
        {
            "live" => acceptedSource with { Removed = false },
            "packed" => acceptedSource with { Unpacked = false },
            "removed" => acceptedSource with { Bindings = new([binding with { Removed = true }]) },
            _ => acceptedSource,
        };
        var submittedTransfer = fault switch
        {
            "instance" => acceptedTransfer with { InstanceId = "other" },
            "binding" => acceptedTransfer with { Binding = binding with { Child = new(binding.Child.Kind, "other") } },
            _ => acceptedTransfer,
        };
        f.Document.Prefabs = new(1, 1, [submittedSource], [submittedTransfer]);
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => new MapPrefabIdentityRegistry(f.Document));
        Assert.Equal(MapDocumentRefusal.AuthoredOwnershipMismatch, refusal.Reason);
        Assert.Equal(submittedTransfer.InstanceId, refusal.InstanceId);
        Assert.Equal((binding.WorldId, binding.Child.Kind, "prefab transfer"),
            (refusal.EntityId, refusal.ChildKind, refusal.SubjectKind));
        Assert.Equal(before, Snapshot(f.Document));
        f.Document.Prefabs = new(1, 1, [acceptedSource], [acceptedTransfer]);
        new MapPrefabIdentityRegistry(f.Document).Finish();
    }

    [Fact]
    public void PlacementTransfer_RequiresTheSameNumericIdentity()
    {
        var f = PrefabFixtures.Create("active-transfer");
        var placement = Assert.Single(f.Document.Placements);
        long? expected = placement.NumericId;
        placement.NumericId++;
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => Feed(f.Document));
        Assert.Equal(MapDocumentRefusal.AuthoredOwnershipMismatch, refusal.Reason);
        Assert.Equal((placement.Id, MapPrefabChildKind.Placement, placement.NumericId, expected),
            (refusal.EntityId, refusal.ChildKind, refusal.NumericId, refusal.ExpectedNumericId));
        Assert.Equal(placement.Id, refusal.PlacementId);
        Assert.Equal(before, Snapshot(f.Document));
        placement.NumericId = expected;
        Feed(f.Document).Finish();
    }

    [Fact]
    public void ActiveTransfer_RequiresExactlyOneStandaloneOwner()
    {
        var f = PrefabFixtures.Create("active-transfer");
        var registry = new MapPrefabIdentityRegistry(f.Document);
        var transfer = Assert.Single(f.Document.Prefabs!.Transfers);
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(registry.Finish);
        Assert.Equal(MapDocumentRefusal.AuthoredOwnershipMismatch, refusal.Reason);
        Assert.Equal((transfer.Binding.WorldId, transfer.Binding.Child.Kind, transfer.InstanceId),
            (refusal.EntityId, refusal.ChildKind, refusal.InstanceId));
        Assert.Equal(0L, refusal.ActualCount);
        Assert.Equal(1L, refusal.ExpectedCount);
        Assert.Equal(before, Snapshot(f.Document));
        registry.AddPlacements(f.Document.Placements);
        registry.Finish();
    }

    [Fact]
    public void RetiredTransfer_ReservesItsTypedWorldIdentity()
    {
        var f = PrefabFixtures.Create("retired-live");
        var transfer = Assert.Single(f.Document.Prefabs!.Transfers);
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => Feed(f.Document));
        Assert.Equal(MapDocumentRefusal.IdentityReserved, refusal.Reason);
        Assert.Equal((transfer.Binding.WorldId, transfer.Binding.Child.Kind, transfer.InstanceId),
            (refusal.EntityId, refusal.ChildKind, refusal.InstanceId));
        Assert.Equal(before, Snapshot(f.Document));
        f.Document.Prefabs = new(1, 1, f.Document.Prefabs.Instances, [transfer with { Retired = false }]);
        Feed(f.Document).Finish();
    }

    [Theory]
    [InlineData("placement", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("producer", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("transfer", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("child", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("retirement", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("anchor", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("actual-anchor", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("pin", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("digest", MapDocumentRefusal.DigestMismatch)]
    public void AttachmentAdmission_ReportsTheFailedRuleAndTypedSubject(string fault, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.Create("attachment");
        var original = f.Document.Prefabs!;
        var row = Assert.Single(original.Attachments);
        var surface = row.Children.Single(c => c.LocalChild.Kind == MapPrefabChildKind.Surface);
        var record = row.Children.Single(c => c.LocalChild.Kind == MapPrefabChildKind.Record);
        var subject = fault is "anchor" or "actual-anchor" ? record : surface;
        var changed = fault switch
        {
            "transfer" => subject with { WorldId = "missing-world" },
            "child" => subject with { LocalChild = new(subject.LocalChild.Kind, "other") },
            "anchor" => subject with { Anchor = null },
            "actual-anchor" => subject with { Anchor = new("other", 0, 0) },
            "digest" => subject with { SourceDigest = new('d', 64) },
            _ => subject,
        };
        var submitted = row with
        {
            PlacementId = fault == "placement" ? "missing-placement" : row.PlacementId,
            Retired = fault == "retirement",
            Children = row.Children.Select(c => c == subject ? changed : c).ToArray(),
        };
        f.Document.Prefabs = new(1, 1, original.Instances, original.Transfers)
        {
            Attachments = fault == "producer" ? [submitted, row with { ResourceId = "another-support" }] : [submitted],
        };
        var poses = f.Document.NativeResourceTransforms;
        if (fault == "pin") f.Document.NativeResourceTransforms = poses!.Where(p => p.OwnerId != subject.WorldId).ToArray();
        string before = Snapshot(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(Feed(f.Document).Finish);
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(submitted.PlacementId, refusal.PlacementId);
        Assert.Equal(fault == "placement" ? submitted.PlacementId : changed.WorldId, refusal.EntityId);
        Assert.Equal(fault == "placement" ? MapPrefabChildKind.Placement : changed.LocalChild.Kind, refusal.ChildKind);
        if (fault != "placement") Assert.Equal(changed.LocalChild, refusal.Child);
        if (fault is "anchor" or "actual-anchor")
        {
            Assert.Equal(changed.Anchor, refusal.Patch);
            Assert.Equal(record.Anchor, refusal.ActualAnchor);
        }
        if (fault == "digest")
        {
            Assert.Equal(subject.SourceDigest, refusal.ExpectedHash);
            Assert.Equal(changed.SourceDigest, refusal.ActualHash);
        }
        Assert.Equal(before, Snapshot(f.Document));
        f.Document.Prefabs = original;
        f.Document.NativeResourceTransforms = poses;
        Feed(f.Document).Finish();
    }

    static MapPrefabIdentityRegistry Feed(MapDocument document)
    {
        var registry = new MapPrefabIdentityRegistry(document);
        registry.AddSurfaces(document.Surfaces.Refs.Select(s => s.Id));
        foreach (var (key, patch) in document.Surfaces.Patches) registry.AddRecords(key, patch.Records.Select(r => r.Id));
        registry.AddPlacements(document.Placements);
        return registry;
    }

    static string Snapshot(MapDocument document) => MapPrefabJson.Digest("test/identity-admission", document);
}
