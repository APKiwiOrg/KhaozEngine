using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal readonly record struct ClothNodeGradient(int Node, Vector3 Direction);

// Linearized XPBD in node space. Eliminating the multiplier update gives
// (M + (1 + damping) J^T alpha^-1 J) dx = J^T alpha^-1 rhs.
// Pins contribute to the constraint rate but have no unknown displacement.
internal sealed class ClothMaterialSystem
{
    private readonly int[] _degrees;
    private readonly int[] _start;
    private readonly int[] _rowOffsets;
    private readonly double[] _matrix;
    private readonly double[] _rhs;
    private readonly float[] _mass;
    private float _damping;

    internal ClothMaterialSystem(ClothRig rig, ClothWorkLimits limits)
    {
        _degrees = new int[rig.RestPositions.Length];
        int size = 0;
        for (int node = 0; node < _degrees.Length; node++)
        {
            _degrees[node] = rig.InverseMasses[node] == 0 ? -1 : size;
            if (_degrees[node] >= 0) size += 3;
        }

        _mass = new float[size];
        _start = new int[size];
        _rowOffsets = new int[size + 1];
        _rhs = new double[size];
        for (int node = 0; node < _degrees.Length; node++)
        {
            int basis = _degrees[node];
            if (basis < 0) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                _mass[basis + axis] = 1 / rig.InverseMasses[node];
                _start[basis + axis] = basis;
            }
        }

        foreach (ClothTriangle triangle in rig.Triangles)
            Include(triangle.A, triangle.B, triangle.C);
        foreach (ClothHinge hinge in rig.Hinges)
            Include(hinge.First, hinge.Second, hinge.Left, hinge.Right);

        // Divide first so the per-substep limit can be checked without overflowing.
        long productsPerIteration = limits.MaxMaterialFactorProducts / limits.Iterations;
        long products = 0;
        for (int row = 0; row < size; row++)
        {
            _rowOffsets[row + 1] = checked(_rowOffsets[row] + row - _start[row] + 1);
            for (int col = _start[row]; col <= row; col++)
                products += col - Math.Max(_start[row], _start[col]);
            if (products > productsPerIteration)
                throw new ArgumentOutOfRangeException(nameof(limits), limits,
                    "The cloth material system exceeds its per-substep factor work limit.");
        }

        FactorProducts = products;
        _matrix = new double[_rowOffsets[^1]];
    }

    internal long FactorProducts { get; }

    internal void Begin(float damping)
    {
        _damping = damping;
        Array.Clear(_matrix);
        Array.Clear(_rhs);
        for (int i = 0; i < _mass.Length; i++)
            _matrix[_rowOffsets[i + 1] - 1] = _mass[i];
    }

    internal static float DirectionalDisplacement(ReadOnlySpan<ClothNodeGradient> gradient,
        Vector3[] positions, Vector3[] opening)
    {
        float result = 0;
        foreach (ClothNodeGradient term in gradient)
            result += Vector3.Dot(term.Direction, positions[term.Node] - opening[term.Node]);
        return result;
    }

    internal void Add(ReadOnlySpan<ClothNodeGradient> gradient, float inverseAlpha, float rhs)
    {
        Span<int> dofs = stackalloc int[12];
        Span<float> values = stackalloc float[12];
        int count = 0;
        foreach (ClothNodeGradient term in gradient)
        {
            int basis = _degrees[term.Node];
            if (basis < 0) continue;
            dofs[count] = basis;
            values[count++] = term.Direction.X;
            dofs[count] = basis + 1;
            values[count++] = term.Direction.Y;
            dofs[count] = basis + 2;
            values[count++] = term.Direction.Z;
        }

        double stiffness = (1d + _damping) * inverseAlpha;
        for (int i = 0; i < count; i++)
        {
            int left = dofs[i];
            double value = values[i];
            _rhs[left] += inverseAlpha * value * rhs;
            double scaled = stiffness * value;
            for (int j = 0; j <= i; j++)
            {
                int row = Math.Max(left, dofs[j]), col = Math.Min(left, dofs[j]);
                _matrix[_rowOffsets[row] + col - _start[row]] += scaled * values[j];
            }
        }
    }

    internal Vector3 Delta(int node)
    {
        int basis = _degrees[node];
        return basis < 0 ? Vector3.Zero : new Vector3((float)_rhs[basis], (float)_rhs[basis + 1], (float)_rhs[basis + 2]);
    }

    internal bool Solve()
    {
        // In-place skyline Cholesky followed by forward and backward substitution.
        double[] matrix = _matrix, rhs = _rhs;
        int[] start = _start, offsets = _rowOffsets;
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
                    if (entry <= 0 || !double.IsFinite(entry)) return false;
                    matrix[rowBase + row] = Math.Sqrt(entry);
                }
                else
                    matrix[rowBase + col] = entry / matrix[colBase + col];
            }

            double load = rhs[row];
            for (int col = rowStart; col < row; col++)
                load -= rhs[col] * matrix[rowBase + col];
            rhs[row] = load / matrix[rowBase + row];
        }

        for (int row = rhs.Length - 1; row >= 0; row--)
        {
            int rowStart = start[row];
            int rowBase = offsets[row] - rowStart;
            double displacement = rhs[row] /= matrix[rowBase + row];
            for (int col = rowStart; col < row; col++)
                rhs[col] -= matrix[rowBase + col] * displacement;
        }
        return true;
    }

    private void Include(int a, int b, int c, int d = -1)
    {
        Span<int> nodes = stackalloc int[4] { a, b, c, d };
        int minimum = _degrees.Length * 3;
        foreach (int node in nodes)
            if (node >= 0 && _degrees[node] >= 0)
                minimum = Math.Min(minimum, _degrees[node]);
        foreach (int node in nodes)
        {
            if (node < 0 || _degrees[node] < 0) continue;
            for (int axis = 0; axis < 3; axis++)
                _start[_degrees[node] + axis] = Math.Min(_start[_degrees[node] + axis], minimum);
        }
    }
}

internal readonly record struct ClothMaterialRow(ClothNodeGradient A, ClothNodeGradient B, ClothNodeGradient C,
    ClothNodeGradient D, int NodeCount, float InverseAlpha, float RightHandSide)
{
    internal static ClothMaterialRow Add(ClothMaterialSystem system, ReadOnlySpan<ClothNodeGradient> gradient,
        Vector3[] positions, Vector3[] opening, float alpha, float lambda, float damping, float error)
    {
        float rhs = -error - alpha * lambda - damping * ClothMaterialSystem.DirectionalDisplacement(gradient, positions, opening);
        float inverseAlpha = 1 / alpha;
        system.Add(gradient, inverseAlpha, rhs);
        return new ClothMaterialRow(gradient[0], gradient[1], gradient[2], gradient.Length == 4 ? gradient[3] : default,
            gradient.Length, inverseAlpha, rhs);
    }

    internal float UpdateMultiplier(ClothMaterialSystem system, float damping)
    {
        if (NodeCount == 0) return 0;
        float change = Vector3.Dot(A.Direction, system.Delta(A.Node)) + Vector3.Dot(B.Direction, system.Delta(B.Node)) +
            Vector3.Dot(C.Direction, system.Delta(C.Node));
        if (NodeCount == 4)
            change += Vector3.Dot(D.Direction, system.Delta(D.Node));
        return InverseAlpha * (RightHandSide - (1 + damping) * change);
    }
}
