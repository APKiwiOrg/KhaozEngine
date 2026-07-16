using System;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class EmissionShapeTests
{
    private static EmitterConfig ShapeCfg(EmissionShape shape, float radius, float shell, Vector3 dir) => new()
    {
        LifetimeMin = 1f,
        LifetimeMax = 1f,
        SpeedMin = 5f,
        SpeedMax = 5f,
        Direction = dir,
        SpreadDegrees = 0f,
        StartSize = 1f,
        EndSize = 1f,
        StartColor = Color.White,
        EndColor = Color.White,
        Shape = shape,
        ShapeRadius = radius,
        ShapeShell = shell,
    };

    [Fact]
    public void Sphere_Volume_KeepsOffsetsWithinRadius()
    {
        var origin = new Vector3(10f, -3f, 2f);
        var sys = new ParticleSystem(256, seed: 5);
        sys.Emit(ShapeCfg(EmissionShape.Sphere, radius: 2.5f, shell: 0f, Vector3.Zero), origin, 200);

        foreach (var p in sys.Active)
        {
            float dist = (p.Position - origin).Length();
            Assert.True(dist <= 2.5f + 1e-4f, $"offset {dist} exceeds radius");
        }
    }

    [Fact]
    public void Sphere_Shell_SpawnsOnTheSurface()
    {
        var origin = new Vector3(1f, 1f, 1f);
        var sys = new ParticleSystem(256, seed: 11);
        sys.Emit(ShapeCfg(EmissionShape.Sphere, radius: 3f, shell: 1f, Vector3.Zero), origin, 200);

        foreach (var p in sys.Active)
        {
            float dist = (p.Position - origin).Length();
            Assert.Equal(3f, dist, 3);
        }
    }

    [Fact]
    public void Sphere_Volume_UsesTheInteriorNotJustTheSurface()
    {
        var origin = Vector3.Zero;
        var sys = new ParticleSystem(256, seed: 3);
        sys.Emit(ShapeCfg(EmissionShape.Sphere, radius: 4f, shell: 0f, Vector3.Zero), origin, 200);

        bool anyInterior = false;
        foreach (var p in sys.Active)
        {
            if (p.Position.Length() < 3.5f)
            {
                anyInterior = true;
                break;
            }
        }

        Assert.True(anyInterior, "volume fill should place some particles well inside the surface");
    }

    [Fact]
    public void Hemisphere_FoldsToTheDirectionHalfSpace()
    {
        var origin = Vector3.Zero;
        var axis = Vector3.UnitY;
        var sys = new ParticleSystem(256, seed: 7);
        sys.Emit(ShapeCfg(EmissionShape.Hemisphere, radius: 2f, shell: 0f, axis), origin, 200);

        foreach (var p in sys.Active)
        {
            float d = Vector3.Dot(p.Position - origin, axis);
            Assert.True(d >= -1e-4f, $"particle fell into the wrong half: dot {d}");
        }
    }

    [Fact]
    public void Hemisphere_FoldsAlongAnArbitraryAxis()
    {
        var origin = Vector3.Zero;
        var axis = Vector3.Normalize(new Vector3(1f, 2f, -0.5f));
        var sys = new ParticleSystem(256, seed: 21);
        sys.Emit(ShapeCfg(EmissionShape.Hemisphere, radius: 2f, shell: 0f, axis), origin, 200);

        foreach (var p in sys.Active)
        {
            float d = Vector3.Dot(p.Position - origin, axis);
            Assert.True(d >= -1e-4f, $"particle fell into the wrong half: dot {d}");
        }
    }

    [Fact]
    public void Disc_IsPlanarPerpendicularToTheAxis()
    {
        var origin = new Vector3(0f, 5f, 0f);
        var axis = Vector3.UnitY;
        var sys = new ParticleSystem(256, seed: 13);
        sys.Emit(ShapeCfg(EmissionShape.Disc, radius: 4f, shell: 0f, axis), origin, 200);

        foreach (var p in sys.Active)
        {
            Vector3 offset = p.Position - origin;
            // Perpendicular to +Y: the axial component is ~zero, and the radius stays within bounds.
            Assert.True(MathF.Abs(offset.Y) < 1e-4f, $"disc not planar: y {offset.Y}");
            Assert.True(offset.Length() <= 4f + 1e-4f);
        }
    }

    [Fact]
    public void Disc_Ring_SpawnsOnTheEdge()
    {
        var origin = Vector3.Zero;
        var axis = Vector3.UnitZ;
        var sys = new ParticleSystem(256, seed: 99);
        sys.Emit(ShapeCfg(EmissionShape.Disc, radius: 2f, shell: 1f, axis), origin, 200);

        foreach (var p in sys.Active)
        {
            Assert.Equal(2f, p.Position.Length(), 3);
            Assert.True(MathF.Abs(p.Position.Z) < 1e-4f);
        }
    }

    [Fact]
    public void RadialVelocity_PointsOutwardThroughTheSpawnPoint()
    {
        var origin = new Vector3(-2f, 4f, 1f);
        var cfg = ShapeCfg(EmissionShape.Sphere, radius: 3f, shell: 1f, Vector3.Zero);
        cfg.VelocityMode = ParticleVelocityMode.Radial;

        var sys = new ParticleSystem(256, seed: 17);
        sys.Emit(cfg, origin, 200);

        foreach (var p in sys.Active)
        {
            Vector3 outward = Vector3.Normalize(p.Position - origin);
            Vector3 velDir = Vector3.Normalize(p.Velocity);
            Assert.True((velDir - outward).Length() < 1e-4f, $"velocity {velDir} not aligned with outward {outward}");
            Assert.Equal(5f, p.Velocity.Length(), 3); // speed fixed at 5
        }
    }

    [Fact]
    public void Point_Shape_StillSpawnsAtOrigin()
    {
        var origin = new Vector3(3f, 3f, 3f);
        var sys = new ParticleSystem(32, seed: 1);
        sys.Emit(ShapeCfg(EmissionShape.Point, radius: 5f, shell: 0f, Vector3.UnitX), origin, 8);

        foreach (var p in sys.Active)
        {
            Assert.Equal(origin, p.Position);
        }
    }
}
