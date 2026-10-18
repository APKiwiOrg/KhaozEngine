using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    [InlineData(FootprintPartition.Mesh)]
    public void YawCrateAtInclusiveBandUpperMatchesOracle(FootprintPartition partition)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 axis = InstallYawCrate(scene, partition);
        var request = Request(x: axis.X, z: axis.Z, upper: 0.25);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.Equal(SupportProposalStatus.Contact, result.AxisVerdict.Status);
        Assert.Equal(SupportProposalStatus.Contact, result.FootVerdict.Status);
        Assert.Equal(0.25, result.AxisVerdict.LowestLower);
        Assert.Equal(0.25, result.AxisVerdict.LowestUpper);
        Assert.Equal(0.25, result.FootVerdict.LowestLower);
        Assert.Equal(0.25, result.FootVerdict.LowestUpper);
        AssertSourceFallbacks();
    }

    [Fact]
    public void YawCrateAtInclusiveBandLowerMatchesOracle()
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 axis = InstallYawCrate(scene, FootprintPartition.Boxes);
        var request = Request(x: axis.X, z: axis.Z, lower: 0.25);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.Equal(0.25, result.FootVerdict.LowestLower);
        Assert.Equal(0.25, result.FootVerdict.LowestUpper);
        AssertSourceFallbacks();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HorizontalBandFallbackRetainsEveryContactMaximum(bool lowerFirst)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 axis = InstallYawCrate(scene, FootprintPartition.Boxes, lowerFirst ? 0.24995f : 0.25f);
        InstallYawCrate(scene, FootprintPartition.Boxes, lowerFirst ? 0.25f : 0.24995f);
        var request = Request(x: axis.X, z: axis.Z, upper: 0.25);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.True(members.Take(result.Members).Count(m => m.HasDiscMaximum && m.ProposalMask == 3) >= 2);
        Assert.Equal(0.25, result.AxisVerdict.LowestLower);
        Assert.Equal(0.25, result.AxisVerdict.LowestUpper);
        Assert.Equal(0.25, result.FootVerdict.LowestLower);
        Assert.Equal(0.25, result.FootVerdict.LowestUpper);
        AssertSourceFallbacks();
    }

    [Fact]
    public void YawCrateJustAboveBandKeepsCertifiedRefusal()
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        float top = MathF.BitIncrement(0.25f);
        Vector3 axis = InstallYawCrate(scene, FootprintPartition.Boxes, top);
        var request = Request(x: axis.X, z: axis.Z, upper: 0.25);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.True(result.AxisVerdict.LowestLower > request.BandUpper);
        Assert.True(result.FootVerdict.LowestLower > request.BandUpper);
        Assert.Equal(SupportStatus.Refused, FootSupport.Find(null, null, scene.World, lease,
            new(new(axis.X, axis.Z), 0, request.Radius, 0.25f, 0.4f, request.CosMaxSlope)).Status);
        AssertSourceFallbacks();
    }

    [Fact]
    public void RepresentedSourceContactBandMatchesIndependentRationalVerdict()
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f);
        scene.Install(new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2), new(-2, 0, 2)],
            [0, 1, 2, 0, 2, 3]), new(new(0, 0.125f, 0), rotation));
        // Preserve the historical question derived between the legacy coefficient-box endpoint contacts.
        Assert.True(InstalledPoseOperator.TryCreate(new BepuPhysics.RigidPose(new(0, 0.125f, 0), rotation), out var legacy));
        OracleFace Sample(double xy)
        {
            V Transform(float x, float z) => new(R.From(legacy.X.X.Bounds.Lower) * R.From(x) + R.From(legacy.Z.X.Bounds.Lower) * R.From(z),
                R.From(xy) * R.From(x) + R.From(legacy.Z.Y.Bounds.Lower) * R.From(z) + R.From(0.125),
                R.From(legacy.X.Z.Bounds.Lower) * R.From(x) + R.From(legacy.Z.Z.Bounds.Lower) * R.From(z));
            V[] exact = [Transform(-2, -2), Transform(-2, 2), Transform(2, 2), Transform(2, -2)];
            return new(default, 0, exact.Select(v => new OracleVector(SupportFootprintExactOracle.AsDouble(v.X),
                SupportFootprintExactOracle.AsDouble(v.Y), SupportFootprintExactOracle.AsDouble(v.Z))).ToArray())
            { ExactVertices = exact };
        }
        var low = SupportFootprintExactOracle.Contact(Sample(legacy.X.Y.Bounds.Lower), Request().FootProposal)!.Value;
        var high = SupportFootprintExactOracle.Contact(Sample(legacy.X.Y.Bounds.Upper), Request().FootProposal)!.Value;
        double boundary = SupportFootprintExactOracle.AsDouble((low.Upper + high.Lower) / new R(2, 1));
        Assert.True(low.Upper < R.From(boundary));
        Assert.True(high.Lower > R.From(boundary));
        var request = Request(upper: boundary);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256);
        var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(79UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(before.Skip(result.Members), members.Skip(result.Members));
        Assert.All(joins.Skip(result.Members * result.JoinWordsPerRow), word => Assert.Equal(79UL, word));
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    [InlineData(FootprintPartition.Mesh)]
    public void WarmYawCrateBandFallbackAllocatesNothing(FootprintPartition partition)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 axis = InstallYawCrate(scene, partition);
        var request = Request(x: axis.X, z: axis.Z, upper: 0.25);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var capability = Capability(scene.World);
        using var region = KhaozEngine.Tests.Locomotion.Contacts.SupportAllocationRegion.Start();
        Assert.Equal(CapsuleFeatureStatus.Complete,
            capability.QuerySupportFootprint(lease, request, members, joins).Status);
        SupportQueryDiagnostics.Reset();
        var phases = new KhaozEngine.Tests.Locomotion.Contacts.SupportCostAttributionTests.PhaseRecorder(false);
        ContactAllocationDiagnostics.SetObserver(phases.Observe);
        phases.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        SupportFootprintResult result;
        try
        {
            using var route = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Route);
            result = capability.QuerySupportFootprint(lease, request, members, joins);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        foreach (var phase in phases.Rows()) _output.WriteLine($"{phase}");
        Assert.Equal(bytes, phases.Bytes.Sum());
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(0.25, result.FootVerdict.LowestUpper);
        Assert.Equal(0, bytes);
        AssertSourceFallbacks();
        _output.WriteLine($"Yaw crate band partition={partition} warmBytes={bytes} exactFallbacks={SupportQueryDiagnostics.Snapshot().ExactFallbacks}");
    }

    static Vector3 InstallYawCrate(SupportFootprintScene scene, FootprintPartition partition, float top = 0.25f)
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.65f);
        Vector3[] corners = [new(-0.25f, -0.125f, -0.25f), new(0.25f, -0.125f, -0.25f),
            new(-0.25f, 0.125f, -0.25f), new(0.25f, 0.125f, -0.25f), new(-0.25f, -0.125f, 0.25f),
            new(0.25f, -0.125f, 0.25f), new(-0.25f, 0.125f, 0.25f), new(0.25f, 0.125f, 0.25f)];
        PhysicsShape shape = partition switch
        {
            FootprintPartition.Boxes => new BoxShape(new(0.25f, 0.125f, 0.25f)),
            FootprintPartition.Hulls => new ConvexHullShape(corners),
            // The wall's first triangle has horizontal and diagonal edges from vertex A.
            _ => new TriangleMeshShape(corners, [2, 3, 7, 2, 7, 6, 0, 4, 6, 0, 6, 2]),
        };
        scene.Install(shape, new(new(0, top - 0.125f, 0), rotation));
        return Vector3.Transform(new Vector3(-0.2f, 0, 0), rotation);
    }
}
