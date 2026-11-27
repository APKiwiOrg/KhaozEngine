using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class ClothSparseFactorTests
{
    [Fact]
    public void A_permuted_factor_matches_the_direct_solution_on_disconnected_blocks_with_fill()
    {
        // Block 1 is isolated and eliminated first. Eliminating block 0 fills the edge between 2 and 4.
        bool[,] graph = new bool[5, 5];
        foreach ((int left, int right) in new[] { (0, 2), (2, 3), (3, 4), (4, 0) })
            graph[left, right] = graph[right, left] = true;
        int[] start = [0, 0, 0, 3, 3, 3, 0, 0, 0, 6, 6, 6, 0, 0, 0];
        var limits = ClothWorkLimits.LayeredContact;
        var direct = new ClothSkylineFactor(start, limits);
        int[] offsets = direct.Offsets;
        var matrix = new double[offsets[^1]];
        var load = new double[start.Length];
        for (int row = 0; row < start.Length; row++)
        {
            load[row] = row - 4.5;
            for (int column = start[row]; column < row; column++)
                if (row / 3 == column / 3 || graph[row / 3, column / 3])
                    matrix[offsets[row] + column - start[row]] = .015 * (row - 2 * column);
            matrix[offsets[row + 1] - 1] = 12 + row;
        }
        double[] reference = [.. load];
        Assert.True(direct.TryFactorAndSolve([.. matrix], reference, out _));
        var sparse = new ClothSparseFactor(graph, start, offsets, limits);
        var solution = new double[start.Length];

        Assert.True(sparse.TryFactorAndSolve(matrix, load, solution, out _));

        for (int degree = 0; degree < reference.Length; degree++)
            Assert.InRange(Math.Abs(solution[degree] - reference[degree]), 0, 1e-12);
    }

    [Fact]
    public void Interspersed_pins_stay_fixed_when_the_free_blocks_are_permuted()
    {
        Vector3[] rest = [new(0, 0, 0), new(.1f, 0, 0), new(1, 0, 0), new(1.1f, 0, 0),
            new(0, .1f, 0), new(1, .1f, 0), new(.1f, .1f, 0)];
        Vector2[] coordinates = Array.ConvertAll(rest, position => new Vector2(position.X, position.Y));
        int[] pinned = [1, 2, 5];
        // Free node 3 is isolated in material space. It precedes the clique at 0, 4 and 6 in the factor.
        ClothRig rig = ClothRig.Create(rest, coordinates, [new(0, 1, 4), new(1, 6, 4), new(2, 3, 5)], pinned, [],
            ClothMaterial.HeavyWoven);
        var limits = new ClothWorkLimits(4, 0, 0, 0);
        var direct = new ClothSimulation(rig, limits);
        var sparse = new ClothSimulation(rig, limits, reuseMaterialFactor: true);
        direct.Reset(rest);
        sparse.Reset(rest);
        Vector3[] pins = [rest[1] + new Vector3(.001f, 0, .002f), rest[2], rest[5] + new Vector3(0, .004f, .001f)];
        var step = new ClothSubstep(pins, [], [], ClothWorkLimits.LayeredStepSeconds, -9.81f * Vector3.UnitZ);

        Assert.Equal(ClothStepStatus.Accepted, direct.Advance(step).Status);
        Assert.Equal(ClothStepStatus.Accepted, sparse.Advance(step).Status);

        for (int i = 0; i < pinned.Length; i++) Assert.Equal(pins[i], sparse.Positions[pinned[i]]);
        foreach (int node in new[] { 0, 3, 4, 6 })
        {
            Assert.NotEqual(rest[node], direct.Positions[node]);
            Assert.InRange(Vector3.Distance(direct.Positions[node], sparse.Positions[node]), 0, 2e-7f);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void A_sparse_pivot_failure_charges_only_updates_already_performed(double pivot)
    {
        var sparse = new ClothSparseFactor(new bool[1, 1], [0, 0, 0], [0, 1, 3, 6], ClothWorkLimits.LayeredContact);

        Assert.False(sparse.TryFactorAndSolve([1, 0, pivot, 0, 0, 1], [1, 1, 1], new double[3],
            out ClothMaterialSolveWork work));

        Assert.Equal(1, work.Factorizations);
        Assert.Equal(3, work.FactorProducts);
        Assert.Equal(0, work.SubstitutionProducts);
    }

    [Fact]
    public void An_empty_factor_is_admitted_at_zero_work()
    {
        var limits = ClothWorkLimits.LayeredContact with { MaxMaterialFactorProducts = 0 };
        var sparse = new ClothSparseFactor(new bool[0, 0], [], [0], limits);

        Assert.True(sparse.TryFactorAndSolve([], [], [], out ClothMaterialSolveWork work));

        Assert.Equal(0, work.FactorProducts);
        Assert.Equal(0, work.SubstitutionProducts);
    }

    [Fact]
    public void Frozen_garment_reserves_and_reports_one_exact_sparse_factor()
    {
        const long factorProducts = 225_234;
        const long substitutionProducts = 22_032;
        ClothRig rig = new ClothDrapeFixture().Rig;
        var limits = ClothWorkLimits.LayeredContact;
        var system = new ClothMaterialSystem(rig, limits, reuseMaterialFactor: true);
        system.Begin(0);

        Assert.Equal(ClothStepStatus.Accepted, system.Solve(out ClothMaterialSolveWork work));

        Assert.Equal(1, work.Factorizations);
        Assert.Equal(factorProducts, work.FactorProducts);
        Assert.Equal(substitutionProducts, work.SubstitutionProducts);
        var exact = limits with { MaxMaterialFactorProducts = factorProducts };
        _ = new ClothMaterialSystem(rig, exact, reuseMaterialFactor: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClothMaterialSystem(rig,
            exact with { MaxMaterialFactorProducts = factorProducts - 1 }, reuseMaterialFactor: true));
    }
}
