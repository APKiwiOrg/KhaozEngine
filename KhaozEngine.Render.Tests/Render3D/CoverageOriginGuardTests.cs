using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageOriginGuardTests
{
    [Fact]
    public void Origin_error_equal_to_margin_is_admitted()
    {
        Assert.True(CoverageActorBounds.Allows(.015625d, .0625f));
        Assert.False(CoverageActorBounds.Allows(Math.BitIncrement(.015625d), .0625f));
        Assert.False(CoverageActorBounds.Allows(double.NaN, 1));
        Assert.False(CoverageActorBounds.Allows(double.PositiveInfinity, float.PositiveInfinity));
    }

    [Fact]
    public void Palette_escape_and_rebasing_have_conservative_bounds()
    {
        var (producer, command) = Setup();
        var skin = producer.BoundSkin([Matrix4x4.Identity, Matrix4x4.Identity]);
        command = command with { Clearance = .0625f, MaxWorldRecession = 1 };
        var nearAccepted = producer.TryAdmitOrigin(skin, Matrix4x4.Identity, Vector3.Zero, [command], out _);
        var far = Matrix4x4.CreateTranslation(1048576, -1048576, 1048576);
        var farUnrebasedAccepted = producer.TryAdmitOrigin(skin, far, Vector3.Zero, [command], out var originRefusal);
        var farRebasedAccepted = producer.TryAdmitOrigin(skin, far, far.Translation, [command], out _);
        Assert.True(nearAccepted);
        Assert.False(farUnrebasedAccepted);
        Assert.True(farRebasedAccepted);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, originRefusal.Code);
        Assert.Equal(command.Id, originRefusal.CommandId);
        Assert.Equal(command.EquipmentInstanceId, originRefusal.EquipmentInstanceId);
        var escaped = producer.BoundSkin([Matrix4x4.CreateTranslation(100, 0, 0), Matrix4x4.CreateTranslation(100, 0, 0)]);
        Assert.True(escaped.Max.X >= 100);
        Assert.True(producer.OriginError(escaped, Matrix4x4.Identity, Vector3.Zero, [command])
            > producer.OriginError(skin, Matrix4x4.Identity, Vector3.Zero, [command]));
    }

    [Fact]
    public void Every_command_margin_is_checked_in_sorted_order()
    {
        var (producer, command) = Setup();
        var skin = producer.BoundSkin([Matrix4x4.Identity, Matrix4x4.Identity]);
        var model = Matrix4x4.CreateTranslation(1024, 1024, 1024);
        var loose = command with { Id = 20, Clearance = 1 };
        var tight = command with { Id = 10, EquipmentInstanceId = 987, Clearance = 1e-6f };
        Assert.True(producer.TryAdmitOrigin(skin, model, Vector3.Zero, [loose], out _));
        Assert.False(producer.TryAdmitOrigin(skin, model, Vector3.Zero, [loose, tight], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(10u, failure.CommandId);
        Assert.Equal(987ul, failure.EquipmentInstanceId);
        Assert.False(producer.TryAdmitOrigin(skin, model, Vector3.Zero, [loose with { Clearance = 1e-7f }, tight], out failure));
        Assert.Equal(10u, failure.CommandId);
    }

    [Fact]
    public void Model_shear_inverse_recession_and_origin_subtraction_residual_are_enclosed()
    {
        var (producer, command) = Setup();
        var skin = producer.BoundSkin([Matrix4x4.Identity, Matrix4x4.Identity]);
        Matrix4x4 model = new(.001f, 20, .3f, 0, 0, 1, .2f, 0, 0, 0, 2, 0, 1048576, 0, 0, 1);
        double noRecession = producer.OriginError(skin, model, model.Translation, [command with { MaxWorldRecession = 0 }]);
        double withRecession = producer.OriginError(skin, model, model.Translation, [command with { MaxWorldRecession = 3 }]);
        Assert.True(withRecession > noRecession + .01);
        double residual = producer.OriginError(skin, Matrix4x4.CreateTranslation(1048576, 0, 0), new(.01f, 0, 0), [command]);
        Assert.True(residual >= Math.Abs((double)1048576 - .01f - (float)(1048576d - .01f)));
        Assert.False(producer.TryAdmitOrigin(skin, model with { M11 = 0 }, Vector3.Zero, [command], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
    }

    [Fact]
    public void Origin_bound_encloses_independent_rounded_model_dots_over_the_recession_domain()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0]
            with
        { MaxWorldRecession = 3 };
        var producer = new CoverageActorBounds(fixture.Source);
        Matrix4x4[] palette = [Matrix4x4.CreateTranslation(100, 0, 0), Matrix4x4.CreateTranslation(100, 0, 0)];
        var skin = producer.BoundSkin(palette);
        Matrix4x4 model = new(.5f, 2, 0, 0, 0, 1, 0, 0, 0, 0, 2, 0, 1048576, -1048576, 1048576, 1);
        Assert.True(Matrix4x4.Invert(model, out var inverse));
        foreach (var origin in new[] { Vector3.Zero, model.Translation, model.Translation + new Vector3(.25f) })
        {
            double bound = producer.OriginError(skin, model, origin, [command]);
            var relative = model with
            {
                M41 = (float)((double)model.M41 - origin.X),
                M42 = (float)((double)model.M42 - origin.Y),
                M43 = (float)((double)model.M43 - origin.Z)
            };
            foreach (var v in fixture.Source.SkinVertices)
                foreach (var displacement in new[] { Vector3.Zero, Vector3.UnitX * 3, -Vector3.UnitY * 3, Vector3.UnitZ * 3 })
                {
                    // This triangular power-of-two inverse is exact. These are boundary points of the
                    // local domain obtained from a world-length recession, including shear amplification.
                    Vector3 p = CoverageActorBoundsTests.Skin(v, palette) + Vector3.TransformNormal(displacement, inverse);
                    Vector3 drawn = CoverageActorBoundsTests.DrawPoint(p, relative);
                    double x = drawn.X - (p.X * (double)model.M11 + p.Y * (double)model.M21 + p.Z * (double)model.M31 + model.M41 - origin.X);
                    double y = drawn.Y - (p.X * (double)model.M12 + p.Y * (double)model.M22 + p.Z * (double)model.M32 + model.M42 - origin.Y);
                    double z = drawn.Z - (p.X * (double)model.M13 + p.Y * (double)model.M23 + p.Z * (double)model.M33 + model.M43 - origin.Z);
                    Assert.True(Math.Sqrt(x * x + y * y + z * z) <= bound);
                }
        }
    }

    static (CoverageActorBounds Producer, CoverageCommandPose Command) Setup()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        return (new(fixture.Source), CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0]);
    }
}
