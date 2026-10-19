using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public class ConvexQueryContractTests(ITestOutputHelper output)
{
    static ConvexQueryShape Duck => new(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, 0.45f, 0.5f);

    [Fact]
    public void ConeCylinderValuePreservesAllRepresentedBits()
    {
        float lower = MathF.BitIncrement(0.4f), ring = MathF.BitDecrement(0.45f);
        ConvexQueryShape value = new(ConvexQueryKind.ConeCylinder, 0.2f, lower, ring, 0.5f);
        Assert.Equal(1, (int)value.Kind);
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.2f), BitConverter.SingleToUInt32Bits(value.Radius));
        Assert.Equal(BitConverter.SingleToUInt32Bits(lower), BitConverter.SingleToUInt32Bits(value.LowerY));
        Assert.Equal(BitConverter.SingleToUInt32Bits(ring), BitConverter.SingleToUInt32Bits(value.RingY));
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.5f), BitConverter.SingleToUInt32Bits(value.UpperY));
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.45f), BitConverter.SingleToUInt32Bits(Duck.RingY));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(value.Margin));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(value.WithMargin(-0f).Margin));
        Assert.Equal(0.001f, value.WithMargin(0.001f).Margin);
        Assert.Equal(value, value.WithMargin(0.001f).WithMargin(0));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(
            new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 1, -0f, 1, 2, -0f).Margin));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(
            new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 1, -0f, 1, 2).LowerY));
        Assert.False(default(ConvexQueryShape).IsValid);
    }

    [Fact]
    public void InvalidOrCollapsedShapeRejects()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, invalid, 0.4f, 0.45f, 0.5f));
            Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, invalid, 0.45f, 0.5f));
            Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, invalid, 0.5f));
            Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, 0.45f, invalid));
            Assert.Throws<ArgumentException>(() => Duck.WithMargin(invalid));
        }
        foreach (float radius in new[] { -1f, -0f, 0f })
            Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, radius, 0.4f, 0.45f, 0.5f));
        foreach (float margin in new[] { -0.001f, MathF.BitDecrement(0.001f), MathF.BitIncrement(0.001f), 1f })
            Assert.Throws<ArgumentException>(() => Duck.WithMargin(margin));
        foreach (ConvexQueryKind kind in new[] { (ConvexQueryKind)0, (ConvexQueryKind)2, (ConvexQueryKind)(-1) })
            Assert.Throws<ArgumentException>(() => new ConvexQueryShape(kind, 0.2f, 0.4f, 0.45f, 0.5f));
        Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, 0.4f, 0.5f));
        Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, 0.5f, 0.5f));
        Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.5f, 0.45f, 0.4f));
        float top = MathF.BitIncrement(1f), midpoint = 0.5f * (1f + top);
        Assert.Throws<ArgumentException>(() => new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 1f, midpoint, top));
        Assert.Throws<ArgumentException>(() => default(ConvexQueryShape).WithMargin(0.001f));
    }

    [Fact]
    public void ContinuousCircleSupportEnclosesEveryAzimuth()
    {
        // Pythagorean directions attain exact disc extrema at rational circle points.
        // The support value is invariant under every azimuth, rather than a polygon sample.
        ConvexQueryShape shape = new(ConvexQueryKind.ConeCylinder, 2, 1, 2, 3);
        foreach ((int x, int z, int length) in new[] { (1, 0, 1), (3, 4, 5), (5, 12, 13), (8, 15, 17) })
            foreach (int sx in new[] { -1, 1 })
                foreach (int sz in new[] { -1, 1 })
                {
                    Encloses(ShortBodyShellOracle.SupportBounds(shape, new(sx * x, 0, sz * z)), 2 * length);
                    Encloses(ShortBodyShellOracle.SupportBounds(shape, new(sz * z, 1, sx * x)), 3 + 2 * length);
                    Encloses(ShortBodyShellOracle.SupportBounds(shape, new(sx * x, -2 * length, sz * z)), -2 * length);
                }
        Encloses(ShortBodyShellOracle.SupportBounds(shape, Vector3.UnitY), 3);
        Encloses(ShortBodyShellOracle.SupportBounds(shape, -Vector3.UnitY), -1);
        Encloses(ShortBodyShellOracle.SupportBounds(shape, Vector3.Zero), 0);
        Encloses(ShortBodyShellOracle.SupportBounds(shape, new(float.Epsilon, 0, 0)), 2 * (double)float.Epsilon);
        Encloses(ShortBodyShellOracle.SupportBounds(shape, new(float.MaxValue, 0, 0)), 2 * (double)float.MaxValue);
        var irrational = ShortBodyShellOracle.SupportBounds(shape, new(1, 0, 1));
        Assert.True(irrational.Lower >= 2.828427124746189);
        Assert.True(irrational.Upper <= 2.828427124746191);
        ConvexQueryShape grown = shape.WithMargin(0.001f);
        Encloses(ShortBodyShellOracle.SupportBounds(grown, new(3, 0, 4)), 10 + 5 * (double)0.001f);
        Encloses(ShortBodyShellOracle.SupportBounds(grown, -Vector3.UnitY), -1 + (double)0.001f);
        Encloses(ShortBodyShellOracle.SupportBounds(grown, new(0, 3, 4)), 17 + 5 * (double)0.001f);
    }

    [Fact]
    public void RequiredBodyPlaneSurplusIsProved()
    {
        float angle = MathF.PI * 45f / 180f + MathF.PI * 3f / 180f;
        float duckTop = 2f * 0.25f, duckRing = 0.5f * (0.4f + duckTop);
        float cowTop = 2f * 0.95f, cowRing = 0.5f * (0.4f + cowTop);
        var duck = ShortBodyShellOracle.Clearance(0.2f, 0.4f, duckRing, duckTop, angle, 0.001f);
        var cow = ShortBodyShellOracle.Clearance(0.9f, 0.4f, cowRing, cowTop, angle, 0.001f);
        output.WriteLine($"angle={angle:R}, bits={BitConverter.SingleToUInt32Bits(angle):X8}");
        output.WriteLine($"duck surplus=[{duck.Lower:R}, {duck.Upper:R}]");
        output.WriteLine($"cow surplus=[{cow.Lower:R}, {cow.Upper:R}]");
        var duckClearance = ShortBodyShellOracle.Clearance(0.2f, 0.4f, duckRing, duckTop, angle, 0);
        var cowClearance = ShortBodyShellOracle.Clearance(0.9f, 0.4f, cowRing, cowTop, angle, 0);
        output.WriteLine($"duck clearance=[{duckClearance.Lower:R}, {duckClearance.Upper:R}]");
        output.WriteLine($"cow clearance=[{cowClearance.Lower:R}, {cowClearance.Upper:R}]");
        Assert.True(duck.Lower >= 0.15147);
        Assert.True(cow.Lower >= 0.09966);
        Assert.True(duck.Upper - duck.Lower <= 1e-15);
        Assert.True(cow.Upper - cow.Lower <= 1e-15);
        Encloses(ShortBodyShellOracle.Clearance(2, 1, 2, 3, 0, 0.001f), 1 - (double)0.001f);
    }

    [Fact]
    public void DefaultsRefuseWithoutUsableFields()
    {
        ConvexSweepResult sweep = default;
        Assert.Equal(ConvexSweepStatus.Unresolved, sweep.Status);
        Assert.Equal(0f, sweep.ClearThroughDistance);
        Assert.Null(sweep.ImpactDistance);
        Assert.Equal(0f, sweep.CertifiedErrorMetres);
        Assert.Null(sweep.Hit);
        Assert.Equal(default, sweep.Identity);
        Assert.Equal(new ConvexDistanceResult(false, false, 0, 0, default, default, default, default), default);
        Assert.Equal(new ConvexContactResult(false, 0, 0, 0), default);
        Assert.Equal(new ConvexPenetrationResult(false, false, default, 0, 0, default), default);
    }

    static SweepHit Hit(float distance) => new(distance, Vector3.Zero, Vector3.UnitX, new StaticHandle(7));

    [Fact]
    public void SweepCarriesAnOrderedBracketAndOperationalHit()
    {
        var clear = new ConvexSweepResult(ConvexSweepStatus.Clear, 2, null, 0, null, default);
        Assert.Equal(2f, clear.ClearThroughDistance);
        Assert.Null(clear.Hit);
        Assert.Equal(default, clear.Identity);
        var hit = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1.0005f, 0.001f, Hit(1.00025f), default);
        Assert.Equal(Hit(1.00025f), hit.Hit);
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Clear, 1, null, 0, Hit(1), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Clear, 1, 1, 0, null, default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, null, 0, Hit(1), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, null, default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 2, 1, 0.001f, Hit(1), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 2, 0.001f, Hit(1), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, Hit(2), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult((ConvexSweepStatus)3, 0, null, 0, null, default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Unresolved, 1, null, 0, null, default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Unresolved, 0, null, 0, Hit(0), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Unresolved, 0, null, 0.001f, null, default));
    }

    [Fact]
    public void SweepHitsDistinguishTwoDynamicBodiesWithIdenticalOperationalHits()
    {
        SweepHit operational = new(1, Vector3.Zero, Vector3.UnitX, null);
        var firstIdentity = new ConvexContactIdentity(true, 7, -1, 11);
        var secondIdentity = new ConvexContactIdentity(true, 8, -1, 11);
        var first = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, operational, firstIdentity);
        var second = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, operational, secondIdentity);
        Assert.Equal(first.Hit, second.Hit);
        Assert.Null(first.Hit!.Value.Body);
        Assert.Equal(firstIdentity, first.Identity);
        Assert.Equal(secondIdentity, second.Identity);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SweepHitsDistinguishTwoCoincidentMeshTrianglesUnderOneStatic()
    {
        SweepHit operational = Hit(1);
        var firstIdentity = new ConvexContactIdentity(false, 7, -1, 0);
        var secondIdentity = new ConvexContactIdentity(false, 7, -1, 1);
        var first = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, operational, firstIdentity);
        var second = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, operational, secondIdentity);
        Assert.Equal(first.Hit, second.Hit);
        Assert.Equal(new StaticHandle(7), first.Hit!.Value.Body);
        Assert.Equal(firstIdentity, first.Identity);
        Assert.Equal(secondIdentity, second.Identity);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SweepHitsDistinguishTwoCoincidentCompoundChildrenUnderOneStatic()
    {
        SweepHit operational = Hit(1);
        var firstIdentity = new ConvexContactIdentity(false, 7, 0, 11);
        var secondIdentity = new ConvexContactIdentity(false, 7, 1, 11);
        var first = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, operational, firstIdentity);
        var second = new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, operational, secondIdentity);
        Assert.Equal(first.Hit, second.Hit);
        Assert.Equal(new StaticHandle(7), first.Hit!.Value.Body);
        Assert.Equal(firstIdentity, first.Identity);
        Assert.Equal(secondIdentity, second.Identity);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(ConvexSweepStatus.Clear)]
    [InlineData(ConvexSweepStatus.Unresolved)]
    public void SweepWithoutHitRejectsEveryNonzeroIdentityComponent(ConvexSweepStatus status)
    {
        Assert.Equal(default, new ConvexSweepResult(status, 0, null, 0, null, default).Identity);
        foreach (ConvexContactIdentity identity in new[]
        {
            new ConvexContactIdentity(true, 0, 0, 0),
            new ConvexContactIdentity(false, 1, 0, 0),
            new ConvexContactIdentity(false, 0, 1, 0),
            new ConvexContactIdentity(false, 0, 0, 1)
        })
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(status, 0, null, 0, null, identity));
    }

    [Fact]
    public void DistanceDistinguishesEmptySeparatedAndOverlapWithRawIdentity()
    {
        var identity = new ConvexContactIdentity(true, 7, 3, 11);
        var empty = new ConvexDistanceResult(true, false, 0, 0, default, default, default, default);
        Assert.True(empty.Complete);
        Assert.False(empty.HasTarget);
        var separated = new ConvexDistanceResult(true, true, 1, 1, Vector3.UnitX, default, Vector3.UnitX, identity);
        Assert.Equal(identity, separated.Identity);
        Assert.Equal(Vector3.UnitX, separated.QueryWitness);
        Assert.Equal(Vector3.Zero, separated.TargetWitness);
        Assert.Equal(Vector3.UnitX, separated.Normal);
        Assert.NotEqual(identity, identity with { FeatureId = 12 });
        Assert.NotEqual(identity, identity with { Dynamic = false });
        Assert.NotEqual(identity, identity with { ChildIndex = 4 });
        Assert.NotEqual(identity, identity with { BodyHandle = 8 });
        Assert.Equal(0f, new ConvexDistanceResult(true, true, 0, 0, default, default, default, identity).UpperDistance);
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(false, true, 0, 0, default, default, default, identity));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, false, 0, 0, default, default, default, identity));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, -1, 0, default, default, default, identity));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 2, 1, default, default, Vector3.UnitX, identity));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 1, 1, default, default, default, identity));
    }

    [Fact]
    public void ContactCapacityRefusalIsAtomicAndPenetrationIsSeparate()
    {
        var identity = new ConvexContactIdentity(false, 0, 2, 4);
        var contact = new ConvexContact(Vector3.UnitY, -0.0005f, 0, identity);
        Assert.Equal(identity, contact.Identity);
        Assert.Equal(-0.0005f, contact.LowerSeparation);
        Assert.Equal(0f, contact.UpperSeparation);
        Assert.Equal(4, new ConvexContactResult(false, 0, 4, 0).RequiredCapacity);
        Assert.Equal(4, new ConvexContactResult(true, 4, 4, 0.001f).Written);
        Assert.Throws<ArgumentException>(() => new ConvexContactResult(false, 1, 4, 0));
        Assert.Throws<ArgumentException>(() => new ConvexContactResult(true, 3, 4, 0));
        Assert.Throws<ArgumentException>(() => new ConvexContactResult(false, 0, -1, 0));
        Assert.Throws<ArgumentException>(() => new ConvexContactResult(false, 0, 4, 0.001f));
        Assert.Throws<ArgumentException>(() => new ConvexContact(Vector3.Zero, 0, 0, identity));
        Assert.Throws<ArgumentException>(() => new ConvexContact(Vector3.UnitY, 1, 0, identity));
        var penetration = new ConvexPenetrationResult(true, true, Vector3.UnitY, 1, 1, identity);
        Assert.Equal(identity, penetration.Identity);
        Assert.Equal(Vector3.UnitY, penetration.Translation);
        Assert.Equal(1f, penetration.LowerDepth);
        Assert.Equal(1f, penetration.UpperDepth);
        Assert.True(new ConvexPenetrationResult(true, false, default, 0, 0, default).Complete);
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(false, true, Vector3.UnitY, 1, 1, identity));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, false, default, 0, 0, identity));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, default, 1, 1, identity));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, Vector3.UnitY, 2, 1, identity));
    }

    [Fact]
    public void NonfiniteResultEvidenceRejects()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Vector3 vector = new(invalid, 0, 0);
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Clear, invalid, null, 0, null, default));
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Clear, 1, null, invalid, null, default));
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, Hit(invalid), default));
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, new(1, vector, Vector3.UnitY, null), default));
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 1, 1, 0, new(1, default, vector, null), default));
            Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, invalid, 1, default, default, Vector3.UnitX, default));
            Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 1, invalid, default, default, Vector3.UnitX, default));
            Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 1, 1, vector, default, Vector3.UnitX, default));
            Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 1, 1, default, vector, Vector3.UnitX, default));
            Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 1, 1, default, default, vector, default));
            Assert.Throws<ArgumentException>(() => new ConvexContact(vector, 0, 0, default));
            Assert.Throws<ArgumentException>(() => new ConvexContact(Vector3.UnitY, invalid, 0, default));
            Assert.Throws<ArgumentException>(() => new ConvexContact(Vector3.UnitY, 0, invalid, default));
            Assert.Throws<ArgumentException>(() => new ConvexContactResult(true, 1, 1, invalid));
            Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, vector, 1, 1, default));
            Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, Vector3.UnitY, invalid, 1, default));
            Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, Vector3.UnitY, 1, invalid, default));
        }
    }

    [Fact]
    public void EvidenceCannotExceedTheExistingBudgetOrLeakPartialFields()
    {
        foreach (float invalid in new[] { -1f, MathF.BitIncrement(0.001f) })
        {
            Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Clear, 1, null, invalid, null, default));
            Assert.Throws<ArgumentException>(() => new ConvexContactResult(true, 1, 1, invalid));
        }
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Clear, -1, null, 0, null, default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 0, float.NaN, 0, Hit(0), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Hit, 0, 0, 0, new(0, default, default, null), default));
        Assert.Throws<ArgumentException>(() => new ConvexSweepResult(ConvexSweepStatus.Unresolved, 0, 0, 0, null, default));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, true, 0, 1, default, default, Vector3.UnitX, default));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(false, false, 1, 1, default, default, default, default));
        Assert.Throws<ArgumentException>(() => new ConvexDistanceResult(true, false, 0, 0, Vector3.UnitX, default, default, default));
        Assert.Throws<ArgumentException>(() => new ConvexContact(Vector3.UnitY, 0, 1, default));
        Assert.Throws<ArgumentException>(() => new ConvexContactResult(true, -1, 0, 0));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, Vector3.UnitY, 0, 1, default));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, Vector3.UnitY, -1, 0, default));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(true, true, Vector3.UnitY, 0, 0, default));
        Assert.Throws<ArgumentException>(() => new ConvexPenetrationResult(false, false, Vector3.UnitY, 0, 0, default));
    }

    [Fact]
    public void OracleRefusesInvalidShapesDirectionsAndAngles()
    {
        Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.SupportBounds(default, Vector3.UnitX));
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.SupportBounds(Duck, new(invalid, 0, 0)));
            Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.SupportBounds(Duck, new(0, invalid, 0)));
            Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.SupportBounds(Duck, new(0, 0, invalid)));
            Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.Clearance(0.2f, 0.4f, 0.45f, 0.5f, invalid, 0));
        }
        Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.Clearance(0.2f, 0.4f, 0.45f, 0.5f, -1, 0));
        Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.Clearance(0.2f, 0.4f, 0.45f, 0.5f, MathF.PI / 2, 0));
        Assert.Throws<ArgumentException>(() => ShortBodyShellOracle.Clearance(0, 0.4f, 0.45f, 0.5f, 0, 0));
    }

    [Fact]
    public void OptionalCapabilityUsesValueShapeByReadonlyReference()
    {
        Type capability = typeof(IPhysicsConvexQueries);
        Assert.True(capability.IsInterface);
        foreach ((string name, Type result, int count) in new[]
        {
            ("SweepConvexCertified", typeof(ConvexSweepResult), 4),
            ("DistanceConvex", typeof(ConvexDistanceResult), 3),
            ("QueryConvexContacts", typeof(ConvexContactResult), 4),
            ("ComputeConvexPenetration", typeof(ConvexPenetrationResult), 3)
        })
        {
            MethodInfo? method = capability.GetMethod(name);
            Assert.NotNull(method);
            Assert.Equal(result, method.ReturnType);
            var parameters = method.GetParameters();
            Assert.Equal(count, parameters.Length);
            Assert.Equal(typeof(ConvexQueryShape).MakeByRefType(), parameters[0].ParameterType);
            Assert.True(parameters[0].IsIn);
            Assert.Equal(typeof(Pose), parameters[1].ParameterType);
            Assert.Equal(typeof(QueryFilter), parameters[^1].ParameterType);
            Assert.True(parameters[^1].IsOptional);
        }
        Assert.Equal(typeof(Vector3), capability.GetMethod("SweepConvexCertified")!.GetParameters()[2].ParameterType);
        Assert.Equal(typeof(Span<ConvexContact>), capability.GetMethod("QueryConvexContacts")!.GetParameters()[2].ParameterType);
    }

    static void Encloses((double Lower, double Upper) bounds, double exact)
    {
        Assert.True(bounds.Lower <= exact && exact <= bounds.Upper,
            $"[{bounds.Lower:R}, {bounds.Upper:R}] does not enclose {exact:R}");
    }
}
