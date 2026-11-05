using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Fixtures;

public sealed class GroundPursuitFixture : IDisposable
{
    internal static MoveTuning Tuning => ContactNavigationFixture.Tuning;
    private PursuitScene? _flat, _detour, _cliff;
    internal PursuitScene Flat => _flat ??= new(false, false);
    internal PursuitScene Detour => _detour ??= new(true, true);
    internal PursuitScene Cliff => _cliff ??= new(true, false);

    public void Dispose() { _flat?.Dispose(); _detour?.Dispose(); _cliff?.Dispose(); }

    internal sealed class PursuitScene : IDisposable
    {
        private readonly OrdinaryContactScene _scene;
        internal GroundMoveContext Context { get; }
        internal GroundNavigation Navigation { get; }

        internal PursuitScene(bool cliff, bool stairs, NavAreaClassifier? classify = null, NavAreaFilter areas = default,
            float cliffHeight = 0.9f)
        {
            _scene = new(Tuning, (_, _) => 0,
                extent: new(new(-10, -2, -10), new(10, 6, 10), 0, 2),
                install: (world, builder) =>
                {
                    ContactNavigationFixture.Box(world, builder, "floor", new(-8, -1, -8), new(8, 0, 8));
                    if (!cliff) return;
                    ContactNavigationFixture.Box(world, builder, "ledge", new(-8, 0, -8), new(0, cliffHeight, 8));
                    if (!stairs) return;
                    ContactNavigationFixture.Box(world, builder, "upper-step", new(0, 0, 2), new(1, 0.6f, 8));
                    ContactNavigationFixture.Box(world, builder, "lower-step", new(1, 0, 2), new(2, 0.3f, 8));
                });
            Context = ContactNavigationFixture.Context(_scene);
            var options = new PhysicsNavBakeOptions(-2, -2, 4, 4, 0.5f, 6, 10,
                Tuning.MaxSlopeRadians, 256, 1024);
            using var capture = PhysicsNavBake.Capture(Context, options, classify ?? (_ => 0));
            Navigation = capture.BuildProfile(Tuning, areas,
                new GroundProfileOptions { MaxDropMetres = cliff ? 2 : 0 });
        }

        internal MoveState Body(float x = -1.25f, float z = 0.25f, float surface = 0)
        {
            float feet = ContactNavigationFixture.Resting(Navigation, new(x, surface, z))
                ?? throw new InvalidOperationException("Fixture point must have a captured surface.");
            return new() { Position = new(x, feet + Tuning.CapsuleHalfHeight, z), Grounded = true };
        }

        internal ReachTarget Target(float x = 1.75f, float z = 0.25f, float surface = 0)
            => ReachTarget.Point(Body(x, z, surface).Position);

        public void Dispose() { Context.Dispose(); _scene.Dispose(); }
    }
}
