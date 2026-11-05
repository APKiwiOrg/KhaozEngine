using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceLocalDetailRefusalTests
{
    [Theory]
    [InlineData("item", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("CornerX", MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData("CornerZ", MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData("Owner", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("Owner.Patch.SurfaceId", MapDocumentRefusal.SchemaValueRequired)]
    public void Dependency_admission_identifies_the_first_failed_branch(string field, MapDocumentRefusal reason)
    {
        var patch = SurfacePatchFixtures.Row(1);
        var valid = new MapCornerDependency(1, 1, new(new("owner", 0, 0), MapLatticeAddress.Corner(1, 1)));
        patch.CornerDependencies.Add(field switch
        {
            "item" => null!,
            "CornerX" => valid with { CornerX = 2 },
            "CornerZ" => valid with { CornerZ = 2 },
            "Owner" => valid with { Owner = null! },
            _ => valid with { Owner = valid.Owner with { Patch = new("", 0, 0) } },
        });
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field == "item" ? "CornerDependencies[0]" : $"CornerDependencies[0].{field}", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal(patch.Key, finding.Patch);
        if (reason == MapDocumentRefusal.SurfaceAddressOutOfPatch)
        {
            Assert.Equal(2L, finding.ActualInteger);
            Assert.Equal(0L, finding.MinimumInteger);
            Assert.Equal(1L, finding.MaximumInteger);
            Assert.Equal(field == "CornerX" ? 2 : 1, finding.CellX);
            Assert.Equal(field == "CornerZ" ? 2 : 1, finding.CellZ);
        }
        patch.CornerDependencies[0] = valid;
        Assert.Empty(Findings(patch));
    }

    [Fact]
    public void Dependency_address_preserves_the_denominator_rule()
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.CornerDependencies.Add(new(1, 1, new(new("owner", 0, 0), default)));
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, finding.Reason);
        Assert.Equal("CornerDependencies[0].Owner.Address.Denominator", finding.FieldPath);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(0L, finding.ActualInteger);
        Assert.Equal(1L, finding.MinimumInteger);
        Assert.Equal(64L, finding.MaximumInteger);
        patch.CornerDependencies[0] = patch.CornerDependencies[0] with
        { Owner = new(new("owner", 0, 0), MapLatticeAddress.Create(1, 1, 64)) };
        Assert.Empty(Findings(patch));
    }

    [Theory]
    [InlineData("item", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("CellX", MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData("CellZ", MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData("Edge", MapDocumentRefusal.SchemaEnumUnsupported)]
    public void Edge_admission_identifies_the_first_failed_branch(string field, MapDocumentRefusal reason)
    {
        var patch = SurfacePatchFixtures.Row(1);
        var valid = new MapEdgeSubdivision(0, 0, MapCellEdge.North, 64);
        patch.EdgeSubdivisions.Add(field switch
        {
            "item" => null!,
            "CellX" => valid with { CellX = 1 },
            "CellZ" => valid with { CellZ = 1 },
            _ => valid with { Edge = (MapCellEdge)4 },
        });
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field == "item" ? "EdgeSubdivisions[0]" : $"EdgeSubdivisions[0].{field}", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal(patch.Key, finding.Patch);
        if (reason == MapDocumentRefusal.SurfaceAddressOutOfPatch)
        {
            Assert.Equal(1L, finding.ActualInteger);
            Assert.Equal(0L, finding.MinimumInteger);
            Assert.Equal(0L, finding.MaximumInteger);
        }
        if (field == "Edge")
        {
            Assert.Equal(typeof(MapCellEdge), finding.EnumType);
            Assert.Equal(4L, finding.ActualInteger);
        }
        patch.EdgeSubdivisions[0] = valid;
        Assert.Empty(Findings(patch));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Repeated_metadata_identifies_the_duplicate_key(bool corner)
    {
        var patch = SurfacePatchFixtures.Row(1);
        if (corner)
        {
            patch.CornerDependencies.Add(new(1, 1, new(new("owner", 0, 0), MapLatticeAddress.Corner(1, 1))));
            patch.CornerDependencies.Add(patch.CornerDependencies[0]);
        }
        else
        {
            patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 64));
            patch.EdgeSubdivisions.Add(patch.EdgeSubdivisions[0]);
        }
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, finding.Reason);
        Assert.Equal(corner ? "CornerDependencies[1]" : "EdgeSubdivisions[1]", finding.FieldPath);
        Assert.Equal(1, finding.Index);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(corner ? 1 : 0, finding.CellX);
        Assert.Equal(corner ? 1 : 0, finding.CellZ);
        Assert.Equal(corner ? null : (MapCellEdge?)MapCellEdge.West, finding.Edge);
        Assert.Equal(2, corner ? patch.CornerDependencies.Count : patch.EdgeSubdivisions.Count);
        if (corner) patch.CornerDependencies.RemoveAt(1);
        else patch.EdgeSubdivisions.RemoveAt(1);
        Assert.Empty(Findings(patch));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(65, 64)]
    public void Segment_range_retains_both_inclusive_limits(int refused, int accepted)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, refused));
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, finding.Reason);
        Assert.Equal("EdgeSubdivisions[0].Segments", finding.FieldPath);
        Assert.Equal((long)refused, finding.ActualInteger);
        Assert.Equal(2L, finding.MinimumInteger);
        Assert.Equal(64L, finding.MaximumInteger);
        patch.EdgeSubdivisions[0] = patch.EdgeSubdivisions[0] with { Segments = accepted };
        Assert.Empty(Findings(patch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subdivision_requires_a_present_cell_and_an_exposed_edge(bool interior)
    {
        var patch = SurfacePatchFixtures.Row(interior ? 2 : 1);
        patch.EdgeSubdivisions.Add(new(0, 0, interior ? MapCellEdge.East : MapCellEdge.West, 64));
        patch.SetPresent(interior ? 1 : 0, 0, interior);
        MapDocumentFinding finding = Assert.Single(Findings(patch));
        Assert.Equal(interior ? MapDocumentRefusal.SurfaceSubdivisionInteriorEdge : MapDocumentRefusal.SurfaceSubdivisionAbsentCell, finding.Reason);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal("EdgeSubdivisions[0]", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal(0, finding.CellX);
        Assert.Equal(0, finding.CellZ);
        Assert.Equal(interior ? MapCellEdge.East : MapCellEdge.West, finding.Edge);
        Assert.Equal(interior, patch.IsPresent(interior ? 1 : 0, 0));
        patch.SetPresent(interior ? 1 : 0, 0, !interior);
        Assert.Empty(Findings(patch));
    }

    [Theory]
    [InlineData("rotation", MapDocumentRefusal.ValueOutsideRange, 4, 3)]
    [InlineData("cut", MapDocumentRefusal.SchemaEnumUnsupported, 4, 3)]
    [InlineData("topology", MapDocumentRefusal.SchemaEnumUnsupported, 3, 2)]
    public void Topology_admission_identifies_rotation_cut_or_topology(string field, MapDocumentRefusal reason, int refused, int accepted)
    {
        var ex = Assert.Throws<MapDocumentException>(() => Split(refused));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(field, ex.FieldPath);
        Assert.Equal((long)refused, ex.ActualInteger);
        if (field == "rotation")
        {
            Assert.Equal(0L, ex.MinimumInteger);
            Assert.Equal(3L, ex.MaximumInteger);
        }
        else Assert.Equal(field == "cut" ? typeof(MapOverlayCut) : typeof(MapCellTopology), ex.EnumType);
        _ = Split(accepted);

        bool Split(int value) => MapSurfaceTopology.SplitSwNe(0, 0, 0, 0,
            field == "cut" ? (MapOverlayCut)value : MapOverlayCut.Full,
            field == "rotation" ? (byte)value : (byte)0,
            field == "topology" ? (MapCellTopology)value : MapCellTopology.Auto);
    }

    [Fact]
    public void Local_findings_preserve_projection_order_and_own_their_snapshot()
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 1));
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 64));
        IReadOnlyList<MapDocumentFinding> findings = Findings(patch);
        Assert.Equal(new[] { MapDocumentRefusal.ValueOutsideRange, MapDocumentRefusal.IdentityDuplicate }, findings.Select(f => f.Reason));
        patch.EdgeSubdivisions.Clear();
        Assert.Equal(2, findings.Count);
        if (findings is IList<MapDocumentFinding> list) Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Empty(Findings(patch));
    }

    static IReadOnlyList<MapDocumentFinding> Findings(MapSurfacePatch patch)
    {
        IReadOnlyList<MapDocumentFinding> findings = patch.ValidateLocalFindings();
        Assert.Equal(patch.ValidateLocal(), findings.Select(f => f.Message));
        return findings;
    }
}
