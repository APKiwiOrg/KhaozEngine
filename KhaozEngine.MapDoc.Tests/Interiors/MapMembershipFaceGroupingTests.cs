using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipFaceGroupingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubdividedTransformedPatch_AdmitsBothGroupsBeforeAllocationAndHoldsThemThroughConsumption(bool accepted)
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        foreach (var edge in new[] { MapCellEdge.South, MapCellEdge.East, MapCellEdge.North, MapCellEdge.West })
            patch.EdgeSubdivisions.Add(new(0, 0, edge, 32));
        var local = MapSurfaceCompiler.Compile(surface, patch);
        // Each of the two parents has two 32-segment exterior edges and one diagonal, making 65 children.
        Assert.Equal(130, local.Faces.Count);
        var pose = new MapResourceTransform(new(16, 2, 8, .4, 1));
        var vertices = local.ExactVertices.Select(pose.Point).ToArray();
        var world = new MapCompiledPatch(local.Key, local.Role, new(0, 0, 0), local.VertexIds, vertices,
            vertices.Select(p => new Vector3(p.X.ToSingle(), p.Y.ToSingle(), p.Z.ToSingle())), local.Faces, local.Paint, []);
        // Two final face copies and two one-cell dictionaries, plus the larger active grouping's growth,
        // sorted buffer, key array and index array. The grouping builder can hold twice its face count.
        long face = Unsafe.SizeOf<MapCompiledFace>(), key = Unsafe.SizeOf<MapFaceKey>();
        long bytes = 1024 + 2 * (256 + 130 * face) + 130 * (3 * face + key + 4);
        long allowance = accepted ? bytes : bytes - 1;
        int entered = 0;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance), _ => entered++);
        using var product = admission.BeginPreparation();
        using var build = new MapMembershipBuild(admission, product, new MapRoofPreparationCounts());
        if (!accepted)
        {
            var refusal = Assert.Throws<MapSceneCapacityException>(() => new MapMembershipFaceGroups(local, world, 1));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", bytes, allowance), refusal.Capacity);
            Assert.Equal(0, entered);
            Assert.Equal(0, admission.WorkspaceBytes);
            return;
        }
        using (var groups = new MapMembershipFaceGroups(local, world, 1))
        {
            Assert.Equal(bytes, admission.WorkspaceBytes);
            Assert.Equal(local.Faces.OrderBy(f => f.Key), groups.Local[0]);
            Assert.Equal(world.Faces.OrderBy(f => f.Key), groups.World[0]);
            Assert.NotSame(groups.Local[0], groups.World[0]);
            Assert.Equal(pose.Point(local.ExactTriangle(groups.Local[0][0]).A), world.ExactTriangle(groups.World[0][0]).A);
            Assert.Equal(bytes, admission.WorkspaceBytes);
        }
        Assert.Equal(1, entered);
        Assert.Equal(bytes, admission.PeakBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
