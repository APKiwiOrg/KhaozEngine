using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement.Research;

[Collection("NavCaptureResearch")]
public sealed class NavCaptureSnapshotTests
{
    // A parser that drops a float bit, an area or water entry must fail this independent byte fixture.
    [Fact]
    public void RestoredSnapshotRetainsEveryBitWithoutSamplingPhysics()
    {
        string path = Path.GetTempFileName();
        try
        {
            var options = new PhysicsNavBakeOptions(-1f, -1f, 1f, 1f, 1f, 2f, 3f, 0.8f, 4, 8)
            { SampleWater = true };
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("PCAP1");
                writer.Write(2);
                writer.Write(2);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                for (int i = 0; i < 4; i++)
                {
                    writer.Write(1);
                    writer.Write(BitConverter.UInt32BitsToSingle(0x80000000));
                    writer.Write(float.PositiveInfinity);
                    writer.Write(0x80000007u);
                }
                writer.Write(1);
                writer.Write(2);
                writer.Write(BitConverter.UInt32BitsToSingle(0x3f800001));
                writer.Write(0xffffffffu);
            }
            using var world = new KhaozEngine.Physics.Bepu.BepuPhysicsWorld();
            using var context = new GroundMoveContext((_, _) => throw new InvalidOperationException(), physics: world,
                medium: (_, _, _) => throw new InvalidOperationException());
            using PhysicsNavBake restored = NavCaptureSnapshot.Restore(new(world, context, options,
                _ => throw new InvalidOperationException()), path);
            Assert.Equal(2, restored.Columns.Width);
            Assert.Equal(2, restored.Columns.Height);
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 2; x++)
                {
                    PhysicsNavSurface surface = Assert.Single(restored.Columns.GetColumn(x, z).ToArray());
                    Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(surface.Height));
                    Assert.Equal(0x7f800000u, BitConverter.SingleToUInt32Bits(surface.Headroom));
                    Assert.Equal(0x80000007u, surface.Areas);
                }
            PhysicsNavWater water = Assert.Single(restored.Columns.Water.ToArray());
            Assert.Equal(2, water.Cell);
            Assert.Equal(0x3f800001u, BitConverter.SingleToUInt32Bits(water.SurfaceY));
            Assert.Equal(0xffffffffu, water.Areas);
            Assert.NotNull(restored.Basis);
            restored.Basis.Value.AssertCurrent();
            string roundTrip = path + ".roundtrip";
            try
            {
                NavCaptureSnapshot.Write(restored, roundTrip);
                Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(roundTrip));
            }
            finally
            {
                File.Delete(roundTrip);
                File.Delete(Path.ChangeExtension(roundTrip, "sha256"));
            }
        }
        finally { File.Delete(path); }
    }

    // Restoring against the same static scene must preserve complete shipped bake bytes, not only columns.
    [Fact]
    public void RestoredFixtureBuildsTheSameCanonicalBakeAsFreshCapture()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var world = new BepuPhysicsWorld();
            world.AddStatic(new BoxShape(new Vector3(3f, 0.1f, 3f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
            using var context = new GroundMoveContext((_, _) => 0f, physics: world);
            var options = new PhysicsNavBakeOptions(-1f, -1f, 1f, 1f, 1f, 2f, 3f, 0.8f, 4, 4);
            NavAreaClassifier classifier = _ => 7u;
            using PhysicsNavBake fresh = PhysicsNavBake.Capture(context, options, classifier);
            NavCaptureSnapshot.Write(fresh, path);
            using PhysicsNavBake restored = NavCaptureSnapshot.Restore(new(world, context, options, classifier), path);
            var tuning = new MoveTuning(1f, 1f, 0.5f, 0.8f, CapsuleRadius: 0.2f);
            var definition = new NavBakeProfile("fixture", tuning, new NavAreaFilter(7u, 0u));
            var sources = new NavBakeSources().AddHashOf("fixture", "flat"u8);
            GroundNavigationBake expected = GroundNavigationBake.Create(fresh, sources, [definition]);
            GroundNavigationBake actual = GroundNavigationBake.Create(restored, sources, [definition]);
            using var expectedBytes = new MemoryStream();
            using var actualBytes = new MemoryStream();
            expected.WriteTo(expectedBytes);
            actual.WriteTo(actualBytes);
            Assert.Equal(expectedBytes.ToArray(), actualBytes.ToArray());
            Assert.True(actual.GetProfile("fixture").Space.Layers[0].IsPassable(0, 0, 0f));

            File.AppendAllText(path, "x");
            Assert.Throws<InvalidDataException>(() => NavCaptureSnapshot.Restore(new(world, context, options, classifier), path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, "sha256"));
        }
    }
}
