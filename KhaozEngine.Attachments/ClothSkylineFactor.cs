using System;

namespace KhaozEngine.Attachments;

// The skyline is shared by the current material matrix and its Cholesky factor.
internal sealed class ClothSkylineFactor
{
    private readonly int[] _start;
    private readonly long[] _factorProductsThroughRow;

    internal ClothSkylineFactor(int[] start, ClothWorkLimits limits, bool reuseMaterialFactor = false)
    {
        _start = start;
        Offsets = new int[start.Length + 1];
        _factorProductsThroughRow = new long[start.Length];
        // Divide first so the per-substep limit can be checked without overflowing.
        long productsPerFactor = limits.MaxMaterialFactorProducts / (reuseMaterialFactor ? 1 : limits.Iterations);
        long products = 0;
        for (int row = 0; row < start.Length; row++)
        {
            Offsets[row + 1] = checked(Offsets[row] + row - start[row] + 1);
            for (int col = start[row]; col <= row; col++)
                products += col - Math.Max(start[row], start[col]);
            if (products > productsPerFactor)
                throw new ArgumentOutOfRangeException(nameof(limits), limits,
                    "The cloth material system exceeds its per-substep factor work limit.");
            _factorProductsThroughRow[row] = products;
        }

        FactorProducts = products;
        SubstitutionProducts = 2L * (Offsets[^1] - start.Length);
    }

    internal int[] Offsets { get; }
    internal long FactorProducts { get; }
    internal long SubstitutionProducts { get; }

    internal bool TryFactorAndSolve(double[] matrix, double[] rhs, out ClothMaterialSolveWork work)
    {
        int[] start = _start, offsets = Offsets;
        for (int row = 0; row < rhs.Length; row++)
        {
            int rowStart = start[row];
            int rowBase = offsets[row] - rowStart;
            for (int col = rowStart; col <= row; col++)
            {
                int colBase = offsets[col] - start[col];
                double entry = matrix[rowBase + col];
                for (int k = Math.Max(rowStart, start[col]); k < col; k++)
                    entry -= matrix[rowBase + k] * matrix[colBase + k];

                if (row == col)
                {
                    if (entry <= 0 || !double.IsFinite(entry))
                    {
                        work = new(Factorizations: 1, FactorProducts: _factorProductsThroughRow[row],
                            SubstitutionProducts: offsets[row] - row);
                        return false;
                    }
                    matrix[rowBase + row] = Math.Sqrt(entry);
                }
                else
                    matrix[rowBase + col] = entry / matrix[colBase + col];
            }

            ForwardRow(matrix, rhs, row, rowStart, rowBase);
        }

        Backward(matrix, rhs);
        work = new(Factorizations: 1, FactorProducts: FactorProducts, SubstitutionProducts: SubstitutionProducts);
        return true;
    }

    internal void Substitute(double[] factor, double[] rhs)
    {
        for (int row = 0; row < rhs.Length; row++)
            ForwardRow(factor, rhs, row, _start[row], Offsets[row] - _start[row]);
        Backward(factor, rhs);
    }

    private static void ForwardRow(double[] matrix, double[] rhs, int row, int rowStart, int rowBase)
    {
        double load = rhs[row];
        for (int col = rowStart; col < row; col++)
            load -= rhs[col] * matrix[rowBase + col];
        rhs[row] = load / matrix[rowBase + row];
    }

    private void Backward(double[] matrix, double[] rhs)
    {
        for (int row = rhs.Length - 1; row >= 0; row--)
        {
            int rowStart = _start[row];
            int rowBase = Offsets[row] - rowStart;
            double displacement = rhs[row] /= matrix[rowBase + row];
            for (int col = rowStart; col < row; col++)
                rhs[col] -= matrix[rowBase + col] * displacement;
        }
    }
}
