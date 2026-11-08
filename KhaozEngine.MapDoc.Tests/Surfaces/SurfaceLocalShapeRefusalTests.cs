using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceLocalShapeRefusalTests
{
    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 2, 0)]
    [InlineData(false, 0, 2)]
    public void Corner_access_distinguishes_rectangle_from_coordinate(bool rectangle, int x, int z)
    {
        var patch = SurfacePatchFixtures.Row(1);
        if (rectangle) patch.CellMinX = 64;
        var ex = Assert.Throws<MapDocumentException>(() => patch.CornerAddress(x, z));
        Assert.Equal(rectangle ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.SurfaceAddressOutOfPatch, ex.Reason);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal(x, ex.CellX);
        Assert.Equal(z, ex.CellZ);
        if (!rectangle)
        {
            Assert.Equal(x == 2 ? "x" : "z", ex.FieldPath);
            Assert.Equal(2L, ex.ActualInteger);
            Assert.Equal(0L, ex.MinimumInteger);
            Assert.Equal(1L, ex.MaximumInteger);
        }
        patch.CellMinX = 63;
        Assert.Equal(MapLatticeAddress.Corner(64, 1), patch.CornerAddress(1, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Height_access_identifies_a_missing_or_mismatched_array(bool missing)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Heights = missing ? null! : new int[3];
        var ex = Assert.Throws<MapDocumentException>(() => patch.Height(1, 1));
        Assert.Equal(missing ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaArrayLengthMismatch, ex.Reason);
        Assert.Equal(nameof(MapSurfacePatch.Heights), ex.FieldPath);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal(missing ? null : (long?)3, ex.ActualCount);
        Assert.Equal(4L, ex.ExpectedCount);
        patch.Heights = new int[4];
        Assert.Equal(0, patch.Height(1, 1));
    }

    [Theory]
    [InlineData("Heights", true, 4)]
    [InlineData("Heights", false, 4)]
    [InlineData("Cells", true, 1)]
    [InlineData("Cells", false, 1)]
    [InlineData("Presence", true, 1)]
    [InlineData("Presence", false, 1)]
    public void Local_arrays_identify_null_or_wrong_length(string field, bool missing, long expected)
    {
        var patch = SurfacePatchFixtures.Row(1);
        if (field == "Heights") patch.Heights = missing ? null! : Array.Empty<int>();
        else if (field == "Cells") patch.Cells = missing ? null! : Array.Empty<MapSurfaceCell>();
        else patch.Presence = missing ? null! : Array.Empty<ulong>();
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(missing ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaArrayLengthMismatch, finding.Reason);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(field, finding.FieldPath);
        Assert.Equal(missing ? null : (long?)0, finding.ActualCount);
        Assert.Equal(expected, finding.ExpectedCount);
        if (field == "Heights") patch.Heights = new int[4];
        else if (field == "Cells") patch.Cells = new MapSurfaceCell[1];
        else patch.Presence = [1];
        Assert.Empty(Findings(patch));
    }

    [Theory]
    [InlineData("Rotation", MapDocumentRefusal.ValueOutsideRange, 4, 3)]
    [InlineData("Flags", MapDocumentRefusal.SchemaEnumUnsupported, 32, 31)]
    [InlineData("Cut", MapDocumentRefusal.SchemaEnumUnsupported, 4, 3)]
    [InlineData("Topology", MapDocumentRefusal.SchemaEnumUnsupported, 3, 2)]
    public void Local_cell_fields_identify_their_own_domain(string field, MapDocumentRefusal reason, int refused, int accepted)
    {
        var patch = SurfacePatchFixtures.Row(1);
        Set(refused);
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal($"Cells[0].{field}", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal((long)refused, finding.ActualInteger);
        if (field == "Rotation")
        {
            Assert.Equal(0L, finding.MinimumInteger);
            Assert.Equal(3L, finding.MaximumInteger);
        }
        else Assert.Equal(field == "Flags" ? typeof(MapCellFlags) : field == "Cut" ? typeof(MapOverlayCut) : typeof(MapCellTopology), finding.EnumType);
        Set(accepted);
        Assert.Empty(Findings(patch));

        void Set(int value) => patch.Cells[0] = field switch
        {
            "Rotation" => patch.Cells[0] with { Rotation = (byte)value },
            "Flags" => patch.Cells[0] with { Flags = (MapCellFlags)value },
            "Cut" => patch.Cells[0] with { Cut = (MapOverlayCut)value },
            _ => patch.Cells[0] with { Topology = (MapCellTopology)value },
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Local_admission_identifies_surface_or_rectangle(bool surface)
    {
        var patch = SurfacePatchFixtures.Row(1);
        if (surface) patch.Key = new("", 0, 0);
        else patch.CellMinX = 64;
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(surface ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.BoundsInvalid, finding.Reason);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(surface ? "Key.SurfaceId" : "rectangle", finding.FieldPath);
        patch.Key = new("r", 0, 0);
        patch.CellMinX = 63;
        Assert.Empty(Findings(patch));
    }

    [Fact]
    public void Presence_padding_carries_the_word_index_and_retains_the_used_bits()
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Presence = [3];
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(MapDocumentRefusal.SurfacePresencePaddingNonzero, finding.Reason);
        Assert.Equal(nameof(MapSurfacePatch.Presence), finding.FieldPath);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(0, finding.Index);
        Assert.Equal(3UL, patch.Presence[0]);
        patch.Presence = [1];
        Assert.Empty(Findings(patch));
        Assert.True(patch.IsPresent(0, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Local_metadata_capacity_retains_the_count_and_admits_the_exact_limit(bool corners)
    {
        var patch = SurfacePatchFixtures.Row(1);
        if (corners)
        {
            for (int z = 0; z <= 1; z++)
                for (int x = 0; x <= 1; x++)
                    patch.CornerDependencies.Add(new(x, z, new(new("owner", 0, 0), MapLatticeAddress.Corner(x, z))));
            patch.CornerDependencies.Add(patch.CornerDependencies[0]);
        }
        else
        {
            foreach (var edge in Enum.GetValues<MapCellEdge>()) patch.EdgeSubdivisions.Add(new(0, 0, edge, 2));
            patch.EdgeSubdivisions.Add(patch.EdgeSubdivisions[0]);
        }
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(corners ? nameof(MapSurfacePatch.CornerDependencies) : nameof(MapSurfacePatch.EdgeSubdivisions), finding.FieldPath);
        Assert.Equal(corners ? "CornerDependencies" : "EdgeSubdivisions", finding.Budget);
        Assert.Equal(5L, finding.ActualCount);
        Assert.Equal(4L, finding.Limit);
        // A fifth unique item cannot fit. The capacity reason distinguishes the earlier refusal from duplicates.
        Assert.Equal(5, corners ? patch.CornerDependencies.Count : patch.EdgeSubdivisions.Count);
        if (corners) patch.CornerDependencies.RemoveAt(4);
        else patch.EdgeSubdivisions.RemoveAt(4);
        Assert.Empty(Findings(patch));
    }

    static IReadOnlyList<MapDocumentFinding> Findings(MapSurfacePatch patch)
    {
        var findings = new List<MapDocumentFinding>();
        IReadOnlyList<string> errors = patch.ValidateLocal(findings);
        Assert.Equal(errors, findings.Select(f => f.Message));
        return findings;
    }
}
