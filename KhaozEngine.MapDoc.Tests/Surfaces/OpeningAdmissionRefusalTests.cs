using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class OpeningAdmissionRefusalTests
{
    [Theory]
    [InlineData("owner", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("empty", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("negative", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("past-slot", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("past-patch", MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("present", MapDocumentRefusal.SpaceOpeningCellsInvalid)]
    public void Opening_refuses_the_actual_owner_cell_or_presence_rule(string fault, MapDocumentRefusal reason)
    {
        var (surface, patch, original) = BoundaryFixtures.TwoCellOpening();
        if (fault == "past-slot")
        {
            patch.CellMinX = patch.CellMinZ = 63;
            patch.Width = patch.Depth = 1;
            patch.Heights = new int[4];
            patch.Cells = new[] { CompilerFixtures.Full };
            patch.Presence = new[] { 0UL };
            original = original with { SlotCells = new[] { 4095 } };
            patch.Records.Clear();
            patch.Records.Add(original);
        }
        if (fault == "past-patch") patch.SetPresent(2, 0, false);
        var opening = fault switch
        {
            "owner" => original with { Patch = new("other", 0, 0) },
            "empty" => original with { SlotCells = Array.Empty<int>() },
            "negative" => original with { SlotCells = new[] { -1 } },
            "past-slot" => original with { SlotCells = new[] { 4096 } },
            "past-patch" => original with { SlotCells = new[] { 64 } },
            "duplicate" => original with { SlotCells = new[] { 0, 0 } },
            _ => original,
        };
        if (fault == "present") patch.SetPresent(0, 0, true);
        var before = patch.Presence.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapOpeningBoundary.Compile(surface, patch, opening));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(opening.Id, error.RecordId);
        Assert.Equal(opening.Patch, error.Patch);
        if (fault == "owner") Assert.Equal(patch.Key, error.ActualAnchor);
        if (fault == "empty")
        {
            Assert.Equal(0L, error.ActualCount);
            Assert.Equal(1L, error.MinimumInteger);
        }
        if (fault is "negative" or "past-slot")
        {
            Assert.Equal(fault == "negative" ? -1 : 4096, error.SlotCell);
            Assert.Equal(0L, error.MinimumInteger);
            Assert.Equal(4095L, error.MaximumInteger);
        }
        if (fault == "past-patch") Assert.Equal(1, error.CellZ);
        if (fault is "duplicate" or "present") Assert.Equal(0, error.SlotCell);
        Assert.Equal(before, patch.Presence);
        if (fault == "present") patch.SetPresent(0, 0, false);
        var control = fault is "empty" or "negative" ? original with { SlotCells = new[] { 0 } }
            : fault == "past-patch" ? original with { SlotCells = new[] { 2 } } : original;
        Assert.NotEmpty(MapOpeningBoundary.Compile(surface, patch, control).Triangles);
    }

    [Fact]
    public void Opening_geometry_refusal_retains_the_record_and_patch()
    {
        var (surface, patch, original) = BoundaryFixtures.TwoCellOpening();
        var key = patch.Key;
        patch.Key = new(surface.Id, long.MaxValue, 0);
        var opening = original with { Patch = patch.Key };
        var error = Assert.Throws<MapDocumentException>(() => MapOpeningBoundary.Compile(surface, patch, opening));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(opening.Id, error.RecordId);
        Assert.Equal(patch.Key, error.Patch);
        patch.Key = key;
        Assert.NotEmpty(MapOpeningBoundary.Compile(surface, patch, original).Triangles);
    }
}
