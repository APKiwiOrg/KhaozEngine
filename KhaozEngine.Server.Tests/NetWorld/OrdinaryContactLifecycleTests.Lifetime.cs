using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CellRuntimeSharesOneBindingAndReleasesItsOwnedViewBeforePhysics(bool teardown)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            ObservedPhysicsWorld? source = null;
            using var server = new ShardedWorldServer(st, new()
            {
                FrameAnchoring = false,
                PhysicsWorldFactory = _ => source = new ObservedPhysicsWorld(),
                MovementRegistrationFactory = cell => Register(cell.Physics!),
                MovementBoundaryFactory = _ => MovementBoundary.Rectangle(-20, -20, 20, 20)
            }, (_, _) => 0, OrdinaryPlayerWorld.Tuning);
            var coord = new CellCoord(0, 0);
            var cell = server.Host.EnsureCell(coord);
            var runtime = Runtime(server, coord);
            var binding = (EffectiveMovementBinding)Property(runtime, "Binding");
            Assert.Same(binding, ((PlayerMovementSystem)Property(runtime, "Movement")).Binding);
            Assert.Same(binding, ((PlayerMoveSimulator)Property(runtime, "Clamp")).Binding);
            Assert.Same(binding, cell.ImportAdmission!.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(EffectiveMovementBinding)).GetValue(cell.ImportAdmission));
            Assert.Equal(1, source!.ViewCreations);
            if (teardown) server.Dispose();
            else Assert.True(server.Host.RemoveCell(coord));
            Assert.Equal(new[] { "movement-view", "physics" }, source.DisposalOrder);
            Assert.Equal(1, source.ViewDisposals);
            if (!teardown)
            {
                server.Host.EnsureCell(coord);
                Assert.NotSame(binding, Property(Runtime(server, coord), "Binding"));
            }
        }
    }

    [Fact]
    public void SingleHeadsAndStandaloneAdaptersDisposeOwnedViewsOnlyOnce()
    {
        using var source = new ObservedPhysicsWorld();
        var registration = Register(source);
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        var simulator = new PlayerMoveSimulator((_, _) => 0, OrdinaryPlayerWorld.Tuning, null, null,
            source, null, SamplerSpace.World, null, null, registration, boundary);
        simulator.Dispose();
        simulator.Dispose();
        var system = new PlayerMovementSystem((_, _) => 0, OrdinaryPlayerWorld.Tuning, null, null,
            source, null, default, SamplerSpace.World, null, null, registration, boundary);
        system.Dispose();
        system.Dispose();
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            var server = new WorldServer(st, new()
            { FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary },
                (_, _) => 0, OrdinaryPlayerWorld.Tuning, physics: source);
            server.Dispose();
            server.Dispose();
            var client = new WorldClient(ct, (_, _) => 0, OrdinaryPlayerWorld.Tuning, new()
            { FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary }, physics: source);
            client.Dispose();
            client.Dispose();
        }
        Assert.Equal(4, source.ViewDisposals);
        Assert.Equal(0, source.SourceDisposals);
        using var stillLive = source.AcquireQueryReadLease();
        stillLive.AssertCurrent();
    }

    internal static OrdinaryMovementRegistration Register(IPhysicsWorld physics)
    {
        using var read = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        return OrdinaryMovementRegistration.CreateBuilder("world", physics,
            new(new(-20, -8, -20), new(20, 8, 20), 0, 0)).Build(read);
    }

    static object Runtime(ShardedWorldServer server, CellCoord coord) =>
        ((IDictionary)typeof(ShardedWorldServer).GetField("cellRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!)[coord]!;
    static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
}
