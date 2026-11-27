using System;

namespace KhaozEngine.Attachments;

// Preconditioned conjugate gradients with a fixed factor and the current assembled matrix.
internal sealed class ClothMaterialReuseSolver(ClothSparseFactor factor, ClothMaterialOperator current, int size)
{
    internal const int CorrectionLimit = 16;
    private const double AbsoluteResidualLimit = 1e-12;
    private const double RelativeResidualLimit = 1e-6;

    private readonly double[] _residual = new double[size];
    private readonly double[] _preconditioned = new double[size];
    private readonly double[] _direction = new double[size];
    private readonly double[] _image = new double[size];
    private bool _hasFactor;

    internal double[] Solution { get; } = new double[size];
    internal long FactorProducts => factor.FactorProducts;

    internal void BeginTick() => _hasFactor = false;

    internal ClothStepStatus Solve(double[] matrix, double[] rhs, out ClothMaterialSolveWork work)
    {
        work = default;
        double rhsSquared = Dot(rhs, rhs, ref work);
        if (!double.IsFinite(rhsSquared)) return ClothStepStatus.NonfiniteState;
        double bound = AbsoluteResidualLimit + RelativeResidualLimit * Math.Sqrt(rhsSquared);
        if (!_hasFactor)
        {
            bool factored = factor.TryFactorAndSolve(matrix, rhs, Solution, out ClothMaterialSolveWork factorWork);
            work = work.Combine(factorWork);
            if (!factored) return ClothStepStatus.NonfiniteState;
            ClothStepStatus status = Certify(matrix, rhs, bound, ref work);
            _hasFactor = status == ClothStepStatus.Accepted;
            return status;
        }

        Array.Clear(Solution);
        if (rhsSquared <= bound * bound) return Certify(matrix, rhs, bound, ref work);
        rhs.CopyTo(_residual, 0);
        return Correct(matrix, rhs, bound, ref work);
    }

    private ClothStepStatus Correct(double[] matrix, double[] rhs, double bound, ref ClothMaterialSolveWork work)
    {
        double previousWeight = 0;
        for (int iteration = 0; iteration < CorrectionLimit; iteration++)
        {
            factor.Substitute(_residual, _preconditioned);
            work = work with
            {
                CorrectionIterations = work.CorrectionIterations + 1,
                SubstitutionProducts = work.SubstitutionProducts + factor.SubstitutionProducts,
            };
            double weight = Dot(_residual, _preconditioned, ref work);
            if (!PositiveFinite(weight)) return ClothStepStatus.NonfiniteState;
            if (previousWeight == 0)
                _preconditioned.CopyTo(_direction, 0);
            else
            {
                double scale = weight / previousWeight;
                if (!PositiveFinite(scale)) return ClothStepStatus.NonfiniteState;
                for (int i = 0; i < _direction.Length; i++)
                    _direction[i] = _preconditioned[i] + scale * _direction[i];
                work = work with { VectorProducts = work.VectorProducts + _direction.Length };
            }

            Apply(matrix, _direction, ref work);
            double curvature = Dot(_direction, _image, ref work);
            if (!PositiveFinite(curvature)) return ClothStepStatus.NonfiniteState;
            double step = weight / curvature;
            if (!PositiveFinite(step)) return ClothStepStatus.NonfiniteState;

            double residualSquared = 0;
            for (int i = 0; i < Solution.Length; i++)
            {
                Solution[i] += step * _direction[i];
                double residual = _residual[i] -= step * _image[i];
                residualSquared += residual * residual;
            }
            work = work with { VectorProducts = work.VectorProducts + 3L * Solution.Length };
            if (!double.IsFinite(residualSquared)) return ClothStepStatus.NonfiniteState;

            previousWeight = weight;
            if (residualSquared <= bound * bound || iteration + 1 == CorrectionLimit)
            {
                ClothStepStatus status = Certify(matrix, rhs, bound, ref work);
                if (status != ClothStepStatus.MaterialSolveWork) return status;
                // Restart from the explicit residual if the recurrence was too optimistic.
                previousWeight = 0;
            }
        }
        return ClothStepStatus.MaterialSolveWork;
    }

    private ClothStepStatus Certify(double[] matrix, double[] rhs, double bound, ref ClothMaterialSolveWork work)
    {
        Apply(matrix, Solution, ref work);
        double residualSquared = 0;
        for (int i = 0; i < Solution.Length; i++)
        {
            double residual = _residual[i] = rhs[i] - _image[i];
            residualSquared += residual * residual;
        }
        work = work with { ResidualChecks = work.ResidualChecks + 1, VectorProducts = work.VectorProducts + Solution.Length };
        if (!double.IsFinite(residualSquared)) return ClothStepStatus.NonfiniteState;
        double ratio = Math.Sqrt(residualSquared) / bound;
        if (ratio > 1) return ClothStepStatus.MaterialSolveWork;
        work = work with { MaxAcceptedResidualRatio = ratio };
        return ClothStepStatus.Accepted;
    }

    private void Apply(double[] matrix, double[] vector, ref ClothMaterialSolveWork work)
    {
        current.Apply(matrix, vector, _image);
        work = work with { OperatorProducts = work.OperatorProducts + current.Products };
    }

    private static double Dot(double[] left, double[] right, ref ClothMaterialSolveWork work)
    {
        double result = 0;
        for (int i = 0; i < left.Length; i++)
            result += left[i] * right[i];
        work = work with { VectorProducts = work.VectorProducts + left.Length };
        return result;
    }

    private static bool PositiveFinite(double value) => value > 0 && double.IsFinite(value);
}
