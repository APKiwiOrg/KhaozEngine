using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement.Research;

[CollectionDefinition("NavCaptureResearch", DisableParallelization = true)]
public sealed class NavCaptureResearchCollection;

[Collection("NavCaptureResearch")]
public sealed class NavCaptureResearchTests
{
    // Missing columns, changed area bits, a repeated setup or a snapshot from the wrong stage must fail this smoke.
    [Fact]
    public void OneCapturePreservesFixtureColumnsAndSeparatesStages()
    {
        string output = Path.Combine(Path.GetTempPath(), "ke-nav-capture-" + Guid.NewGuid().ToString("N"));
        int setups = 0, samples = 0;
        try
        {
            var stages = NavCaptureResearch.Run(() =>
            {
                setups++;
                var world = new BepuPhysicsWorld();
                world.AddStatic(new BoxShape(new Vector3(2f, 0.1f, 2f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
                return new NavCaptureResearchInput(world, new GroundMoveContext((_, _) => 0f, physics: world),
                    new PhysicsNavBakeOptions(-1f, -1f, 1f, 1f, 1f, 2f, 3f, 0.8f, 4, 4),
                    _ => { samples++; return 7u; });
            }, output);

            Assert.Equal(1, setups);
            Assert.Equal(4, samples);
            Assert.Collection(stages, s => Assert.Equal("setup", s.Name), s => Assert.Equal("capture", s.Name),
                s => Assert.Equal("snapshot", s.Name));
            Assert.All(stages, s =>
            {
                Assert.True(s.WallSeconds >= 0);
                Assert.True(s.CpuSeconds >= 0);
                Assert.True(s.ProcessAllocatedBytes >= 0);
                Assert.True(s.ThreadAllocatedBytes >= 0);
            });
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(output, "columns.bin")));
            Assert.Equal("PCAP1", reader.ReadString());
            Assert.Equal(2, reader.ReadInt32());
            Assert.Equal(2, reader.ReadInt32());
            Assert.Equal(0f, reader.ReadSingle());
            Assert.Equal(0f, reader.ReadSingle());
            Assert.Equal(0f, reader.ReadSingle());
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(1, reader.ReadInt32());
                Assert.Equal(0f, reader.ReadSingle(), 5);
                Assert.Equal(float.PositiveInfinity, reader.ReadSingle());
                Assert.Equal(7u, reader.ReadUInt32());
            }
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
            Assert.True(File.Exists(Path.Combine(output, "columns.sha256")));
            Assert.True(File.Exists(Path.Combine(output, "metrics.json")));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
