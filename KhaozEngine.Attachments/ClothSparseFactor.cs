using System;

namespace KhaozEngine.Attachments;

// Right-looking Cholesky in packed columns. The ordered graph fixes all fill at bind time.
internal sealed class ClothSparseFactor
{
    private const int BlockSize = 3;
    private readonly int[] _originalDegrees;
    private readonly int[] _columnOffsets;
    private readonly int[] _rows;
    private readonly int[] _matrixEntries;
    // A packed coordinate map stores the relative row offset within each factor column.
    // Each column is at most three entries per cloth node, including its diagonal.
    private readonly ushort[] _relativeEntries;
    private readonly double[] _values;
    private readonly double[] _permuted;

    internal ClothSparseFactor(bool[,] connected, int[] start, int[] matrixOffsets, ClothWorkLimits limits)
    {
        int[][] laterBlocks = ClothSparseOrdering.Fill(connected, out int[] originalBlocks);
        int size = start.Length;
        _columnOffsets = new int[size + 1];
        long products = 0;
        for (int column = 0; column < size; column++)
        {
            int below = BlockSize * (1 + laterBlocks[column / BlockSize].Length) - 1 - column % BlockSize;
            _columnOffsets[column + 1] = _columnOffsets[column] + 1 + below;
            products += ColumnProducts(below);
            if (products > limits.MaxMaterialFactorProducts)
                throw new ArgumentOutOfRangeException(nameof(limits), limits,
                    "The cloth material system exceeds its per-substep factor work limit.");
        }

        int entries = _columnOffsets[^1];
        _originalDegrees = new int[size];
        _rows = new int[entries];
        _matrixEntries = new int[entries];
        _relativeEntries = new ushort[size * (size + 1) / 2];
        _values = new double[entries];
        _permuted = new double[size];
        for (int index = 0; index < size; index++)
            _originalDegrees[index] = BlockSize * originalBlocks[index / BlockSize] + index % BlockSize;

        for (int column = 0; column < size; column++)
        {
            int block = column / BlockSize;
            int entry = _columnOffsets[column];
            for (int row = column; row < BlockSize * (block + 1); row++) BindRow(row);
            foreach (int later in laterBlocks[block])
                for (int axis = 0; axis < BlockSize; axis++) BindRow(BlockSize * later + axis);

            void BindRow(int row)
            {
                _rows[entry] = row;
                _relativeEntries[MapBase(column) + row] = checked((ushort)(entry - _columnOffsets[column]));
                int originalRow = Math.Max(_originalDegrees[row], _originalDegrees[column]);
                int originalColumn = Math.Min(_originalDegrees[row], _originalDegrees[column]);
                bool assembled = originalRow / BlockSize == originalColumn / BlockSize ||
                    connected[originalRow / BlockSize, originalColumn / BlockSize];
                _matrixEntries[entry++] = assembled && originalColumn >= start[originalRow]
                    ? matrixOffsets[originalRow] + originalColumn - start[originalRow]
                    : -1;
            }
        }

        FactorProducts = products;
        SubstitutionProducts = 2L * (entries - size);
    }

    internal long FactorProducts { get; }
    internal long SubstitutionProducts { get; }

    internal bool TryFactorAndSolve(double[] matrix, double[] load, double[] solution, out ClothMaterialSolveWork work)
    {
        for (int entry = 0; entry < _values.Length; entry++)
        {
            int source = _matrixEntries[entry];
            _values[entry] = source >= 0 ? matrix[source] : 0;
        }

        long products = 0;
        for (int column = 0; column < _originalDegrees.Length; column++)
        {
            if (!FactorColumn(column, ref products))
            {
                work = new(Factorizations: 1, FactorProducts: products);
                return false;
            }
        }

        Substitute(load, solution);
        work = new(Factorizations: 1, FactorProducts: products, SubstitutionProducts: SubstitutionProducts);
        return true;
    }

    private bool FactorColumn(int column, ref long products)
    {
        int start = _columnOffsets[column], end = _columnOffsets[column + 1];
        double pivot = _values[start];
        if (pivot <= 0 || !double.IsFinite(pivot)) return false;

        double diagonal = _values[start] = Math.Sqrt(pivot);
        for (int entry = start + 1; entry < end; entry++) _values[entry] /= diagonal;

        // Every pair of rows below this pivot is in the filled graph.
        for (int left = start + 1; left < end; left++)
        {
            int targetColumn = _rows[left];
            int target = _columnOffsets[targetColumn];
            int mapBase = MapBase(targetColumn);
            double scale = _values[left];
            for (int right = left; right < end; right++)
                _values[target + _relativeEntries[mapBase + _rows[right]]] -= scale * _values[right];
        }

        products += ColumnProducts(end - start - 1);
        return true;
    }

    internal void Substitute(double[] load, double[] solution)
    {
        for (int i = 0; i < _permuted.Length; i++) _permuted[i] = load[_originalDegrees[i]];
        for (int column = 0; column < _permuted.Length; column++)
        {
            int start = _columnOffsets[column], end = _columnOffsets[column + 1];
            double result = _permuted[column] /= _values[start];
            for (int entry = start + 1; entry < end; entry++)
                _permuted[_rows[entry]] -= _values[entry] * result;
        }

        for (int column = _permuted.Length - 1; column >= 0; column--)
        {
            int start = _columnOffsets[column], end = _columnOffsets[column + 1];
            double result = _permuted[column];
            for (int entry = start + 1; entry < end; entry++)
                result -= _values[entry] * _permuted[_rows[entry]];
            result /= _values[start];
            _permuted[column] = result;
            solution[_originalDegrees[column]] = result;
        }
    }

    private int MapBase(int column) => column * _permuted.Length - column * (column + 1) / 2;

    private static long ColumnProducts(int below) => (long)below * (below + 1) / 2;
}
