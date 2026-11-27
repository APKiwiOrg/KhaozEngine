using System;

namespace KhaozEngine.Attachments;

internal readonly record struct ClothMaterialSolveWork(int Factorizations = 0, long FactorProducts = 0,
    int CorrectionIterations = 0, int ResidualChecks = 0, long OperatorProducts = 0,
    long SubstitutionProducts = 0, long VectorProducts = 0, double MaxAcceptedResidualRatio = 0)
{
    internal ClothMaterialSolveWork Combine(ClothMaterialSolveWork other) => new(
        Factorizations + other.Factorizations, FactorProducts + other.FactorProducts,
        CorrectionIterations + other.CorrectionIterations, ResidualChecks + other.ResidualChecks,
        OperatorProducts + other.OperatorProducts, SubstitutionProducts + other.SubstitutionProducts,
        VectorProducts + other.VectorProducts, Math.Max(MaxAcceptedResidualRatio, other.MaxAcceptedResidualRatio));
}
