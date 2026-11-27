using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class ClothMaterialReuseTests
{
    private static readonly ClothWorkLimits Limits = new(4, 1, 16, 128);
    private static readonly ClothMaterial Material = ClothMaterial.HeavyWoven with { NormalAirDrag = 0 };

    [Fact]
    public void Moving_pins_relinearize_each_projection_with_one_factor_per_tick()
    {
        Vector3[] rest = [Vector3.Zero, .1f * Vector3.UnitX, .1f * Vector3.UnitY];
        ClothRig rig = Triangle(rest, [0, 1]);
        var direct = new ClothSimulation(rig, Limits);
        // The one free node has three scalar degrees of freedom with 0, 1 and 3 Cholesky products by row.
        const long factorProducts = 4;
        var reuseLimits = Limits with { MaxMaterialFactorProducts = factorProducts };
        var reused = new ClothSimulation(rig, reuseLimits, reuseMaterialFactor: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClothSimulation(rig, reuseLimits));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClothSimulation(rig,
            reuseLimits with { MaxMaterialFactorProducts = factorProducts - 1 }, reuseMaterialFactor: true));
        direct.Reset(rest);
        reused.Reset(rest);
        Vector3[] pins = [rest[0], rest[1] + new Vector3(.007f, .002f, .012f)];
        var step = new ClothSubstep(pins, [], [], ClothWorkLimits.LayeredStepSeconds, -9.81f * Vector3.UnitZ);

        for (int tick = 0; tick < 2; tick++)
        {
            ClothStepResult reference = direct.Advance(step);
            ClothStepResult prototype = reused.Advance(step);

            Assert.Equal(ClothStepStatus.Accepted, reference.Status);
            Assert.Equal(ClothStepStatus.Accepted, prototype.Status);
            Assert.Equal(Limits.Iterations * factorProducts, reference.MaterialFactorProducts);
            Assert.Equal(factorProducts, prototype.MaterialFactorProducts);
            ClothMaterialSolveWork referenceWork = direct.LastAdvanceMaterialWork;
            ClothMaterialSolveWork reuseWork = reused.LastAdvanceMaterialWork;
            Assert.Equal(Limits.Iterations, referenceWork.Factorizations);
            Assert.Equal(6L * Limits.Iterations, referenceWork.SubstitutionProducts);
            Assert.Equal(1, reuseWork.Factorizations);
            Assert.InRange(reuseWork.CorrectionIterations, 1, (Limits.Iterations - 1) * ClothMaterialReuseSolver.CorrectionLimit);
            Assert.True(reuseWork.ResidualChecks >= Limits.Iterations);
            Assert.InRange(Vector3.Distance(direct.Positions[2], reused.Positions[2]), 0, 2e-7f);
        }
    }

    [Fact]
    public void A_reused_factor_solves_and_certifies_the_changed_matrix()
    {
        int[] start = [0, 0, 0];
        var factor = new ClothSkylineFactor(start, Limits);
        var solver = new ClothMaterialReuseSolver(factor, new ClothMaterialOperator(new[,] { { true } }, start, factor.Offsets), 3);
        double[] first = [4, .25, 3, .5, -.5, 2];
        double[] changed = [5, -1, 4, .25, 1.5, 2];
        double[] load = [2, -1, 3];
        Assert.Equal(ClothStepStatus.Accepted, solver.Solve(first, load, out _));
        Assert.True(CurrentResidual(solver.Solution) > 1);
        double[] reference = [.. load];
        Assert.True(factor.TryFactorAndSolve([.. changed], reference, out _));

        Assert.Equal(ClothStepStatus.Accepted, solver.Solve(changed, load, out ClothMaterialSolveWork work));

        for (int index = 0; index < reference.Length; index++)
            Assert.InRange(Math.Abs(reference[index] - solver.Solution[index]), 0, 2e-6);
        double bound = 1e-12 + 1e-6 * Math.Sqrt(14);
        double residual = CurrentResidual(solver.Solution);
        Assert.InRange(residual, 0, bound);
        Assert.InRange(Math.Abs(residual / bound - work.MaxAcceptedResidualRatio), 0, 1e-9);
        Assert.Equal(0, work.Factorizations);
        Assert.True(work.ResidualChecks > 0);
        Assert.Equal(9L * (work.CorrectionIterations + work.ResidualChecks), work.OperatorProducts);
        Assert.Equal(6L * work.CorrectionIterations, work.SubstitutionProducts);

        static double CurrentResidual(double[] displacement)
        {
            double x = displacement[0], y = displacement[1], z = displacement[2];
            double rx = 2 - (5 * x - y + .25 * z);
            double ry = -1 - (-x + 4 * y + 1.5 * z);
            double rz = 3 - (.25 * x + 1.5 * y + 2 * z);
            return Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }
    }

    [Fact]
    public void An_unconverged_finite_solve_charges_its_attempts_and_refuses_at_the_cap()
    {
        const int size = 96;
        var start = new int[size];
        var first = new double[size];
        var changed = new double[size];
        var load = new double[size];
        for (int row = 0; row < size; row++)
        {
            start[row] = row;
            first[row] = load[row] = 1;
            changed[row] = row + 1;
        }
        var factor = new ClothSkylineFactor(start, Limits);
        var solver = new ClothMaterialReuseSolver(factor,
            new ClothMaterialOperator(new bool[size / 3, size / 3], start, factor.Offsets), size);
        Assert.Equal(ClothStepStatus.Accepted, solver.Solve(first, load, out _));

        Assert.Equal(ClothStepStatus.MaterialSolveWork, solver.Solve(changed, load, out ClothMaterialSolveWork work));

        Assert.Equal(ClothMaterialReuseSolver.CorrectionLimit, work.CorrectionIterations);
        Assert.Equal(1, work.ResidualChecks);
        Assert.Equal(0, work.Factorizations);
        Assert.Equal(0, work.MaxAcceptedResidualRatio);
        Assert.Equal((long)size * (work.CorrectionIterations + work.ResidualChecks), work.OperatorProducts);
        Assert.InRange(work.VectorProducts, 1, (1 + 7L * ClothMaterialReuseSolver.CorrectionLimit) * size);
    }

    [Theory]
    [InlineData(4, ClothStepStatus.Accepted)]
    [InlineData(double.PositiveInfinity, ClothStepStatus.NonfiniteState)]
    public void A_zero_load_requires_a_finite_current_residual(double current, ClothStepStatus expected)
    {
        int[] start = [0, 1, 2];
        var factor = new ClothSkylineFactor(start, Limits);
        var solver = new ClothMaterialReuseSolver(factor, new ClothMaterialOperator(new[,] { { true } }, start, factor.Offsets), 3);
        Assert.Equal(ClothStepStatus.Accepted, solver.Solve([1, 2, 3], [1, 1, 1], out _));

        Assert.Equal(expected, solver.Solve([current, 2, 3], [0, 0, 0], out ClothMaterialSolveWork work));

        Assert.All(solver.Solution, displacement => Assert.Equal(0, displacement));
        Assert.Equal(0, work.CorrectionIterations);
        Assert.Equal(1, work.ResidualChecks);
    }

    [Fact]
    public void A_failed_pivot_charges_completed_factor_products_and_forward_substitution()
    {
        var factor = new ClothSkylineFactor([0, 0, 0, 0], Limits);

        Assert.False(factor.TryFactorAndSolve([1, 0, 1, 0, 0, -1, 0, 0, 0, 1], [1, 1, 1, 1], out ClothMaterialSolveWork work));

        Assert.Equal(1, work.Factorizations);
        // The first three factor rows evaluate 0, 1 and 3 products. Only the first two rows substitute.
        Assert.Equal(4, work.FactorProducts);
        Assert.Equal(1, work.SubstitutionProducts);
    }

    [Fact]
    public void Refused_and_display_attempts_preserve_accepted_history_and_charge_only_their_own_work()
    {
        Vector3[] rest = [Vector3.Zero, .1f * Vector3.UnitX, .1f * Vector3.UnitY];
        ClothRig rig = Triangle(rest, [0, 1]);
        var limits = Limits with { MaxContactChecks = 0 };
        var prototype = new ClothSimulation(rig, limits, reuseMaterialFactor: true);
        var control = new ClothSimulation(rig, limits, reuseMaterialFactor: true);
        prototype.Reset(rest);
        control.Reset(rest);
        Vector3[] pins = [rest[0], rest[1]];
        const float seconds = ClothWorkLimits.LayeredStepSeconds;
        var step = new ClothSubstep(pins, [], [], seconds, -9.81f * Vector3.UnitZ);
        Assert.Equal(ClothStepStatus.Accepted, prototype.Advance(step).Status);
        Assert.Equal(ClothStepStatus.Accepted, control.Advance(step).Status);
        Vector3[] positions = prototype.Positions.ToArray(), velocities = prototype.Velocities.ToArray();
        Assert.NotEqual(Vector3.Zero, velocities[2]);
        ClothSupport floor = ClothSupport.Plane(Vector3.UnitZ, Quaternion.Identity, -Vector3.UnitZ);

        ClothStepResult refused = prototype.Advance(new ClothSubstep(pins, [floor], [floor], seconds, step.Gravity));

        Assert.Equal(ClothStepStatus.ContactWork, refused.Status);
        Assert.True(refused.MaterialFactorProducts > 0);
        ClothMaterialSolveWork attempted = prototype.LastAdvanceMaterialWork;
        Assert.Equal(1, attempted.Factorizations);
        Assert.Equal(1, attempted.ResidualChecks);
        ClothStepResult display = prototype.ProjectDisplay(positions, new Vector3[rest.Length], step);
        Assert.Equal(ClothStepStatus.Accepted, display.Status);
        Assert.Equal(0, display.MaterialFactorProducts);
        Assert.Equal(attempted, prototype.LastAdvanceMaterialWork);
        ClothStepResult early = prototype.Advance(new ClothSubstep(pins, [floor, floor], [floor, floor], seconds, step.Gravity));
        Assert.Equal(ClothStepStatus.SupportCapacity, early.Status);
        Assert.Equal(0, early.MaterialFactorProducts);
        Assert.Equal(default, prototype.LastAdvanceMaterialWork);
        Assert.Equal(positions, prototype.Positions.ToArray());
        Assert.Equal(velocities, prototype.Velocities.ToArray());

        Assert.Equal(ClothStepStatus.Accepted, prototype.Advance(step).Status);
        Assert.Equal(ClothStepStatus.Accepted, control.Advance(step).Status);
        Assert.Equal(control.Positions.ToArray(), prototype.Positions.ToArray());
        Assert.Equal(control.Velocities.ToArray(), prototype.Velocities.ToArray());
    }

    private static ClothRig Triangle(Vector3[] rest, int[] pins) =>
        ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], pins, [], Material);
}
