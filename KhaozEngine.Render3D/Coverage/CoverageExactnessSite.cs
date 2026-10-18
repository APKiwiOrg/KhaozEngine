namespace KhaozEngine.Render3D;

internal enum CoverageExactnessPhase
{
    None = 0, Skinning = 1, SourceValidation = 2, GroupInterpolation = 3, AttributeInterpolation = 4,
    Conformation = 5, FaceCorrection = 6, GroupCorrection = 7, FullTarget = 8, Transition = 9,
    FaceValidation = 10, NormalGather = 11
}

internal enum CoverageExactnessOperation
{
    None = 0, Add = 1, Subtract = 2, Multiply = 3, Divide = 4, SquareRoot = 5, Abs = 6, Negate = 7,
    Min = 8, Max = 9, Clamp = 10, Compare = 11, FiniteTest = 12, Predicate = 13, CrossingSide = 14, Store = 15
}

/// <summary>The first operand or result flushed to signed zero at or below the smallest normal.</summary>
internal readonly record struct CoverageExactnessSite(CoverageExactnessPhase Phase, int Element,
    CoverageExactnessOperation Operation, uint ValueBits);
