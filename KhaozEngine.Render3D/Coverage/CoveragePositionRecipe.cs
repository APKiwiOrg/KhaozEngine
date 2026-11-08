namespace KhaozEngine.Render3D;

/// <summary>Immutable integer position recipe over original posed source vertices.</summary>
internal readonly record struct CoveragePositionRecipe(int A, int B, int C, int WeightA, int WeightB, int WeightC, int Denominator);
