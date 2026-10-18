using System;
using System.Collections.Generic;
using System.Numerics;
using BepuUtilities;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedStoredBoundsResidualTests
{
    public static IEnumerable<object[]> Routes()
    {
        foreach (bool view in new[] { false, true })
            foreach (bool dimensions in new[] { false, true })
                yield return [view, dimensions];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void OriginalIdentityAperturePrunes4097SeparatedBoxes(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        for (int i = 0; i < 4097; i++)
            world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0.625f, 0, 0)));
        StaticHandle[]? exclusions = view
            ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)))] : null;
        CapsuleSweepResult result = Read(world, Vector3.Zero, new(0, 0, 0.125f),
            length: 0, useDimensions: dimensions, exclusions: exclusions);
        SameBits(new(CapsuleSweepStatus.Clear, 0.125f, null, 0), result);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void AdmittedOperationalContactCanMissEntireStoredCoordinateDomain(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        float w = MathF.BitDecrement(MathF.BitDecrement(MathF.BitDecrement(0.8660254f)));
        var rotation = new Quaternion(0, 0, 0.5f, w);
        var handle = world.AddStatic(new BoxShape(new(0.25f)), new(Vector3.Zero, rotation));
        var geometry = CertifiedCompoundSweepTests.Geometry(world, handle);
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(world).Shapes, Static(world, handle).Shape,
            new(Vector3.Zero, rotation), geometry, 0, out var leaf));
        Assert.True(CapsuleSweepPruning.Validate(leaf.Map, rotation));
        Assert.Empty((HashSet<int>)Field(world, "_unprovedSweepStatics"));

        const float radius = 281474976710656f;
        Vector3 centre = new(MathF.BitIncrement(radius), 0, 0);
        Assert.Equal(33554432f, centre.X - radius);
        Assert.True(CapsuleSweepAperture.TryCreate(centre, radius, 0, Vector3.Zero,
            out Vector3 min, out _));
        Assert.True(min.X > CapsuleGeneralSweepPolicy.MaximumStoredCoordinate);

        // Evaluate the pinned transpose graph directly. Its centre lies inside the
        // query sphere about the box origin, proving an operational initial contact.
        var q = new QuaternionWide
        {
            X = new(rotation.X),
            Y = new(rotation.Y),
            Z = new(rotation.Z),
            W = new(rotation.W)
        };
        QuaternionWide.Conjugate(q, out var inverse);
        Vector3Wide.Broadcast(centre, out var offset);
        QuaternionWide.TransformWithoutOverlap(offset, inverse, out var local);
        double x = local.X[0], y = local.Y[0], z = local.Z[0];
        Assert.True(x * x + y * y + z * z < (double)radius * radius);

        // The general domain refuses before a stored enclosure can prune this contact.
        // No stored bound admitted by GS-3 can intersect the original aperture.
        Refused(Read(world, centre, Vector3.Zero, radius: radius, length: 0,
            useDimensions: dimensions, exclusions: view
                ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)))] : null));
    }
}
