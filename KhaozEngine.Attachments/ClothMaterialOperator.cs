using System.Collections.Generic;

namespace KhaozEngine.Attachments;

// The current assembled matrix has structural zeros inside the factor's skyline.
// Triangle and hinge node pairs determine which entries assembly can write.
internal sealed class ClothMaterialOperator
{
    private readonly int[] _start;
    private readonly int[] _skylineOffsets;
    private readonly int[] _operatorOffsets;
    private readonly int[] _columns;

    internal ClothMaterialOperator(bool[,] connectedNodes, int[] start, int[] skylineOffsets)
    {
        _start = start;
        _skylineOffsets = skylineOffsets;
        _operatorOffsets = new int[skylineOffsets.Length];
        List<int> columns = [];
        for (int row = 0; row < start.Length; row++)
        {
            for (int col = start[row]; col < row; col++)
                if (connectedNodes[row / 3, col / 3]) columns.Add(col);
            _operatorOffsets[row + 1] = columns.Count;
        }
        _columns = columns.ToArray();
        Products = start.Length + 2L * _columns.Length;
    }

    internal long Products { get; }

    internal void Apply(double[] matrix, double[] vector, double[] product)
    {
        for (int row = 0; row < vector.Length; row++)
        {
            int rowBase = _skylineOffsets[row] - _start[row];
            double value = vector[row];
            double load = matrix[rowBase + row] * value;
            for (int entry = _operatorOffsets[row]; entry < _operatorOffsets[row + 1]; entry++)
            {
                int col = _columns[entry];
                double coefficient = matrix[rowBase + col];
                load += coefficient * vector[col];
                product[col] += coefficient * value;
            }
            product[row] = load;
        }
    }
}
