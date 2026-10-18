using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using BepuCompound = BepuPhysics.Collidables.Compound;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using RigidPose = BepuPhysics.RigidPose;
using TypedIndex = BepuPhysics.Collidables.TypedIndex;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    public static IEnumerable<object[]> RepresentedCurvedRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (bool aboutX in new[] { false, true })
                foreach (int sign in new[] { -1, 1 })
                    yield return [capsule, aboutX, sign];
    }

    // Layout 0 installs the log alone. Layouts 1 and 2 install it as the first or second compound child beside a
    // distant sphere, so leaf ids and raw element ids follow the installed child order.
    [Theory]
    [MemberData(nameof(RepresentedCurvedRows))]
    public void RepresentedCurvedColumnFeedsEverySupportRole(bool capsule, bool aboutX, int sign)
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, MathF.PI / 2);
        foreach (float radius in new[] { 0.1f, 0.15f, 0.3f })
            for (int layout = 0; layout < 3; layout++)
            {
                using var scene = new SupportFootprintScene();
                scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
                StaticHandle owner = layout == 0 ? InstallLyingLog(scene, capsule, radius, rotation) :
                    InstallLyingLogCompound(scene, capsule, radius, rotation, layout == 2);
                int leafId = layout == 2 ? 1 : 0;
                SupportCurvedLeaf leaf = CheckRepresentedCapture(scene, owner, leafId);
                int side = leafId * SupportNeighborhoodOracle.TangentLeafStride + SupportNeighborhoodCurved.Side;
                using var lease = scene.World.AcquireQueryReadLease();
                // Flank, crest and one end. Longitudinal coordinates follow the installed axis.
                foreach ((float across, float along) in new[] { (sign * 0.25f, 0f), (0f, 0f), (0f, sign * 0.55f) })
                {
                    var query = LyingLogQuery(radius, aboutX, across);
                    query = query with { Axis = aboutX ? new(across, along) : new(along, across) };
                    SupportFootprintResult result = CheckRepresentedFootprint(scene, owner, leaf,
                        FootSupport.DescribeFootprint(query), lease, out SupportFootprintMember[] members);
                    if (along != 0) continue;
                    SupportFootprintMember member = Assert.Single(members, m => m.ElementId == side);
                    int expectedRawElementId = side;
                    Assert.Equal(expectedRawElementId, member.ElementId);
                    bool axisMeetsLog = Math.Abs(across) <= radius;
                    Assert.Equal(axisMeetsLog ? 2 : 1, member.WitnessCount);
                    Assert.Equal(axisMeetsLog ? SupportWitnessRole.Axis : SupportWitnessRole.Footprint, member.First.Role);
                    if (axisMeetsLog) Assert.Equal(SupportWitnessRole.Footprint, member.Second.Role);
                    // The proposal probe's lowest point lies at or below the highest certified point under its disc.
                    Assert.Equal(SupportProposalStatus.Contact, result.FootVerdict.Status);
                    Assert.True(result.FootVerdict.LowestLower <= result.FootVerdict.LowestUpper);
                    Assert.True(result.FootVerdict.LowestLower <= members.Max(m => m.DiscHeightUpper));
                    SupportSample sample = FootSupport.Find(null, scene.World, lease, query);
                    Assert.Equal(SupportStatus.Walkable, sample.Status);
                    Assert.Equal(owner, sample.Static);
                    Assert.Equal(expectedRawElementId, sample.FeatureId);
                    Assert.InRange(sample.HeightError, 0f, 0.0005f);
                }
                CheckRepresentedTangents(scene, owner, lease, new(0, 2 * radius, 0), side);
            }
    }

    public static IEnumerable<object[]> RepresentedCurvedControlRows() =>
        new[] { "sphere", "upright-capsule", "upright-cylinder", "cyclic-capsule", "cyclic-cylinder",
            "boundary-capsule", "boundary-cylinder", "tilt-positive", "tilt-negative" }.Select(row => new object[] { row });

    // Exact installs keep their columns. A genuine tilt keeps its irrational normalized axis. The boundary pose's
    // real reading tilts by about -2e-8, while its represented column is exactly horizontal, so it certifies in
    // the closed-form horizontal stratum.
    [Theory]
    [MemberData(nameof(RepresentedCurvedControlRows))]
    public void RepresentedCurvedControlsKeepTheirColumns(string row)
    {
        bool cylinder = row.EndsWith("cylinder", StringComparison.Ordinal) || row.StartsWith("tilt", StringComparison.Ordinal);
        Quaternion rotation = row switch
        {
            "cyclic-capsule" or "cyclic-cylinder" => new(0.5f, 0.5f, 0.5f, 0.5f),
            "boundary-capsule" or "boundary-cylinder" => new(0.6f, 0, 0.3741657f, 0.70710677f),
            "tilt-positive" => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6),
            "tilt-negative" => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 6),
            _ => Quaternion.Identity,
        };
        PhysicsShape shape = row == "sphere" ? new SphereShape(0.3f) : cylinder ? new CylinderShape(0.3f, 1) :
            new CapsuleShape(0.3f, 1);
        Vector3 centre = new(0, 1, 0);
        // The engine cylinder is placed at its base.
        Vector3 position = cylinder ? centre - Vector3.Transform(new Vector3(0, 0.5f, 0), rotation) : centre;
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(shape, new(position, rotation));
        SupportCurvedLeaf leaf = CheckRepresentedCapture(scene, owner, 0);
        bool tilted = row.StartsWith("tilt", StringComparison.Ordinal);
        bool boundary = row.StartsWith("boundary", StringComparison.Ordinal);
        Assert.Equal(boundary ? 0 : BitConverter.SingleToInt32Bits(leaf.SourceColumn.Y),
            BitConverter.SingleToInt32Bits(leaf.SourceColumn.Y));
        if (!boundary)
            Assert.Equal(!tilted, leaf.Axis.X.Lower == leaf.Axis.X.Upper && leaf.Axis.Y.Lower == leaf.Axis.Y.Upper &&
                leaf.Axis.Z.Lower == leaf.Axis.Z.Upper);
        float top = row switch
        {
            "sphere" or "cyclic-capsule" or "cyclic-cylinder" or "boundary-capsule" or "boundary-cylinder" => 1.3f,
            "upright-capsule" => 1.8f,
            "upright-cylinder" => 1.5f,
            _ => 1.4f,
        };
        // The two tilts mirror each other about X, and so do their queries.
        var query = new FootSupportQuery(new(row == "tilt-negative" ? -0.05f : 0.05f, 0.05f), top, 0.2f, 0.4f, 0.4f,
            MathF.Cos(MathF.PI / 4));
        using var lease = scene.World.AcquireQueryReadLease();
        CheckRepresentedFootprint(scene, owner, leaf, FootSupport.DescribeFootprint(query), lease, out _, !tilted);
        SupportSample sample = FootSupport.Find(null, scene.World, lease, query);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(owner, sample.Static);
        Assert.InRange(sample.HeightError, 0f, 0.0005f);
        if (!tilted) CheckRepresentedTangents(scene, owner, lease, new(0, top, 0),
            row == "upright-cylinder" ? SupportNeighborhoodCurved.TopCap : 0);
    }

    // The requests of the former tilted boundary rows. Their source box touched the horizontal stratum. The
    // represented column lies in it exactly, so the closed-form oracle decides every role.
    [Theory]
    [InlineData(false, 0f, 0.70710677f)]
    [InlineData(true, 0f, 0.70710677f)]
    [InlineData(false, 0.1f, 0.70710677f)]
    [InlineData(true, 0.1f, 0.70710677f)]
    [InlineData(false, 0f, 1f)]
    [InlineData(true, 0f, 1f)]
    [InlineData(false, 0.1f, 1f)]
    [InlineData(true, 0.1f, 1f)]
    public void RepresentedCurvedBoundaryPoseCertifiesInTheHorizontalStratum(bool capsule, float z, float cosine)
    {
        using var scene = new SupportFootprintScene();
        var orientation = new Quaternion(0.6f, 0, 0.3741657f, 0.70710677f);
        Vector3 centre = new(0, -0.25f, 0);
        BepuCompound.GetWorldPose(new(new Vector3(0, 0.5f, 0), Quaternion.Identity), new(Vector3.Zero, orientation),
            out RigidPose lift);
        StaticHandle owner = scene.Install(capsule ? new CapsuleShape(0.25f, 1) : new CylinderShape(0.25f, 1),
            new(capsule ? centre : centre - lift.Position, orientation));
        SupportCurvedLeaf leaf = CheckRepresentedCapture(scene, owner, 0);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(leaf.SourceColumn.Y));
        using var lease = scene.World.AcquireQueryReadLease();
        CheckRepresentedFootprint(scene, owner, leaf, Request(z: z, lower: -2, upper: 2, cosine: cosine), lease, out _,
            closedForm: true);
    }

    [Fact]
    public void RepresentedCurvedColumnFeedsEverySupportRoleOnlyThroughTheSupportDoor()
    {
        Quaternion quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        (string Name, PhysicsShape Shape, Pose Pose)[] statics =
        [
            ("sphere", new SphereShape(0.3f), new(new(0, 0.3f, 0), tilt)),
            ("capsule", new CapsuleShape(0.3f, 1), new(new(0, 0.3f, 0), quarter)),
            ("cylinder", new CylinderShape(0.3f, 1), new(new Vector3(0, 0.3f, 0) - Vector3.Transform(new Vector3(0, 0.5f, 0), quarter), quarter)),
            ("compound", new CompoundShape([new(new CapsuleShape(0.3f, 1), new(Vector3.Zero, quarter)),
                new(new CylinderShape(0.2f, 0.5f), new(new(1.5f, -0.3f, 0), tilt)),
                new(new SphereShape(0.25f), new(new(-1.5f, -0.05f, 0), Quaternion.Identity))]),
                new(new(0, 0.3f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f))),
        ];
        foreach ((string name, PhysicsShape shape, Pose pose) in statics)
        {
            using var scene = new SupportFootprintScene();
            scene.Install(shape, pose);
            using var lease = scene.World.AcquireQueryReadLease();
            var members = new SupportFootprintMember[256];
            var elements = new SupportElement[256];
            var joins = new ulong[1024];
            var dualAdmissions = new StrongBox<int>();
            StrongBox<int>? prior = InstalledPoseOperator.ObserveDualAdmissions(dualAdmissions);
            SupportNeighborhoodResult neighborhood;
            SupportFootprintResult footprint;
            try
            {
                neighborhood = ((IPhysicsSupportNeighborhood)scene.World).QuerySupportNeighborhood(
                    lease, new CapsuleShape(0.2f, 0.01f), Pose.At(new(0, 0.805f, 0)), 0.0001f, elements, joins);
                footprint = Capability(scene.World).QuerySupportFootprint(lease, Request(z: 0.05f, lower: 0.2, upper: 0.8),
                    members, joins);
            }
            finally { InstalledPoseOperator.ObserveDualAdmissions(prior); }
            Assert.True(CapsuleFeatureStatus.Complete == neighborhood.Status, $"{name} neighborhood {neighborhood.Status}");
            Assert.True(neighborhood.Elements > 0, name);
            Assert.True(CapsuleFeatureStatus.Complete == footprint.Status, $"{name} footprint {footprint.Status}");
            Assert.True(footprint.Members > 0, name);
            Assert.True(dualAdmissions.Value == 0, $"{name} reached {dualAdmissions.Value} dual admissions");
        }
    }

    [Fact]
    public void RepresentedCurvedRadicalsRemainGuardedAfterPointSources()
    {
        // The installed quarter-turn column about Z. Its length is irrational.
        int[] bits = InstalledSupportPoseTests.CoefficientBits(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2))[3..6];
        Vector3 column = new(BitConverter.Int32BitsToSingle(bits[0]), BitConverter.Int32BitsToSingle(bits[1]),
            BitConverter.Int32BitsToSingle(bits[2]));
        Assert.NotEqual(0f, column.Y);
        var arithmetic = new SupportCurvedSourceArithmetic();
        arithmetic.Reset(column);
        var x = arithmetic.X; var y = arithmetic.Y;
        var squared = arithmetic.Add(arithmetic.Square(x), arithmetic.Square(y));
        var length = arithmetic.Root(squared);
        // A singleton source does not make its root exact.
        Assert.True(arithmetic.Bounds(length).Lower < arithmetic.Bounds(length).Upper);
        var k = arithmetic.Divide(y, length);
        var u = arithmetic.Divide(x, length);
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(arithmetic.Subtract(arithmetic.Add(arithmetic.Square(k),
            arithmetic.Square(u)), arithmetic.One)));
        // Exact slope equality between two distinct radical expressions of the same cosine.
        var slope = arithmetic.Divide(arithmetic.Square(y), squared);
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(arithmetic.Subtract(arithmetic.Square(k), slope)));
        Assert.Equal(GeometrySign.Positive, arithmetic.Sign(k));
        Assert.Equal(GeometrySign.Negative, arithmetic.Sign(u));
        // Publication still encloses the arithmetic error of the irrational unit axis.
        GeometryVectorOutput published = GeometryVectorOperations.Publish(new(arithmetic.Bounds(u), arithmetic.Bounds(k),
            GeometryInterval.Exact(0)));
        Assert.True(published.IsResolved && published.Error > 0);
        // A captured leaf of the same column keeps an outward, nonsingleton unit axis.
        var leaves = new List<SupportCurvedLeaf>();
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodCurved.Leaf(SupportCurvedShape.Cylinder, 0.25f,
            0.5f, new RigidPose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), 0, leaves));
        Assert.True(Assert.Single(leaves).Axis.X.Lower < leaves[0].Axis.X.Upper);
        // Genuine bounded failure: an undecided constant expression beyond the compile ceiling refuses.
        int checkpoint = arithmetic.Checkpoint;
        var forward = arithmetic.Zero;
        var backward = arithmetic.Zero;
        for (int i = 0; i < 40; i++)
        {
            var a = arithmetic.Constant(0.1f + i * 0.0137f);
            var b = arithmetic.Constant(0.3f - i * 0.0041f);
            forward = arithmetic.Add(forward, arithmetic.Multiply(arithmetic.Multiply(a, b), x));
        }
        for (int i = 39; i >= 0; i--)
        {
            var a = arithmetic.Constant(0.1f + i * 0.0137f);
            var b = arithmetic.Constant(0.3f - i * 0.0041f);
            backward = arithmetic.Add(backward, arithmetic.Multiply(a, arithmetic.Multiply(b, x)));
        }
        var difference = arithmetic.Subtract(forward, backward);
        Assert.True(arithmetic.Bounds(difference).Lower < 0 && arithmetic.Bounds(difference).Upper > 0);
        Assert.Equal(GeometrySign.Unresolved, arithmetic.Sign(difference));
        arithmetic.Restore(checkpoint);
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(arithmetic.Subtract(arithmetic.Square(k), slope)));
    }

    static StaticHandle InstallLyingLogCompound(SupportFootprintScene scene, bool capsule, float radius,
        Quaternion rotation, bool reversed)
    {
        Vector3 centre = new(0, radius, 0);
        Vector3 root = capsule ? centre : centre - Vector3.Transform(new Vector3(0, 0.5f, 0), rotation);
        PhysicsShape log = capsule ? new CapsuleShape(radius, 1) : new CylinderShape(radius, 1);
        // The sphere sits far beside the log at its own height, whatever the root rotation.
        Vector3 beside = Vector3.Transform(new Vector3(1.5f, 0, 1.5f) + centre - root, Quaternion.Conjugate(rotation));
        CompoundChild first = new(log, Pose.Identity), second = new(new SphereShape(0.05f), new(beside, Quaternion.Identity));
        CompoundChild[] children = reversed ? [second, first] : [first, second];
        return scene.Install(new CompoundShape(children), new(root, rotation));
    }

    // Both curved capture doors return the same leaf. Its source column is the represented Y of the actual
    // installed leaf pose, bit for bit, and its unit axis encloses the exact normalization of that column.
    static SupportCurvedLeaf CheckRepresentedCapture(SupportFootprintScene scene, StaticHandle owner, int leafId)
    {
        var simulation = SimulationOf(scene.World);
        simulation.Statics.GetDescription(HandlesOf(scene.World)[owner.Value].Handle, out var description);
        TypedIndex shape = description.Shape;
        RigidPose installed = description.Pose;
        if (shape.Type == default(BepuCompound).TypeId)
        {
            ref var child = ref simulation.Shapes.GetShape<BepuCompound>(shape.Index).Children[leafId];
            BepuCompound.GetWorldPose(child.LocalPose, description.Pose, out installed);
            shape = child.ShapeIndex;
        }
        var leaves = new List<SupportCurvedLeaf>();
        Assert.Equal(CapsuleFeatureStatus.Complete,
            SupportNeighborhoodCurved.Capture(simulation, description.Shape, description.Pose, leaves));
        SupportCurvedLeaf leaf = Assert.Single(leaves, l => l.LeafId == leafId);
        int[] expectedColumnBits = InstalledSupportPoseTests.CoefficientBits(installed.Orientation)[3..6];
        Assert.Equal(expectedColumnBits, ColumnBits(leaf));
        V column = InstalledPoseOracle.Represented(installed.Orientation).Y;
        B length = B.Root(column.LengthSquared);
        var unit = new P((B)column.X / length, (B)column.Y / length, (B)column.Z / length);
        bool axisEnclosureContainsIndependentNormalizedColumn =
            R.From(leaf.Axis.X.Lower) <= unit.X.Lower && unit.X.Upper <= R.From(leaf.Axis.X.Upper) &&
            R.From(leaf.Axis.Y.Lower) <= unit.Y.Lower && unit.Y.Upper <= R.From(leaf.Axis.Y.Upper) &&
            R.From(leaf.Axis.Z.Lower) <= unit.Z.Lower && unit.Z.Upper <= R.From(leaf.Axis.Z.Upper);
        Assert.True(axisEnclosureContainsIndependentNormalizedColumn);
        Assert.Equal(FeaturePoint.Exact(installed.Position).Bounds, leaf.Centre);
        var direct = new List<SupportCurvedLeaf>();
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodCurved.Leaf(simulation, shape, installed, leafId, direct));
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodCurved.Leaf(leaf.Shape, (float)leaf.Radius,
            (float)leaf.HalfLength, installed, leafId, direct));
        Assert.Equal(new[] { leaf, leaf }, direct);
        return leaf;
    }

    static int[] ColumnBits(SupportCurvedLeaf leaf) => [BitConverter.SingleToInt32Bits(leaf.SourceColumn.X),
        BitConverter.SingleToInt32Bits(leaf.SourceColumn.Y), BitConverter.SingleToInt32Bits(leaf.SourceColumn.Z)];

    // Cold, cached and reference evaluations publish the same members, which match the independent oracle.
    SupportFootprintResult CheckRepresentedFootprint(SupportFootprintScene scene, StaticHandle owner,
        SupportCurvedLeaf leaf, SupportFootprintRequest request, IPhysicsQueryLease lease,
        out SupportFootprintMember[] owned, bool closedForm = false)
    {
        CheckLyingLogParts(scene, owner, request, horizontal: closedForm, lease: lease);
        SupportFootprintMember[]? first = null;
        SupportFootprintResult firstResult = default;
        foreach (bool reference in new[] { false, true })
        {
            using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
            for (int pass = 0; pass < 2; pass++)
            {
                var members = new SupportFootprintMember[256];
                SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members,
                    new ulong[1024]);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                SupportFootprintMember[] mine = [.. members.Take(result.Members).Where(m => m.Static == owner)];
                Assert.NotEmpty(mine);
                foreach (SupportFootprintMember member in mine)
                {
                    int part = member.ElementId - leaf.LeafId * SupportNeighborhoodOracle.TangentLeafStride;
                    Assert.InRange(part, 0, leaf.Parts - 1);
                    Assert.Equal(SupportElementKind.Tangent, member.Kind);
                    if (member.WitnessCount == 2)
                    {
                        Assert.Equal(SupportWitnessRole.Axis, member.First.Role);
                        Assert.Equal(SupportWitnessRole.Footprint, member.Second.Role);
                    }
                }
                if (first is null) (first, firstResult) = (mine, result);
                else
                {
                    Assert.Equal(first, mine);
                    Assert.Equal(firstResult.AxisVerdict, result.AxisVerdict);
                    Assert.Equal(firstResult.FootVerdict, result.FootVerdict);
                }
            }
        }
        owned = first!;
        return firstResult;
    }

    // A probe resting on the top of the leaf meets its tangent, within the published errors.
    static void CheckRepresentedTangents(SupportFootprintScene scene, StaticHandle owner, IPhysicsQueryLease lease,
        Vector3 top, int elementId)
    {
        const float radius = 0.2f, band = 0.0001f;
        var lowest = new Vector3(top.X, top.Y + band / 2, top.Z);
        var elements = new SupportElement[256];
        SupportNeighborhoodResult result = ((IPhysicsSupportNeighborhood)scene.World).QuerySupportNeighborhood(lease,
            new CapsuleShape(radius, 0.01f), Pose.At(lowest + new Vector3(0, radius + 0.005f, 0)), band, elements,
            new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        SupportElement[] tangents = [.. elements.Take(result.Elements)
            .Where(e => e.Static == owner && e.Kind == SupportElementKind.Tangent)];
        Assert.Contains(tangents, e => e.ElementId == elementId);
        OracleVector lower = OracleVector.From(lowest + new Vector3(0, radius, 0));
        SupportNeighborhoodOracle.AssertTangentsMatch(tangents, SupportNeighborhoodOracle.InstalledCurved(scene.World, owner),
            lower, lower + new OracleVector(0, 0.01, 0), radius, band);
    }
}
